using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Server;

namespace CodeAtlas.Cli.Commands;

internal static class QueryCommand
{
    private static readonly HashSet<EdgeKind> DefaultKinds =
        [EdgeKind.Calls, EdgeKind.Implements, EdgeKind.Inherits, EdgeKind.References, EdgeKind.Dispatches];

    public static int Run(ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Positional.Count < 2)
        {
            stderr.WriteLine(L("用法：", "Usage: ") + "codeatlas query <repo> <name|qualified-name> [--in] [--out] [--depth N] [--kinds a,b] [--db <path>]\n" +
                             "       codeatlas query <repo> <name|qualified-name> --outline [--depth N] [--no-externals] [--tests] [--db <path>]\n" +
                             "       codeatlas query <repo> <name|qualified-name> --impact [--depth N] [--tests] [--db <path>]\n" +
                             "       codeatlas query <repo> <name|qualified-name> --relations [--depth 1|2] [--tests] [--db <path>]\n" +
                             L("       codeatlas query <repo> <A> --to <B> [--depth N] [--max-paths N] [--json] [--db <path>]（A 到 B 的调用路径）\n", "       codeatlas query <repo> <A> --to <B> [--depth N] [--max-paths N] [--json] [--db <path>] (call paths from A to B)\n") +
                             L("       codeatlas query <repo> <name|qualified-name|#符号id> --flow [--depth N] [--json] [--db <path>]", "       codeatlas query <repo> <name|qualified-name|#symbol-id> --flow [--depth N] [--json] [--db <path>]"));
            return 2;
        }
        var repo = args.Positional[0];
        var target = args.Positional[1];
        var depth = int.TryParse(args.Value("--depth"), out var d) ? Math.Clamp(d, 1, 6) : 1;
        var direction = (args.Has("--in"), args.Has("--out")) switch
        {
            (true, false) => Direction.Incoming,
            (false, true) => Direction.Outgoing,
            _ => Direction.Both,
        };
        var kinds = DefaultKinds;
        if (args.Value("--kinds") is { } k)
        {
            kinds = [];
            foreach (var name in k.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!DbNames.TryParseEdgeKind(name, out var kind))
                {
                    stderr.WriteLine(L($"未知的边类型：{name}。可用：{string.Join(", ", DbNames.EdgeKindNames)}", $"Unknown edge kind: {name}. Available: {string.Join(", ", DbNames.EdgeKindNames)}"));
                    return 2;
                }
                kinds.Add(kind);
            }
        }

        IndexDatabase db;
        try { db = CommonOptions.OpenExisting(repo, args, stderr); }
        catch (FileNotFoundException) { return 1; }

        using (db)
        {
            var reader = new IndexReader(db);
            var files = reader.GetAllFiles().ToDictionary(f => f.Id, f => f.Path);
            // #12345：按符号 id（入口清单、--json 输出里的 symbolId）
            var matches = target.StartsWith('#') && long.TryParse(target[1..], out var byId)
                ? (reader.GetSymbol(byId) is { } one ? [one] : [])
                // 完整限定名 → 短名 → 按段对齐的限定名后缀（"类型.方法"、省略参数表 / 泛型），见 OutlineQuery.FindSymbols
                : OutlineQuery.FindSymbols(db, target);
            if (matches.Count == 0) { stderr.WriteLine(L($"未找到符号：{target}", $"Symbol not found: {target}")); return 1; }

            if (matches.Count > 3)
            {
                stdout.WriteLine(L($"有 {matches.Count} 个同名符号，请用限定名：", $"{matches.Count} symbols share this name; use a qualified name:"));
                foreach (var m in matches) stdout.WriteLine($"  {m.QualifiedName}  [{DbNames.ToDb(m.Kind)}]  {files.GetValueOrDefault(m.FileId, "?")}:{m.StartLine}");
                return 0;
            }

            if (args.Value("--to") is { } toName)
                return PrintPaths(db, files, matches, toName, args, stdout, stderr);

            if (args.Has("--flow"))
            {
                // 与 GET /api/symbols/{id}/flow 同一份数据；--depth 是时序树的深度（1..20，默认 12），不是上面邻居查询的 1..6
                int? flowDepth = int.TryParse(args.Value("--depth"), out var fd) ? fd : null;
                return PrintFlow(db, repo, args, matches, flowDepth, stdout, stderr);
            }

            if (args.Has("--relations"))
            {
                RelationsPrinter.Print(db, matches.Select(m => m.Id), new RelationsOptions(int.TryParse(args.Value("--depth"), out var rd) ? Math.Clamp(rd, 1, 2) : 1, args.Has("--tests")), stdout);
                return 0;
            }

            if (args.Has("--impact"))
            {
                // 调用方树默认 3 层：一层只看得到直接调用方，看不出"一路影响到哪"；入口汇总另外整趟 BFS，不受这个深度限制
                var treeDepth = int.TryParse(args.Value("--depth"), out var td) ? Math.Clamp(td, 1, ImpactBuilder.MaxDepth) : 3;
                var tests = args.Has("--tests");
                foreach (var m in matches)
                {
                    stdout.WriteLine($"{m.QualifiedName}  [{DbNames.ToDb(m.Kind)}]  {files.GetValueOrDefault(m.FileId, "?")}:{m.StartLine}");
                    PrintImpact(db, m.Id, tests, treeDepth, stdout);
                    stdout.WriteLine();
                }
                return 0;
            }

            if (args.Has("--outline"))
            {
                var options = new OutlineOptions(Externals: !args.Has("--no-externals"), Tests: args.Has("--tests"));
                foreach (var m in matches)
                {
                    stdout.WriteLine($"{m.QualifiedName}  [{DbNames.ToDb(m.Kind)}]  {files.GetValueOrDefault(m.FileId, "?")}:{m.StartLine}");
                    PrintOutline(db, m.Id, options, depth, indent: 1, visited: [m.Id], stdout);
                    stdout.WriteLine();
                }
                return 0;
            }

            foreach (var m in matches)
            {
                stdout.WriteLine($"{m.QualifiedName}  [{DbNames.ToDb(m.Kind)}]  {files.GetValueOrDefault(m.FileId, "?")}:{m.StartLine}");
                Print(reader, m.Id, direction, kinds, depth, indent: 1, visited: [m.Id], stdout);
                stdout.WriteLine();
            }
            return 0;
        }
    }

    /// <summary>
    /// A → B 的调用路径（与 GET /api/symbols/{id}/paths?to= 同一份数据）。--depth 是路径最多几步（1..12，默认 8），--max-paths 最多几条（1..50，默认 10）。
    /// 两端各自要唯一：同名多个时列出候选让人换限定名（或 #符号id）。每一步一行：→ 是确定 / 高置信度的调用，⇢ 是分派边或低置信度（&lt; 0.7）的边。
    /// --json 输出 CallPathsDto。
    /// </summary>
    private static int PrintPaths(IndexDatabase db, Dictionary<long, string> files, IReadOnlyList<SymbolRecord> fromMatches, string toName, ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        var reader = new IndexReader(db);
        var toMatches = toName.StartsWith('#') && long.TryParse(toName[1..], out var toId)
            ? (reader.GetSymbol(toId) is { } one ? [one] : [])
            : OutlineQuery.FindSymbols(db, toName);
        if (toMatches.Count == 0) { stderr.WriteLine(L($"未找到符号：{toName}", $"Symbol not found: {toName}")); return 1; }
        foreach (var (side, list) in new[] { ("A", fromMatches), ("B", toMatches) })
        {
            if (list.Count <= 1) continue;
            stdout.WriteLine(L($"{side} 有 {list.Count} 个同名符号，请用限定名或 #符号id：", $"{side}: {list.Count} symbols share this name; use a qualified name or #symbol-id:"));
            foreach (var m in list) stdout.WriteLine($"  #{m.Id}  {m.QualifiedName}  [{DbNames.ToDb(m.Kind)}]  {files.GetValueOrDefault(m.FileId, "?")}:{m.StartLine}");
            return 0;
        }
        var depth = int.TryParse(args.Value("--depth"), out var d) ? d : CallPathFinder.DefaultMaxDepth;
        var max = int.TryParse(args.Value("--max-paths"), out var mp) ? mp : CallPathFinder.DefaultMaxPaths;
        var (symbols, edges) = MapData.Load(reader);
        var result = CallPathFinder.Find(symbols, edges, fromMatches[0].Id, toMatches[0].Id, depth, max);
        if (args.Has("--json"))
        {
            stdout.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, ArchCommand.FlowJson));
            return 0;
        }
        var a = fromMatches[0];
        var b = toMatches[0];
        stdout.WriteLine($"{a.QualifiedName}  [{DbNames.ToDb(a.Kind)}]  {files.GetValueOrDefault(a.FileId, "?")}:{a.StartLine}");
        stdout.WriteLine($"  ⇒ {b.QualifiedName}  [{DbNames.ToDb(b.Kind)}]  {files.GetValueOrDefault(b.FileId, "?")}:{b.StartLine}");
        stdout.WriteLine(L($"路径 {result.Paths.Count} 条（最多 {result.MaxDepth} 步、{result.MaxPaths} 条{(result.Truncated ? "；还有更多，已截断" : "")}）",
            $"{Plural(result.Paths.Count, "path", "paths")} (at most {result.MaxDepth} steps, {result.MaxPaths} paths{(result.Truncated ? "; more exist, truncated" : "")})"));
        if (result.Note is { } note) stdout.WriteLine("  " + note);
        for (var i = 0; i < result.Paths.Count; i++)
        {
            var p = result.Paths[i];
            stdout.WriteLine();
            stdout.WriteLine(L($"#{i + 1}  {p.Steps.Count} 步  最低 confidence {p.MinConfidence:0.00}{(p.Inferred ? "  含分派 / 低置信度边" : "")}",
                $"#{i + 1}  {Plural(p.Steps.Count, "step", "steps")}  min confidence {p.MinConfidence:0.00}{(p.Inferred ? "  includes dispatch / low-confidence edges" : "")}"));
            stdout.WriteLine($"    {result.FromLabel}");
            foreach (var s in p.Steps)
            {
                var kind = s.Kind == "dispatch" ? "dispatches" : "calls";
                var via = s.Via is null ? "" : $"  via {s.Via}";
                stdout.WriteLine($"    {(s.Weak ? "⇢" : "→")} {kind}  {s.Label}  ({s.Confidence:0.00})  @ {s.Path}:{s.Line}{via}");
            }
        }
        return 0;
    }

    /// <summary>
    /// 从任意符号出发的时序树（与 GET /api/symbols/{id}/flow 同一份数据，打印格式同 arch --flow）。--json：一个符号输出一个对象，多个同名时输出数组。
    /// </summary>
    private static int PrintFlow(IndexDatabase db, string repo, ParsedArgs args, IReadOnlyList<SymbolRecord> matches, int? depth, TextWriter stdout, TextWriter stderr)
    {
        var maxDepth = Math.Clamp(depth ?? FlowBuilder.DefaultMaxDepth, 1, 20);
        var (entries, routeMatches) = EntryStorage.Read(db);
        var arch = ArchStorage.Read(db);
        var containers = arch.Nodes.Where(n => n.Kind == Core.Architecture.ArchNodeKind.Container).ToList();
        var externals = arch.Nodes.Where(n => n.Kind == Core.Architecture.ArchNodeKind.External).ToList();
        var (symbols, edges) = MapData.Load(new IndexReader(db));
        var byId = symbols.ToDictionary(s => s.Id);
        Core.Architecture.ArchNode? repoContainer = null;
        var roots = CommonOptions.Roots(Path.GetFullPath(repo), args, stderr);
        var flows = new List<FlowDto>();
        foreach (var m in matches)
        {
            if (!byId.TryGetValue(m.Id, out var symbol)) continue;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var flow = SymbolFlow.Build(symbols, edges, symbol, containers, routeMatches, entries, maxDepth, arch.Outbound, externals,
                () => repoContainer ??= DsmBuilder.RepoContainer(roots));
            flow = MessagePipeline.InjectFromDb(db, roots.Main, flow, containers, symbols, edges);
            flows.Add(flow);
            if (args.Has("--json")) continue;
            var asEntry = entries.FirstOrDefault(e => e.SymbolId == flow.EntrySymbolId);
            var containerName = containers.FirstOrDefault(c => c.Key == flow.ContainerKey)?.Name ?? flow.ContainerKey;
            ArchCommand.PrintFlowTree(flow, asEntry is null ? L("起点", "Start") : L("入口", "Entry"), asEntry?.HttpMethod, containerName, sw.Elapsed.TotalMilliseconds, stdout);
            stdout.WriteLine();
        }
        if (args.Has("--json"))
            stdout.WriteLine(flows.Count == 1
                ? System.Text.Json.JsonSerializer.Serialize(flows[0], ArchCommand.FlowJson)
                : System.Text.Json.JsonSerializer.Serialize(flows, ArchCommand.FlowJson));
        return 0;
    }

    /// <summary>
    /// 调用大纲（与 GET /api/symbols/{id}/outline 同一份数据）：缩进树，行尾是行号、所在控制结构、相邻重复次数；
    /// "+" 表示能展开，--depth N 时就地展开 N 层（已在当前路径上的不再展开）。
    /// </summary>
    private static void PrintOutline(IndexDatabase db, long id, OutlineOptions options, int depth, int indent, HashSet<long> visited, TextWriter stdout)
    {
        if (OutlineQuery.Build(db, id, options) is not { } outline) return;
        PrintSteps(outline.Steps, indent);
        if (outline.Truncated) stdout.WriteLine(new string(' ', indent * 2) + L($"…（超过 {OutlineBuilder.MaxSteps} 步，已截断）", $"… (more than {OutlineBuilder.MaxSteps} steps, truncated)"));

        void PrintSteps(IReadOnlyList<OutlineStepDto> steps, int level)
        {
            var pad = new string(' ', level * 2);
            foreach (var s in steps)
            {
                var tail = $"L{s.Line}" + (s.Ctx is null ? "" : $" [{s.Ctx}]") + (s.Repeat > 1 ? $" x{s.Repeat}" : "");
                var conf = s.Confidence < 1 ? $" {s.Confidence:0.##}" : "";
                stdout.WriteLine($"{pad}{s.Order}. {(s.Expandable ? "+" : " ")} {s.Kind,-9} {s.Label}{conf}  @ {tail}");
                PrintSteps(s.Children, level + 1); // impl / dispatch 子项各自按同样规则展开
                if (s.Children.Count == 0 && ExpandTarget(s) is { } t) Expand(t, level + 1);
            }
        }

        long? ExpandTarget(OutlineStepDto s) => depth > 1 && s.Expandable && s.Target is { } t && !visited.Contains(t.Id) ? t.Id : null;

        void Expand(long target, int level)
        {
            visited.Add(target);
            PrintOutline(db, target, options, depth - 1, level, visited, stdout);
            visited.Remove(target);
        }
    }

    /// <summary>
    /// 影响范围（与 GET /api/symbols/{id}/impact 同一份数据）：先是入口汇总（按容器分组，行尾是往上走了几层），
    /// 再是调用方树（缩进一层 = 往上一层；Kind、置信度、各处行号与所在控制结构；本身是入口的标 ⇐；行尾 ↺ 是已在当前路径上的（递归），不再展开；
    /// 行尾 + 表示还能往上展开但超过了 --depth）。
    /// </summary>
    private static void PrintImpact(IndexDatabase db, long id, bool tests, int depth, TextWriter stdout)
    {
        if (ImpactQuery.Build(db, id, new ImpactOptions(Entries: true, Tests: tests)) is not { } impact) return;
        var entries = impact.Entries ?? [];
        stdout.WriteLine(L($"  入口（{entries.Count}）{(impact.EntriesTruncated ? $"：超过 {ImpactBuilder.MaxDepth} 层或 {ImpactBuilder.MaxNodes} 个节点，可能不全" : "")}", $"  Entry points ({entries.Count}){(impact.EntriesTruncated ? $": more than {ImpactBuilder.MaxDepth} levels or {ImpactBuilder.MaxNodes} nodes, may be incomplete" : "")}"));
        if (entries.Count == 0) stdout.WriteLine(L("    （往上没有走到入口）", "    (no entry point reached upwards)"));
        foreach (var e in entries.OrderBy(e => e.ContainerName, StringComparer.Ordinal).ThenBy(e => e.Depth))
            stdout.WriteLine(L($"    [{e.ContainerName}] {EntryText(e.Entry)}  ← 深度 {e.Depth}", $"    [{e.ContainerName}] {EntryText(e.Entry)}  ← depth {e.Depth}"));
        stdout.WriteLine(L("  调用方", "  Callers"));
        PrintCallers(impact, level: 2, remaining: depth, path: [id]);

        void PrintCallers(ImpactDto node, int level, int remaining, HashSet<long> path)
        {
            var pad = new string(' ', level * 2);
            if (node.Callers.Count == 0 && level == 2) stdout.WriteLine(pad + L("（没有调用方）", "(no callers)"));
            foreach (var c in node.Callers)
            {
                var recursive = path.Contains(c.Caller.Id);
                var expand = !recursive && c.Expandable && remaining > 1;
                var mark = recursive ? "  ↺" : c.Expandable && !expand ? "  +" : "";
                var sites = string.Join(" ", c.Sites.Select(s => $"L{s.Line}" + (s.Ctx is null ? "" : $"[{s.Ctx}]")));
                var conf = c.Confidence < 1 ? $" {c.Confidence:0.##}" : "";
                var entry = c.AsEntry.Count > 0 ? "  ⇐ " + string.Join(L("；", "; "), c.AsEntry.Select(e => EntryText(e.Entry))) : "";
                stdout.WriteLine($"{pad}← {c.Label}  {c.Kind}{conf}  @ {sites}{entry}{mark}");
                if (!expand || ImpactQuery.Build(db, c.Caller.Id, new ImpactOptions(Entries: false, Tests: tests)) is not { } up) continue;
                path.Add(c.Caller.Id);
                PrintCallers(up, level + 1, remaining - 1, path);
                path.Remove(c.Caller.Id);
            }
            if (node.Truncated) stdout.WriteLine(pad + L($"…（超过 {ImpactBuilder.MaxCallers} 个调用方，已截断）", $"… (more than {ImpactBuilder.MaxCallers} callers, truncated)"));
            if (node.TestCallers > 0 && !tests) stdout.WriteLine(pad + L($"（另有 {node.TestCallers} 个测试方法直接调用，--tests 显示）", $"({Plural(node.TestCallers, "more test method calls", "more test methods call")} it directly; use --tests to show)"));
        }
    }

    /// <summary>HTTP POST /api/orders；其余是"kind 路由或入口名"：handler CreateOrderCommand、hosted OrderWorker。</summary>
    private static string EntryText(EntryDto e) => e.Kind == "http"
        ? $"HTTP {(e.HttpMethod is null ? "" : e.HttpMethod + " ")}{e.Route ?? e.Name}"
        : $"{e.Kind} {e.Route ?? e.Name}";

    private static void Print(IndexReader reader, long id, Direction direction, IReadOnlySet<EdgeKind> kinds, int depth, int indent, HashSet<long> visited, TextWriter stdout)
    {
        var pad = new string(' ', indent * 2);
        var seenEdges = new HashSet<long>();
        foreach (var n in reader.GetNeighbors(id, direction, kinds))
        {
            // 自环边（递归调用）在 Both 方向下会被 outgoing 和 incoming 各返回一次，只打印一次
            if (!seenEdges.Add(n.Edge.Id)) continue;
            var isOutgoing = n.Edge.FromId == id;
            var arrow = isOutgoing ? "→" : "←";
            var name = n.Other?.QualifiedName ?? n.Edge.ToName + L("  (悬空)", "  (dangling)");
            stdout.WriteLine($"{pad}{arrow} {DbNames.ToDb(n.Edge.Kind)}  {name}  ({n.Edge.Confidence:0.00})  @ line {n.Edge.Line}");
            if (depth > 1 && n.Other is { } other && other.Kind != SymbolKind.External && visited.Add(other.Id))
                Print(reader, other.Id, isOutgoing ? Direction.Outgoing : Direction.Incoming, kinds, depth - 1, indent + 1, visited, stdout);
        }
    }
}
