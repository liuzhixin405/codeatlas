using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Hashing;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Server;

/// <param name="Depth">往上走的层数上限（1..<see cref="ImpactBuilder.MaxDepth"/>）。</param>
/// <param name="TestGlobs">自定义测试识别：路径匹配任一 glob 的才算测试（取代默认规则 <see cref="PipelineBuilder.IsTest"/>）。null / 空 = 默认规则。</param>
public sealed record AffectedOptions(int Depth = AffectedQuery.DefaultDepth, IReadOnlyList<string>? TestGlobs = null);

/// <summary>
/// 改动影响分析（codeatlas affected、GET /api/affected）：改动行 → 改动的符号（种子）→ 往上走到的入口与测试。
/// <list type="number">
/// <item><b>行 → 符号</b>：用新侧行号（diff 是 base → 工作区，索引反映工作区）。每个改动行取包含它的最内层"单元"符号——
/// 方法 / 函数 / 属性 / 类型（类、结构、接口、枚举、类型别名）/ 顶层变量；字段算到所属类型上，模块、命名空间不算。
/// 纯删除（+c,0）取同时包含 c 与 c+1 两行的最内层单元（删掉整个方法 → 所在的类）。只有空白的改动块忽略。</item>
/// <item><b>文件级退化</b>：有改动行不在任何单元里（using / import、文件顶部、顶层语句之外），或新文件、二进制、文件名列表输入、
/// 已删除但索引里还有（索引过期）——这个文件里的单元全部当种子，文件标 Analysis = file、行在 Outside 里。</item>
/// <item><b>跳过</b>（Analysis = skipped，Note 写原因）：不在索引里（非源码 / 被跳过 / 新文件还没索引）、已删除且索引里没有、只改名、只有空白改动。</item>
/// <item><b>往上走</b>：与影响范围（<see cref="ImpactQuery"/>）同一套入边规则（calls / impl / dispatch / new / 跨服务 hop；
/// 置信度 ≥ <see cref="ImpactBuilder.WalkThreshold"/>，分派边照走）；类型 / 枚举 / 别名 / 顶层变量种子另加 references 入边（被当参数 / 字段类型用的地方）作第一跳。
/// 测试代码只往测试代码走（测试辅助方法 → 测试方法），不会经测试走回产品代码，所以产品代码里走到的入口与侧边栏「受影响的入口」一致。
/// 每个种子单独走（结果能归到种子上），入边按符号缓存，多个种子共享。<see cref="AffectedOptions.Depth"/> 层、<see cref="ImpactBuilder.MaxNodes"/> 个节点封顶。</item>
/// <item><b>置信度</b>：每个走到的符号取 max-min（所有路径里最弱一条边最强的那条）；低于 0.7 的在扩展里标"推测"。</item>
/// <item><b>测试</b>：走到的测试代码里的符号 + 本身就改了的测试符号（深度 0）；TestFiles = 它们所在文件 ∪ 改动的测试文件（已删除的不算）。</item>
/// </list>
/// </summary>
public static class AffectedQuery
{
    /// <summary>
    /// 默认层数：与影响范围侧边栏的入口汇总同一上限（<see cref="ImpactBuilder.MaxDepth"/>），同一个方法在两处看到的入口一致。
    /// 符号级的一跳比 codegraph 文件级 import 的一跳细得多（一次 HTTP 请求到仓储方法常常就 4–6 跳），沿用它的 5 会漏掉深处的入口；CI 想收窄就显式给 --depth。
    /// </summary>
    public const int DefaultDepth = ImpactBuilder.MaxDepth;

    /// <summary>索引支持的源码扩展名（与 LanguageAdapters 一致）：不在索引里时区分"非源码"与"源码但没索引"。</summary>
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".mts", ".cts", ".tsx", ".js", ".mjs", ".cjs", ".jsx", ".go", ".py", ".java" };

    private const string RefKind = "ref";

    /// <summary>git 模式：跑 git diff（见 <see cref="AffectedGit"/>）再分析。git 不可用、base 不存在时返回 Error。</summary>
    public static (AffectedDto? Result, string? Error) FromGit(IndexDatabase db, string repoDir, string? baseRef, AffectedOptions options)
    {
        var b = string.IsNullOrWhiteSpace(baseRef) ? AffectedGit.DefaultBase : baseRef.Trim();
        var git = AffectedGit.Collect(repoDir, b);
        if (git.Error is not null) return (null, git.Error);
        return (Build(db, repoDir, git.Changes, "git", b, git.MergeBase, options), null);
    }

    /// <summary>--stdin 模式：自动识别统一 diff 或文件名列表。</summary>
    public static AffectedDto FromText(IndexDatabase db, string repoDir, string text, AffectedOptions options)
    {
        var isDiff = AffectedDiff.LooksLikeDiff(text);
        IReadOnlyList<FileChange> changes = isDiff ? AffectedDiff.ParseDiff(text) : AffectedDiff.ParseFileList(text);
        changes = AffectedGit.RelativeToRepo(repoDir, changes);
        return Build(db, repoDir, changes, isDiff ? "diff" : "files", null, null, options);
    }

    /// <param name="repoDir">仓库主根（判断文件是否还在、索引是否过期用）；null 时不查磁盘（单测）。</param>
    public static AffectedDto Build(IndexDatabase db, string? repoDir, IReadOnlyList<FileChange> changes, string input, string? baseRef, string? mergeBase, AffectedOptions options)
    {
        var depthLimit = Math.Clamp(options.Depth, 1, ImpactBuilder.MaxDepth);
        var isTest = TestPredicate(options.TestGlobs);
        var g = new ImpactQuery.Graph(db);
        var reader = new IndexReader(db);

        // ---- 1. 文件 → 种子 ----
        var files = new List<AffectedFileDto>();
        var seeds = new Dictionary<long, Seed>();
        var changedTestFiles = new List<string>();
        var stale = new List<string>();
        foreach (var change in changes.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            var file = FindFile(db, change.Path);
            var full = repoDir is null ? null : Path.Combine(repoDir, change.Path.Replace('/', Path.DirectorySeparatorChar));
            var status = change.Status;
            // 文件名列表不知道状态：磁盘上没有了就是删了
            if (change.Hunks is null && status == DiffStatus.Modified && full is not null && !File.Exists(full)) status = DiffStatus.Deleted;
            var fileIsTest = isTest(change.Path, "");
            if (fileIsTest && status != DiffStatus.Deleted) changedTestFiles.Add(change.Path);
            var ranges = (change.Hunks ?? []).Select(h => new AffectedRangeDto(h.NewStart, h.NewCount)).ToList();
            var statusText = status.ToString().ToLowerInvariant();

            AffectedFileDto Skip(string note, bool isStale = false) => new(change.Path, change.OldPath, statusText, "skipped", ranges, [], 0, fileIsTest, isStale, note);

            if (file is null)
            {
                var note = status == DiffStatus.Deleted
                    ? L("已删除，索引里没有它的符号（索引反映工作区）；原来调用它的地方要是跟着改了，会按那些改动算", "Deleted; the index has no symbols for it (the index reflects the work tree); callers that were changed along with it are analyzed as changes")
                    : SourceExtensions.Contains(Path.GetExtension(change.Path))
                        ? L("源码文件不在索引里（新文件还没索引、被跳过，或在索引范围外）：先运行 codeatlas index", "Source file is not in the index (new and not indexed yet, skipped, or outside the indexed roots): run codeatlas index first")
                        : L("非源码文件，不在索引里", "Not a source file; not in the index");
                files.Add(Skip(note));
                continue;
            }
            if (file.Status != FileStatus.Ok)
            {
                files.Add(Skip(file.Status == FileStatus.Skipped ? L("索引时被跳过（超过 2MB）", "Skipped at index time (larger than 2 MB)") : L("索引时解析失败", "Failed to parse at index time")));
                continue;
            }
            var isStale = status != DiffStatus.Deleted && full is not null && IsStale(file, full);
            if (isStale) stale.Add(change.Path);
            var symbols = reader.GetSymbolsInFile(file.Id).ToList();
            var units = symbols.Where(s => IsUnit(s, symbols)).ToList();

            string? fileNote = null;
            var outside = new List<int>();
            var hit = new Dictionary<long, SortedSet<int>>();
            if (status == DiffStatus.Deleted)
                fileNote = L("已删除，但索引里还有（索引过期）：按索引里的旧内容文件级处理", "Deleted, but still in the index (stale index): analyzed at file level using the old indexed content");
            else if (change.Binary) fileNote = L("二进制改动：文件级", "Binary change: file level");
            else if (change.Hunks is null) fileNote = status == DiffStatus.Added ? L("新文件：文件级", "New file: file level") : L("只有文件名：文件级", "File name only: file level");
            else if (status == DiffStatus.Added) fileNote = L("新文件：文件级", "New file: file level");
            else
            {
                var real = change.Hunks.Where(h => !h.Blank).ToList();
                if (change.Hunks.Count == 0 && status == DiffStatus.Renamed) { files.Add(Skip(L($"只改名（{change.OldPath} →），内容未变", $"Rename only (from {change.OldPath}); content unchanged"), isStale)); continue; }
                if (real.Count == 0) { files.Add(Skip(L("只有空白改动", "Whitespace-only changes"), isStale)); continue; }
                foreach (var h in real)
                {
                    if (h.NewCount == 0)
                    {
                        // 纯删除：删在 c 行之后，取同时包含 c 与 c+1 的最内层单元
                        if (Innermost(units, h.NewStart, h.NewStart + 1) is { } u) Add(hit, u.Id, h.NewStart + 1);
                        else outside.Add(h.NewStart + 1);
                        continue;
                    }
                    for (var line = h.NewStart; line < h.NewStart + h.NewCount; line++)
                        if (Innermost(units, line, line) is { } u) Add(hit, u.Id, line);
                        else outside.Add(line);
                }
                if (outside.Count > 0)
                    fileNote = L($"改动落在符号之外（{LineList(outside)}：import / using、文件顶部等）：文件级", $"Changes outside any symbol ({LineList(outside)}: import / using, top of file, …): file level");
            }

            var fileLevel = fileNote is not null; // 上面只有退化为文件级的情形才写 fileNote
            var contributed = 0;
            foreach (var u in units)
            {
                var lines = hit.TryGetValue(u.Id, out var set) ? set.ToList() : null;
                if (lines is null && !fileLevel) continue;
                contributed++;
                if (seeds.TryGetValue(u.Id, out var prev)) { if (lines is not null) prev.Lines.UnionWith(lines); continue; }
                seeds[u.Id] = new Seed(u, lines is null ? "file" : "lines", [.. lines ?? []]);
            }
            files.Add(new AffectedFileDto(change.Path, change.OldPath, statusText, fileLevel ? "file" : "symbols", ranges, outside, contributed, fileIsTest, isStale, fileNote));
        }

        // ---- 2. 每个种子往上走 ----
        var (entriesBySymbol, matchesByEntry) = ImpactQuery.LoadEntriesFor(db);
        var containerNames = ContainerNames(db);
        var walker = new Walker(db, g, matchesByEntry, isTest, depthLimit);
        g.Load(seeds.Keys.ToList(), withParents: true);
        var symbolDtos = new List<AffectedSymbolDto>();
        var anyTruncated = false;
        foreach (var seed in seeds.Values.OrderBy(s => g.PathOf(s.Symbol), StringComparer.Ordinal).ThenBy(s => s.Symbol.StartLine).ThenBy(s => s.Symbol.Id))
        {
            var walk = walker.Walk(seed.Symbol);
            anyTruncated |= walk.Truncated;
            var entries = new List<AffectedEntryDto>();
            var tests = new List<AffectedTestDto>();
            var callers = 0;
            foreach (var (id, d) in walk.Depths)
            {
                if (g.Get(id) is not { } s) continue;
                var conf = Math.Round(walk.Confidence.GetValueOrDefault(id), 2);
                if (walker.IsTestNode(id))
                {
                    tests.Add(new AffectedTestDto(ImpactBuilder.ToDto(s, g.PathOf(s)), OutlineBuilder.SymbolLabel(s, g.Symbols, g.Paths), d, conf, [seed.Symbol.Id]));
                    continue;
                }
                if (id != seed.Symbol.Id) callers++;
                foreach (var e in entriesBySymbol[id])
                    entries.Add(new AffectedEntryDto(new EntryDto(e.SymbolId, e.Kind, e.RuleId, e.Route, e.HttpMethod, s.Name, s.QualifiedName, e.File, e.Line),
                        e.ContainerKey, containerNames.GetValueOrDefault(e.ContainerKey, e.ContainerKey), d, conf, [seed.Symbol.Id]));
            }
            symbolDtos.Add(new AffectedSymbolDto(ImpactBuilder.ToDto(seed.Symbol, g.PathOf(seed.Symbol)), OutlineBuilder.SymbolLabel(seed.Symbol, g.Symbols, g.Paths),
                seed.Reason, seed.Lines.ToList(), walker.IsTestNode(seed.Symbol.Id), SortEntries(entries), SortTests(tests), callers, walk.Truncated));
        }

        // ---- 3. 合并 ----
        var entryUnion = symbolDtos.SelectMany(s => s.Entries)
            .GroupBy(e => (e.Entry.SymbolId, e.Entry.Kind, e.Entry.Route, e.Entry.HttpMethod, e.ContainerKey))
            .Select(gr => gr.First() with { Depth = gr.Min(e => e.Depth), Confidence = gr.Max(e => e.Confidence), From = gr.SelectMany(e => e.From).Distinct().Order().ToList() })
            .ToList();
        var testUnion = symbolDtos.SelectMany(s => s.Tests)
            .GroupBy(t => t.Symbol.Id)
            .Select(gr => gr.First() with { Depth = gr.Min(t => t.Depth), Confidence = gr.Max(t => t.Confidence), From = gr.SelectMany(t => t.From).Distinct().Order().ToList() })
            .ToList();
        var testFiles = testUnion.Select(t => t.Symbol.Path).Concat(changedTestFiles)
            .Where(p => p != IndexWriter.ExternalFilePath && (repoDir is null || File.Exists(Path.Combine(repoDir, p))))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        var warnings = new List<string>();
        if (stale.Count > 0)
            warnings.Add(L($"索引比工作区旧：{stale.Count} 个改动文件在索引之后又改过（{string.Join("、", stale.Take(5))}{(stale.Count > 5 ? " 等" : "")}），行号可能对不上——先运行 codeatlas index",
                $"The index is older than the work tree: {stale.Count} changed {(stale.Count == 1 ? "file was" : "files were")} modified after indexing ({string.Join(", ", stale.Take(5))}{(stale.Count > 5 ? ", …" : "")}); line numbers may not match — run codeatlas index first"));
        if (anyTruncated)
            warnings.Add(L($"有改动的符号往上超过 {depthLimit} 层或 {ImpactBuilder.MaxNodes} 个节点，入口与测试可能不全", $"Some changed symbols exceed {depthLimit} levels or {ImpactBuilder.MaxNodes} nodes upward; entries and tests may be incomplete"));

        return new AffectedDto(baseRef, mergeBase, input, files, symbolDtos, SortEntries(entryUnion), SortTests(testUnion), testFiles, depthLimit, anyTruncated, warnings);
    }

    private sealed record Seed(SymbolRecord Symbol, string Reason, SortedSet<int> Lines);

    private static void Add(Dictionary<long, SortedSet<int>> hit, long id, int line)
    {
        if (!hit.TryGetValue(id, out var set)) hit[id] = set = [];
        set.Add(line);
    }

    /// <summary>"L3, L7-9" 这样的行号列表（连续的合并）。</summary>
    public static string LineList(IEnumerable<int> lines)
    {
        var sorted = lines.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(i == j ? $"L{sorted[i]}" : $"L{sorted[i]}-{sorted[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    /// <summary>能当种子的"单元"：方法 / 函数 / 属性 / 类型 / 顶层变量（父符号不是方法 / 函数 / 类型）。字段、模块、命名空间、外部符号不算。</summary>
    internal static bool IsUnit(SymbolRecord s, IReadOnlyList<SymbolRecord> fileSymbols)
    {
        switch (s.Kind)
        {
            case SymbolKind.Method or SymbolKind.Function or SymbolKind.Property:
            case SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum or SymbolKind.TypeAlias:
                return true;
            case SymbolKind.Variable:
                return s.ParentId is not { } p || fileSymbols.FirstOrDefault(x => x.Id == p) is not { } parent || parent.Kind is SymbolKind.Module or SymbolKind.Namespace;
            default:
                return false;
        }
    }

    /// <summary>同时包含 from..to 两行的最内层单元（跨度最小；同跨度取起点靠后的）。</summary>
    internal static SymbolRecord? Innermost(IReadOnlyList<SymbolRecord> units, int from, int to) =>
        units.Where(u => u.StartLine <= from && to <= u.EndLine)
            .OrderBy(u => u.EndLine - u.StartLine).ThenByDescending(u => u.StartLine).ThenBy(u => u.Id).FirstOrDefault();

    /// <summary>按路径找文件：先精确，Windows / macOS 上再忽略大小写（git 输出的大小写和扫描时可能不一致）。</summary>
    private static FileRecord? FindFile(IndexDatabase db, string path)
    {
        var reader = new IndexReader(db);
        if (reader.GetFile(path) is { } exact) return exact;
        if (OperatingSystem.IsLinux()) return null;
        var id = db.Query<long>("SELECT id FROM files WHERE path = $p COLLATE NOCASE LIMIT 1", ("$p", path)).Cast<long?>().FirstOrDefault();
        return id is { } i ? reader.GetFileById(i) : null;
    }

    /// <summary>索引里的版本与磁盘上的不一样：大小不同直接算；大小相同但修改时间不同（或没记）时比内容哈希（checkout 会改修改时间但内容不变）。</summary>
    /// <summary>
    /// 与视图 / MCP 同一口径（<see cref="StaleFileCheck.Classify"/>）。改动文件一般不多，不设哈希预算；
    /// 文件已删除由 diff 状态另行处理，这里不算过期。
    /// </summary>
    private static bool IsStale(FileRecord file, string fullPath)
    {
        var unlimited = int.MaxValue;
        return StaleFileCheck.Classify(file, fullPath, ref unlimited) is not (Freshness.Fresh or Freshness.Missing);
    }

    /// <summary>测试判定：给了 glob 就只认 glob（匹配路径），否则用默认规则（路径或限定名里有 Tests / Test / Spec 段等）。</summary>
    internal static Func<string, string, bool> TestPredicate(IReadOnlyList<string>? globs)
    {
        if (globs is null || globs.Count == 0) return PipelineBuilder.IsTest;
        var regexes = globs.Select(GlobRegex).ToList();
        return (path, _) => regexes.Any(r => r.IsMatch(path.Replace('\\', '/')));
    }

    /// <summary>
    /// glob → 正则：** 跨目录（**/ 可匹配零层），* 与 ? 不跨 /，{a,b} 多选，[...] 字符类；不含 / 的 glob 匹配任意目录下的文件名（与 .gitignore 的习惯一致）。
    /// Windows / macOS 上忽略大小写。
    /// </summary>
    public static Regex GlobRegex(string glob)
    {
        var g = glob.Replace('\\', '/').Trim();
        if (g.StartsWith("./", StringComparison.Ordinal)) g = g[2..];
        var anyDir = !g.Contains('/');
        var sb = new System.Text.StringBuilder("^");
        if (anyDir) sb.Append("(?:.*/)?");
        var braces = 0;
        for (var i = 0; i < g.Length; i++)
        {
            var c = g[i];
            switch (c)
            {
                case '*' when i + 1 < g.Length && g[i + 1] == '*':
                    i++;
                    if (i + 1 < g.Length && g[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                    else sb.Append(".*");
                    break;
                case '*': sb.Append("[^/]*"); break;
                case '?': sb.Append("[^/]"); break;
                case '{': braces++; sb.Append("(?:"); break;
                case '}' when braces > 0: braces--; sb.Append(')'); break;
                case ',' when braces > 0: sb.Append('|'); break;
                case '[':
                    var close = g.IndexOf(']', i + 1);
                    if (close < 0) { sb.Append(@"\["); break; }
                    var cls = g[(i + 1)..close];
                    if (cls.StartsWith('!')) cls = "^" + cls[1..];
                    sb.Append('[').Append(cls.Replace(@"\", @"\\")).Append(']');
                    i = close;
                    break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), OperatingSystem.IsLinux() ? RegexOptions.CultureInvariant : RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static List<AffectedEntryDto> SortEntries(IEnumerable<AffectedEntryDto> entries) => entries
        .OrderBy(e => e.Depth)
        .ThenBy(e => e.Entry.Kind, StringComparer.Ordinal)
        .ThenBy(e => e.Entry.Route ?? "", StringComparer.Ordinal)
        .ThenBy(e => e.ContainerName, StringComparer.Ordinal)
        .ThenBy(e => e.Entry.Name, StringComparer.Ordinal)
        .ThenBy(e => e.Entry.SymbolId)
        .ToList();

    private static List<AffectedTestDto> SortTests(IEnumerable<AffectedTestDto> tests) => tests
        .OrderBy(t => t.Symbol.Path, StringComparer.Ordinal).ThenBy(t => t.Symbol.StartLine).ThenBy(t => t.Symbol.Id).ToList();

    private static Dictionary<string, string> ContainerNames(IndexDatabase db)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = db.CreateCommand("SELECT key, name FROM arch_nodes");
        using var r = cmd.ExecuteReader();
        while (r.Read()) names[r.GetString(0)] = r.GetString(1);
        return names;
    }

    /// <summary>往上走（规则见类注释第 4、5 条）。入边按符号缓存，多个种子共用。</summary>
    private sealed class Walker(IndexDatabase db, ImpactQuery.Graph g, ILookup<long, RouteMatch> matchesByEntry, Func<string, string, bool> isTest, int maxDepth)
    {
        private readonly Dictionary<long, List<ImpactLink>> _up = [];
        private readonly Dictionary<long, bool> _test = [];

        public bool IsTestNode(long id)
        {
            if (_test.TryGetValue(id, out var t)) return t;
            if (g.Get(id) is not { } s) return false;
            return _test[id] = isTest(g.PathOf(s), s.QualifiedName);
        }

        public sealed record WalkResult(Dictionary<long, int> Depths, Dictionary<long, double> Confidence, bool Truncated);

        public WalkResult Walk(SymbolRecord seed)
        {
            var depths = new Dictionary<long, int> { [seed.Id] = 0 };
            var links = new List<(long Target, long Caller, double Confidence)>();
            var frontier = new List<long> { seed.Id };
            var truncated = false;
            for (var depth = 0; frontier.Count > 0; depth++)
            {
                Ensure(frontier);
                var next = new List<long>();
                foreach (var t in frontier)
                {
                    var up = _up[t];
                    IEnumerable<ImpactLink> all = t == seed.Id && UsesReferences(seed) ? up.Concat(References(seed.Id)) : up;
                    foreach (var l in all)
                    {
                        var c = l.CallerId;
                        if (c == t) continue;
                        if (IsTestNode(t) && !IsTestNode(c)) continue; // 测试代码不走回产品代码
                        if (depths.ContainsKey(c)) { links.Add((t, c, l.Confidence)); continue; }
                        if (depth + 1 > maxDepth || depths.Count >= ImpactBuilder.MaxNodes) { truncated = true; continue; }
                        links.Add((t, c, l.Confidence));
                        depths[c] = depth + 1;
                        next.Add(c);
                    }
                }
                frontier = next;
            }
            return new WalkResult(depths, MaxMin(seed.Id, depths.Keys, links), truncated);
        }

        /// <summary>一批符号的上一层（与入口汇总同一规则：置信度 ≥ 0.5 或分派，加跨服务 hop），调用方连父符号一起加载（拼显示名、判测试用）。</summary>
        private void Ensure(IReadOnlyCollection<long> ids)
        {
            var missing = ids.Where(i => !_up.ContainsKey(i)).Distinct().ToList();
            if (missing.Count == 0) return;
            foreach (var m in missing) _up[m] = [];
            foreach (var l in g.Links(missing, ImpactBuilder.ListThreshold))
                if (l.Confidence >= ImpactBuilder.WalkThreshold || l.Kind == "dispatch") _up[l.TargetId].Add(l);
            foreach (var m in missing) _up[m].AddRange(ImpactQuery.HopLinks(g, matchesByEntry, m));
            g.Load(missing.SelectMany(m => _up[m]).Select(l => l.CallerId).Distinct().ToList(), withParents: true);
        }

        /// <summary>类型 / 枚举 / 类型别名 / 顶层变量：改它影响的是用到它的地方，不只是 new 它的地方。</summary>
        private static bool UsesReferences(SymbolRecord s) =>
            s.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum or SymbolKind.TypeAlias or SymbolKind.Variable;

        private List<ImpactLink> References(long id)
        {
            var list = new List<ImpactLink>();
            using (var cmd = db.CreateCommand("""
                SELECT e.from_id, e.confidence, e.line, e.pos, e.ctx FROM edges e
                WHERE e.to_id = $id AND e.kind = 'references' AND e.confidence >= $min AND e.from_id <> $id
                """, ("$id", id), ("$min", ImpactBuilder.WalkThreshold)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new ImpactLink(id, r.GetInt64(0), RefKind, r.GetDouble(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4)));
            g.Load(list.Select(l => l.CallerId).Distinct().ToList(), withParents: true);
            return list;
        }

        /// <summary>max-min 置信度：种子 1.0，沿边取 min，多条路径取 max；在已收集的边上松弛到不动点（边数有上限，几轮就收敛）。</summary>
        private static Dictionary<long, double> MaxMin(long seed, IEnumerable<long> nodes, List<(long Target, long Caller, double Confidence)> links)
        {
            var conf = nodes.ToDictionary(n => n, _ => 0.0);
            conf[seed] = 1.0;
            for (var round = 0; round < 64; round++)
            {
                var changed = false;
                foreach (var (t, c, w) in links)
                {
                    var cand = Math.Min(conf[t], w);
                    if (cand > conf[c] + 1e-9) { conf[c] = cand; changed = true; }
                }
                if (!changed) break;
            }
            return conf;
        }
    }
}
