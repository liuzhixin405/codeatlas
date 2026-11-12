using System.Text;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Server.Mcp;

/// <param name="MaxChars">整段输出的字符预算（约等于 token × 4；中文更密）。超出时按相关度截断源码，关系部分最多占 <see cref="ExploreBuilder.RelationsShare"/>。</param>
/// <param name="MaxSymbols">最多取几个命中符号。</param>
/// <param name="IncludeTests">测试代码不降权、影响范围列出测试调用方。</param>
/// <param name="MaxLinesPerSymbol">单个符号最多贴几行源码：方法超了截头部，类型超了改成"声明 + 成员一览"。</param>
public sealed record ExploreOptions(
    int MaxChars = ExploreBuilder.DefaultMaxChars, int MaxSymbols = ExploreBuilder.DefaultMaxSymbols,
    bool IncludeTests = false, int MaxLinesPerSymbol = ExploreBuilder.DefaultMaxLinesPerSymbol);

/// <summary>
/// MCP 主工具 codeatlas_explore 的输出：一次调用给 agent 一段纯文本，依次是
/// <list type="number">
/// <item>过期警告：命中符号所在文件的 stat 与索引记录对不上（<see cref="IndexFreshness"/>）、索引格式过旧；</item>
/// <item>命中的符号：按 <see cref="SymbolMatcher"/> 的得分排，带命中原因；</item>
/// <item>源码：从磁盘实时读，带行号，按文件分组（文件按其中最相关符号的名次排）；包含在已贴出的范围里的符号不重复贴；</item>
/// <item>调用路径：命中符号之间的直接边与经一个中间符号的两跳路径（calls / dispatches / implements / inherits，类型命中时含它的成员）；</item>
/// <item>各符号调出去的步骤：<see cref="OutlineQuery"/> 的调用大纲（含分派、接口实现、多候选），不含框架 / 第三方调用；</item>
/// <item>影响范围：<see cref="ImpactQuery"/> 的直接调用方、测试调用方数、受影响入口。</item>
/// </list>
/// 每条边带置信度；0.3（同名多候选，各候选一条边）显式标注"低置信"，不当成确定的调用。
/// 预算：先放警告与命中列表，关系部分封顶 <see cref="RelationsShare"/>，剩下的按相关度放源码；放不下的符号列在末尾"已截断"里（文件:行，agent 可直接读）。
/// </summary>
public static class ExploreBuilder
{
    public const int DefaultMaxChars = 24000;
    public const int MinMaxChars = 2000;
    public const int MaxMaxChars = 200_000;
    public const int DefaultMaxSymbols = 8;
    public const int DefaultMaxLinesPerSymbol = 160;
    /// <summary>关系部分（调用路径 + 调出 + 影响）最多占预算的比例。</summary>
    public const double RelationsShare = 0.35;
    /// <summary>命中得分低于第一名这个比例的不要（只是顺带对上一个词）。</summary>
    public const double RelativeCutoff = 0.4;
    /// <summary>算影响范围的命中符号个数（每个要往上整趟 BFS）。</summary>
    public const int ImpactSymbols = 3;
    /// <summary>列调用大纲的命中符号个数与每个的步数。</summary>
    public const int OutlineSymbols = 5;
    public const int OutlineSteps = 12;
    public const int MaxPaths = 20;
    /// <summary>警告里最多列几个过期文件。</summary>
    public const int MaxStaleListed = 20;
    /// <summary>源码里超过这个长度的行截断（压缩过的 JS 之类）。</summary>
    private const int MaxLineChars = 300;
    /// <summary>剩余预算不到这么多字符时不再贴半截源码。</summary>
    private const int MinPartial = 400;

    public const string LowConfidenceZh = "低置信：同名多候选之一，未确定";
    public const string LowConfidenceEn = "low confidence: one of several same-name candidates, not certain";

    private sealed record Snippet(SymbolHit Hit, string Label, string Text, int From, int To, bool Full);

    public static string Build(IndexDatabase db, string repoRoot, string query, ExploreOptions options)
    {
        var maxChars = Math.Clamp(options.MaxChars, MinMaxChars, MaxMaxChars);
        var reader = new IndexReader(db);
        var head = new StringBuilder();
        head.AppendLine($"# CodeAtlas explore: {query}");
        if (db.NeedsRebuild)
            head.AppendLine(L("警告：索引是旧版本格式建的，关系可能不全；请先运行 codeatlas index <repo> --full。", "Warning: the index was built with an older format and relations may be incomplete; run codeatlas index <repo> --full first."));

        var all = SymbolMatcher.Match(db, query, options.IncludeTests);
        if (all.Count == 0)
        {
            head.AppendLine(L("没有找到相关符号。换成代码里会出现的名字（类名、方法名、文件名）再试，或用 codeatlas_search 按前缀搜。",
                "No related symbols found. Try names that appear in the code (class, method or file names), or use codeatlas_search for prefix search."));
            return Lf(head.ToString());
        }
        var top = all[0].Score;
        var hits = all.Where(h => h.Score >= top * RelativeCutoff).Take(Math.Clamp(options.MaxSymbols, 1, 50)).ToList();

        // ---- 标签（类型.方法）：命中符号 + 父符号 ----
        var symbols = hits.ToDictionary(h => h.Symbol.Id, h => h.Symbol);
        var paths = hits.GroupBy(h => h.Symbol.FileId).ToDictionary(g => g.Key, g => g.First().Path);
        LoadSymbols(db, hits.Select(h => h.Symbol.ParentId).OfType<long>().ToList(), symbols, paths);
        string Label(SymbolRecord s) => OutlineBuilder.SymbolLabel(s, symbols, paths);

        // ---- 过期检查：命中符号所在的文件 ----
        var files = hits.Select(h => h.Symbol.FileId).Distinct().Select(reader.GetFileById).OfType<FileRecord>().ToList();
        var stale = IndexFreshness.CheckAll(repoRoot, files);
        var stalePaths = stale.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        if (stale.Count > 0)
        {
            head.AppendLine(L($"警告：{stale.Count} 个文件在建索引后改过（大小 / 修改时间与索引记录不一致），其中的行号、源码片段与调用关系可能对不上——请直接读这些文件的源码，或重新运行 codeatlas index：",
                $"Warning: {stale.Count} file(s) changed after indexing (size / modified time differ from the index); line numbers, snippets and relations there may be off — read these files directly, or re-run codeatlas index:"));
            foreach (var s in stale.Take(MaxStaleListed)) head.AppendLine($"  - {s.Path}  ({s.Reason})");
            if (stale.Count > MaxStaleListed) head.AppendLine(L($"  - ……另外 {stale.Count - MaxStaleListed} 个", $"  - … {stale.Count - MaxStaleListed} more"));
        }

        head.AppendLine();
        head.AppendLine(L("## 命中的符号（按相关度）", "## Matched symbols (by relevance)"));
        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            head.AppendLine($"{i + 1}. {Label(h.Symbol)}  [{DbNames.ToDb(h.Symbol.Kind)}]  {h.Path}:{h.Symbol.StartLine}-{h.Symbol.EndLine}  " +
                            L($"得分 {h.Score:0.#}（{string.Join("；", h.Reasons)}）", $"score {h.Score:0.#} ({string.Join("; ", h.Reasons)})") +
                            $"  #{h.Symbol.Id}");
        }
        if (all.Count > hits.Count)
            head.AppendLine(L($"（另有 {all.Count - hits.Count} 个弱相关的命中没列出；需要时加大 maxSymbols 或换更具体的名字）",
                $"({all.Count - hits.Count} weaker matches not listed; raise maxSymbols or use a more specific name)"));

        // ---- 关系 ----
        var relations = new StringBuilder();
        AppendPaths(db, hits, symbols, paths, relations);
        AppendOutlines(db, hits, options, Label, relations);
        AppendImpact(db, hits, options, Label, relations);
        var relationsText = relations.ToString();
        var relationsCap = (int)(maxChars * RelationsShare);
        var relationsCut = false;
        if (relationsText.Length > relationsCap)
        {
            relationsText = CutAtLine(relationsText, relationsCap);
            relationsCut = true;
        }

        // ---- 源码：按相关度放，放不下的列进"已截断" ----
        var snippets = Snippets(reader, repoRoot, hits, Label, options.MaxLinesPerSymbol);
        var tail = new StringBuilder();
        var budget = maxChars - head.Length - relationsText.Length - 600; // 600：分组标题与截断说明
        var included = new List<Snippet>();
        var omitted = new List<SymbolHit>();
        var partial = false;
        foreach (var sn in snippets)
        {
            if (omitted.Count > 0 || partial) { omitted.Add(sn.Hit); continue; }
            var cost = sn.Text.Length + sn.Hit.Path.Length + 8;
            if (cost <= budget) { included.Add(sn); budget -= cost; continue; }
            if (budget >= MinPartial)
            {
                var cut = CutAtLine(sn.Text, budget - 80);
                included.Add(sn with { Text = cut + L("      …（超出预算，后面的行没贴；需要时直接读文件）\n", "      … (over budget, remaining lines omitted; read the file if needed)\n"), Full = false });
                partial = true;
                continue;
            }
            omitted.Add(sn.Hit);
        }

        var source = new StringBuilder();
        source.AppendLine();
        source.AppendLine(L("## 源码（磁盘实时读取，行号在左）", "## Source (read live from disk, line numbers on the left)"));
        foreach (var group in included.GroupBy(s => s.Hit.Path))
        {
            source.AppendLine();
            source.AppendLine(stalePaths.Contains(group.Key) ? $"### {group.Key}  " + L("（已过期，见顶部警告）", "(stale, see warning above)") : $"### {group.Key}");
            foreach (var sn in group.OrderBy(s => s.From))
            {
                source.AppendLine($"// {sn.Label}  [{DbNames.ToDb(sn.Hit.Symbol.Kind)}]  L{sn.From}-{sn.To}");
                source.Append(sn.Text);
            }
        }

        if (omitted.Count > 0 || partial || relationsCut)
        {
            tail.AppendLine();
            tail.AppendLine(L($"## 已截断（预算 {maxChars} 字符）", $"## Truncated (budget {maxChars} chars)"));
            if (partial) tail.AppendLine(L("- 最后一个贴出的符号源码只贴了前半部分。", "- The last snippet shown is only partial."));
            if (omitted.Count > 0)
            {
                tail.AppendLine(L($"- 这 {omitted.Count} 个符号的源码没贴（按相关度排在后面），需要时直接读：", $"- Source of these {omitted.Count} symbol(s) was omitted (lower relevance); read them directly if needed:"));
                foreach (var h in omitted) tail.AppendLine($"  - {Label(h.Symbol)}  {h.Path}:{h.Symbol.StartLine}-{h.Symbol.EndLine}");
            }
            if (relationsCut) tail.AppendLine(L("- 关系部分超过预算的 35%，后面的行被截掉了；用 codeatlas_callers / codeatlas_callees / codeatlas_impact 单独查。",
                "- The relations part exceeded 35% of the budget and was cut; use codeatlas_callers / codeatlas_callees / codeatlas_impact for the rest."));
            tail.AppendLine(L("- 可用 maxChars 调大预算。", "- Raise maxChars for a larger budget."));
        }

        return Lf(head + source.ToString() + relationsText + tail);
    }

    // ---------------- 源码 ----------------

    private static List<Snippet> Snippets(IndexReader reader, string repoRoot, List<SymbolHit> hits, Func<SymbolRecord, string> label, int maxLines)
    {
        maxLines = Math.Clamp(maxLines, 10, 2000);
        var cache = new Dictionary<string, string[]?>(StringComparer.Ordinal);
        string[]? Lines(string path)
        {
            if (cache.TryGetValue(path, out var lines)) return lines;
            try { lines = File.ReadAllLines(IndexFreshness.FullPath(repoRoot, path)); }
            catch (IOException) { lines = null; }
            catch (UnauthorizedAccessException) { lines = null; }
            return cache[path] = lines;
        }

        var result = new List<Snippet>();
        foreach (var h in hits)
        {
            var s = h.Symbol;
            // 包含在另一个会整段贴出的命中符号里（方法在命中的小类里，不论谁排前面）：不重复贴
            if (hits.Any(o => o != h && o.Path == h.Path && o.Symbol.EndLine - o.Symbol.StartLine + 1 <= maxLines
                              && o.Symbol.StartLine <= s.StartLine && s.EndLine <= o.Symbol.EndLine
                              && (o.Symbol.StartLine, o.Symbol.EndLine) != (s.StartLine, s.EndLine))) continue;
            var lines = Lines(h.Path);
            if (lines is null)
            {
                result.Add(new Snippet(h, label(s), L("      （文件读不到：已删除或移动，见顶部警告）\n", "      (file unreadable: deleted or moved, see warning above)\n"), s.StartLine, s.EndLine, false));
                continue;
            }
            var from = Math.Clamp(s.StartLine, 1, Math.Max(1, lines.Length));
            var to = Math.Clamp(s.EndLine, from, Math.Max(1, lines.Length));
            var sb = new StringBuilder();
            var count = to - from + 1;
            if (count <= maxLines)
            {
                AppendLines(sb, lines, from, to);
                result.Add(new Snippet(h, label(s), sb.ToString(), from, to, true));
            }
            else if (OutlineBuilder.IsType(s.Kind))
            {
                // 大类型：声明那几行 + 成员一览（成员本身命中时另外贴）
                var declEnd = from;
                while (declEnd < to && declEnd - from < 6 && !lines[declEnd - 1].Contains('{') && !lines[declEnd - 1].TrimEnd().EndsWith(':')) declEnd++;
                AppendLines(sb, lines, from, declEnd);
                sb.AppendLine(L($"      …（类型共 {count} 行，超过 {maxLines} 行，只列成员；需要哪个成员就按名字再 explore 或直接读文件）",
                    $"      … (type spans {count} lines, over {maxLines}; members listed only — explore a member by name or read the file)"));
                foreach (var c in reader.GetChildren(s.Id).Where(c => c.Kind != SymbolKind.External))
                    sb.AppendLine($"      - {c.Name}  [{DbNames.ToDb(c.Kind)}]  L{c.StartLine}-{c.EndLine}");
                result.Add(new Snippet(h, label(s), sb.ToString(), from, to, false));
            }
            else
            {
                AppendLines(sb, lines, from, from + maxLines - 1);
                sb.AppendLine(L($"      …（共 {count} 行，第 {from + maxLines}–{to} 行没贴）", $"      … ({count} lines; lines {from + maxLines}–{to} omitted)"));
                result.Add(new Snippet(h, label(s), sb.ToString(), from, to, false));
            }
        }
        return result;
    }

    private static void AppendLines(StringBuilder sb, string[] lines, int from, int to)
    {
        for (var n = from; n <= to && n <= lines.Length; n++)
        {
            var text = lines[n - 1];
            if (text.Length > MaxLineChars) text = text[..MaxLineChars] + " …";
            sb.Append(n.ToString().PadLeft(6)).Append("  ").AppendLine(text);
        }
    }

    // ---------------- 调用路径 ----------------

    private sealed record RawEdge(long From, long To, string Kind, string? Via, double Confidence, int Line, string Path);

    private static void AppendPaths(IndexDatabase db, List<SymbolHit> hits, Dictionary<long, SymbolRecord> symbols, Dictionary<long, string> paths, StringBuilder sb)
    {
        // 命中的类型连它的直接成员一起算：类 A 命中、它的方法调了命中的 B，这条也是"A 与 B 之间"的路径
        var owner = hits.ToDictionary(h => h.Symbol.Id, h => h.Symbol.Id);
        var typeIds = hits.Where(h => OutlineBuilder.IsType(h.Symbol.Kind)).Select(h => h.Symbol.Id).ToList();
        if (typeIds.Count > 0)
            foreach (var m in OutlineQuery.ReadSymbols(db, $"SELECT {OutlineQuery.SymbolColumns} FROM symbols s WHERE s.parent_id IN ({OutlineQuery.Ids(typeIds)})", typeIds.Count))
            {
                owner.TryAdd(m.Id, m.ParentId!.Value);
                symbols.TryAdd(m.Id, m);
            }
        var closure = owner.Keys.ToList();
        const string kinds = "e.kind IN ('calls', 'dispatches', 'implements', 'inherits')";

        var direct = ReadEdges(db, $"e.from_id IN ({OutlineQuery.Ids(closure)}) AND e.to_id IN ({OutlineQuery.Ids(closure)}) AND {kinds}")
            .Where(e => owner[e.From] != owner[e.To]).ToList();
        // 两跳：命中 → X → 命中（X 不在命中里；只走 calls / dispatches，置信度 ≥ 0.4，免得多候选把路径放大）
        var outs = ReadEdges(db, $"e.from_id IN ({OutlineQuery.Ids(closure)}) AND e.to_id IS NOT NULL AND e.to_id NOT IN ({OutlineQuery.Ids(closure)}) AND e.kind IN ('calls', 'dispatches') AND e.confidence >= 0.4");
        var mids = outs.Select(e => e.To).Distinct().ToList();
        var ins = mids.Count == 0 ? [] : ReadEdges(db, $"e.from_id IN ({OutlineQuery.Ids(mids)}) AND e.to_id IN ({OutlineQuery.Ids(closure)}) AND e.kind IN ('calls', 'dispatches') AND e.confidence >= 0.4");
        var insByMid = ins.ToLookup(e => e.From);
        var twoHop = new List<(RawEdge A, RawEdge B)>();
        foreach (var a in outs)
            foreach (var b in insByMid[a.To])
                if (owner[a.From] != owner[b.To] && twoHop.Count < MaxPaths * 4) twoHop.Add((a, b));
        LoadSymbols(db, [.. mids.Where(m => insByMid.Contains(m)), .. direct.Select(e => e.From), .. direct.Select(e => e.To)], symbols, paths);
        LoadSymbols(db, symbols.Values.Select(s => s.ParentId).OfType<long>().Where(p => !symbols.ContainsKey(p)).ToList(), symbols, paths);
        string Name(long id) => symbols.TryGetValue(id, out var s) ? OutlineBuilder.SymbolLabel(s, symbols, paths) : "#" + id;

        sb.AppendLine();
        sb.AppendLine(L("## 调用路径（命中符号之间）", "## Call paths (between matched symbols)"));
        if (direct.Count == 0 && twoHop.Count == 0)
        {
            sb.AppendLine(L("（命中符号之间没有直接或经一个中间符号的调用 / 分派 / 继承边）", "(no direct or one-hop calls / dispatches / inheritance between matched symbols)"));
            return;
        }
        foreach (var e in direct.DistinctBy(e => (e.From, e.To, e.Kind, e.Via, e.Line)).Take(MaxPaths))
            sb.AppendLine($"- {Name(e.From)} → {Name(e.To)}  {EdgeText(e)}");
        foreach (var (a, b) in twoHop.DistinctBy(p => (p.A.From, p.A.To, p.B.To)).Take(MaxPaths))
            sb.AppendLine($"- {Name(a.From)} → {Name(a.To)} → {Name(b.To)}  " + L("（两跳）", "(two hops)") + $"  [{EdgeText(a)}] [{EdgeText(b)}]");
    }

    private static string EdgeText(RawEdge e) =>
        $"{e.Kind}{(e.Via is null ? "" : "(" + e.Via + ")")}  {e.Path}:{e.Line}  {Conf(e.Confidence)}{SynthesizedNote(e.Via)}";

    /// <summary>合成边（索引后推断出来的，不是源码里的直接调用）显式标注，免得 agent 当成确定的调用。目前只有事件频道配对（via = event:x，见 EventChannelLinker）。</summary>
    internal static string SynthesizedNote(string? via) =>
        via is not null && via.StartsWith("event:", StringComparison.Ordinal)
            ? " [" + L("推断：事件频道 emit / on 同名配对，不是直接调用", "inferred: event channel emit / on matched by name, not a direct call") + "]"
            : "";

    /// <summary>置信度文本；0.3 档（≤ 0.3）显式标低置信，免得 agent 当成确定的调用。</summary>
    public static string Conf(double c) =>
        c <= 0.3 + 1e-9 ? $"{c:0.##} [{L(LowConfidenceZh, LowConfidenceEn)}]" : c.ToString("0.##");

    private static List<RawEdge> ReadEdges(IndexDatabase db, string where)
    {
        var list = new List<RawEdge>();
        using var cmd = db.CreateCommand($"""
            SELECT e.from_id, e.to_id, e.kind, e.via, e.confidence, e.line, f.path FROM edges e JOIN files f ON f.id = e.file_id
            WHERE {where} AND e.from_id <> e.to_id ORDER BY e.from_id, e.line, e.id
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new RawEdge(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetDouble(4), r.GetInt32(5), r.GetString(6)));
        return list;
    }

    // ---------------- 调用大纲 ----------------

    private static void AppendOutlines(IndexDatabase db, List<SymbolHit> hits, ExploreOptions options, Func<SymbolRecord, string> label, StringBuilder sb)
    {
        var roots = hits.Select(h => h.Symbol).Where(s => s.Kind is SymbolKind.Method or SymbolKind.Function).Take(OutlineSymbols).ToList();
        if (roots.Count == 0) return;
        var outlines = OutlineQuery.BuildMany(db, roots, new OutlineOptions(Externals: false, Tests: options.IncludeTests), expandable: false);
        sb.AppendLine();
        sb.AppendLine(L("## 调出去的调用（按源码顺序；不含框架 / 第三方调用）", "## Outgoing calls (in source order; framework / third-party calls excluded)"));
        foreach (var root in roots)
        {
            if (!outlines.TryGetValue(root.Id, out var o)) continue;
            sb.AppendLine($"{label(root)}  ({o.Symbol.Path}:{o.Symbol.StartLine}):");
            if (o.Steps.Count == 0) { sb.AppendLine(L("  （没有仓库内的调用）", "  (no in-repo calls)")); continue; }
            foreach (var step in o.Steps.Take(OutlineSteps)) AppendStep(sb, step, "  ");
            if (o.Steps.Count > OutlineSteps) sb.AppendLine(L($"  ……另外 {o.Steps.Count - OutlineSteps} 步（codeatlas_callees 看全部）", $"  … {o.Steps.Count - OutlineSteps} more steps (codeatlas_callees for all)"));
        }

    }

    internal static void AppendStep(StringBuilder sb, OutlineStepDto step, string indent)
    {
        var where = step.Target is { } t && t.Path != IndexWriter.ExternalFilePath ? $"  → {t.Path}:{t.StartLine}" : "";
        var repeat = step.Repeat > 1 ? $" ×{step.Repeat}" : "";
        var ctx = step.Ctx is null ? "" : $" [{step.Ctx}]";
        if (step.Kind == "ambiguous")
        {
            sb.AppendLine($"{indent}{step.Order}. L{step.Line} ambiguous {step.Label}{repeat}{ctx}  {Conf(step.Confidence)}" +
                          L($"：{step.Children.Count} 个候选，分不清是哪个", $": {step.Children.Count} candidates, cannot tell which"));
            foreach (var c in step.Children.Take(8))
                sb.AppendLine($"{indent}     ? {c.Label}" + (c.Target is { } ct ? $"  {ct.Path}:{ct.StartLine}" : ""));
            if (step.Children.Count > 8) sb.AppendLine($"{indent}     ? …(+{step.Children.Count - 8})");
            return;
        }
        sb.AppendLine($"{indent}{step.Order}. L{step.Line} {step.Kind} {step.Label}{repeat}{ctx}  {Conf(step.Confidence)}{where}");
        foreach (var c in step.Children.Take(8)) AppendStep(sb, c, indent + "   ");
    }

    // ---------------- 影响范围 ----------------

    private static void AppendImpact(IndexDatabase db, List<SymbolHit> hits, ExploreOptions options, Func<SymbolRecord, string> label, StringBuilder sb)
    {
        var targets = hits.Select(h => h.Symbol)
            .Where(s => s.Kind is SymbolKind.Method or SymbolKind.Function or SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface)
            .Take(ImpactSymbols).ToList();
        if (targets.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine(L("## 影响范围（谁调用它、一路影响到哪些入口；直接调用方只算置信度 ≥ 0.4 的边，入口汇总只走 ≥ 0.5，0.3 的多候选不参与）",
            "## Impact (who calls it and which entry points it reaches; callers use edges with confidence ≥ 0.4, the entry walk only ≥ 0.5, 0.3 candidates excluded)"));
        foreach (var s in targets)
        {
            if (ImpactQuery.Build(db, s.Id, new ImpactOptions(Entries: true, Tests: options.IncludeTests)) is not { } impact) continue;
            AppendImpactSummary(sb, impact, label(s), 5, 5);
        }
    }

    internal static void AppendImpactSummary(StringBuilder sb, ImpactDto impact, string label, int maxCallers, int maxEntries)
    {
        var entries = impact.Entries ?? [];
        sb.AppendLine($"{label}  ({impact.Symbol.Path}:{impact.Symbol.StartLine}): " +
                      L($"直接调用方 {impact.Callers.Count}{(impact.Truncated ? "+" : "")}，测试里的调用方 {impact.TestCallers}，受影响入口 {entries.Count}{(impact.EntriesTruncated ? "+（往上走到上限，可能不全）" : "")}",
                        $"{impact.Callers.Count}{(impact.Truncated ? "+" : "")} direct caller(s), {impact.TestCallers} in tests, {entries.Count}{(impact.EntriesTruncated ? "+ (walk hit the limit, may be incomplete)" : "")} affected entry point(s)"));
        foreach (var c in impact.Callers.Take(maxCallers))
            sb.AppendLine($"  ← {c.Kind} {c.Label}  {c.Caller.Path}:{string.Join(",", c.Sites.Select(x => x.Line))}  {Conf(c.Confidence)}" +
                          (c.AsEntry.Count > 0 ? L("  （本身是入口）", "  (is an entry point)") : ""));
        if (impact.Callers.Count > maxCallers) sb.AppendLine(L($"  ……另外 {impact.Callers.Count - maxCallers} 个调用方（codeatlas_callers 看全部）", $"  … {impact.Callers.Count - maxCallers} more callers (codeatlas_callers for all)"));
        foreach (var e in entries.Take(maxEntries))
            sb.AppendLine($"  ⇡ {e.Entry.Kind} {(e.Entry.HttpMethod is null ? "" : e.Entry.HttpMethod + " ")}{e.Entry.Route ?? e.Entry.Name}  {e.ContainerName}  {e.Entry.File}:{e.Entry.Line}  " + L($"深 {e.Depth}", $"depth {e.Depth}"));
        if (entries.Count > maxEntries) sb.AppendLine(L($"  ……另外 {entries.Count - maxEntries} 个入口（codeatlas_impact 看全部）", $"  … {entries.Count - maxEntries} more entry points (codeatlas_impact for all)"));
    }

    // ---------------- 工具 ----------------

    private static void LoadSymbols(IndexDatabase db, IReadOnlyCollection<long> ids, Dictionary<long, SymbolRecord> symbols, Dictionary<long, string> paths)
    {
        var missing = ids.Where(i => !symbols.ContainsKey(i)).Distinct().ToList();
        foreach (var s in OutlineQuery.ReadSymbols(db, $"SELECT {OutlineQuery.SymbolColumns} FROM symbols s WHERE s.id IN ({OutlineQuery.Ids(missing)})", missing.Count))
            symbols[s.Id] = s;
        var files = symbols.Values.Select(s => s.FileId).Where(f => !paths.ContainsKey(f)).Distinct().ToList();
        foreach (var (id, path) in SymbolMatcher.Paths(db, files)) paths[id] = path;
    }

    /// <summary>统一成 \n 换行（StringBuilder.AppendLine 在 Windows 上写 \r\n）。</summary>
    internal static string Lf(string text) => text.Replace("\r\n", "\n");

    /// <summary>截到不超过 max 个字符的最后一个整行。</summary>
    internal static string CutAtLine(string text, int max)
    {
        if (text.Length <= max) return text;
        if (max <= 0) return "";
        var cut = text.LastIndexOf('\n', Math.Min(max, text.Length) - 1);
        return cut < 0 ? "" : text[..(cut + 1)];
    }
}
