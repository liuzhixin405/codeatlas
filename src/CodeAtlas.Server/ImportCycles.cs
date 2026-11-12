using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// <c>codeatlas arch &lt;repo&gt; --cycles files</c> 的结果：文件之间 import 的环。
/// </summary>
/// <param name="Edges">参与计算的文件间 import 边数（去重后，一对文件算一条）。</param>
/// <param name="Files">这些边涉及的文件数。</param>
/// <param name="IncludeHeuristic">是否把启发式边也算进去（--edges all）。</param>
/// <param name="SkippedHeuristic">没算进去的启发式文件间边数（--edges certain 时）；算进去时为 0。</param>
/// <param name="Cycles">每个强连通分量一项（≥ 2 个文件），按分量里最小的路径排序。</param>
public sealed record ImportCyclesDto(int Edges, int Files, bool IncludeHeuristic, int SkippedHeuristic, IReadOnlyList<ImportCycleDto> Cycles);

/// <param name="Files">分量里的全部文件（按路径排序）。</param>
/// <param name="Path">分量里的一个具体的环（从最小的文件出发、最短的那条回到它自己），首尾是同一个文件。</param>
/// <param name="Steps">Path 上每一步是哪一行的 import、是不是启发式边。</param>
public sealed record ImportCycleDto(IReadOnlyList<string> Files, IReadOnlyList<string> Path, IReadOnlyList<ImportStepDto> Steps);

/// <param name="Heuristic">这一步是启发式边：confidence &lt; 1，或一条 import 展开到多个文件（Go 包 = 目录下每个文件、Java 通配 import）。</param>
public sealed record ImportStepDto(string From, string To, int Line, double Confidence, bool Heuristic);

/// <summary>
/// 文件级 import 环检测（CI 用）。输入是索引里的 imports 边：起点是文件的模块符号，终点也是模块符号（某个文件）时才算文件间依赖；
/// C# 的 using 指向命名空间、外部包指向 external:…，都不是文件，不参与（C# 的命名空间 / 项目级环用 arch --dsm）。
/// <list type="bullet">
/// <item><b>确定边</b>（默认只算这些）：confidence 1.0，且这条 import 只解析到一个文件——TS/JS 的相对路径 / tsconfig 别名、Python 模块、Java 单类型 import。</item>
/// <item><b>启发式边</b>（--edges all 才算）：confidence &lt; 1.0；或一条 import 展开到同一行的多个文件（Go 的包 import 连到包目录下每个文件、
/// Java 的 <c>import a.b.*</c>）——这是包级依赖，换成文件级会凭空多出环。</item>
/// </list>
/// 环 = 强连通分量（Tarjan，与 DSM 同一个实现）；每个分量另给一条最短的具体环路径，方便定位要断开哪条 import。
/// </summary>
public static class ImportCycles
{
    private sealed record FileEdge(string From, string To, int Line, double Confidence, bool Heuristic);

    public static ImportCyclesDto Find(IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges, bool includeHeuristic = false)
    {
        var byId = symbols.ToDictionary(s => s.Id);
        var imports = edges.Where(e => e.Kind == EdgeKind.Imports
                && byId.TryGetValue(e.FromId, out var f) && f.Kind == SymbolKind.Module
                && byId.TryGetValue(e.ToId, out var t) && t.Kind == SymbolKind.Module)
            .ToList();
        // 同一个模块同一行 import 到几个不同文件：> 1 是包级 / 通配展开
        var fanout = imports.GroupBy(e => (e.FromId, e.Line)).ToDictionary(g => g.Key, g => g.Select(e => e.ToId).Distinct().Count());

        // 一对文件只留一条：确定边优先，其次行号小的
        var pairs = new Dictionary<(string, string), FileEdge>();
        foreach (var e in imports)
        {
            var from = byId[e.FromId].Path;
            var to = byId[e.ToId].Path;
            if (from == to) continue;
            var heuristic = e.Confidence < 1.0 || fanout[(e.FromId, e.Line)] > 1;
            var edge = new FileEdge(from, to, e.Line, e.Confidence, heuristic);
            if (!pairs.TryGetValue((from, to), out var cur) || (cur.Heuristic && !heuristic) || (cur.Heuristic == heuristic && e.Line < cur.Line))
                pairs[(from, to)] = edge;
        }
        var used = pairs.Values.Where(e => includeHeuristic || !e.Heuristic).ToList();
        var skipped = includeHeuristic ? 0 : pairs.Values.Count(e => e.Heuristic);

        var nodes = used.SelectMany(e => new[] { e.From, e.To }).Distinct(StringComparer.Ordinal).ToList();
        var adj = nodes.ToDictionary(n => n, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var edgeOf = new Dictionary<(string, string), FileEdge>();
        foreach (var e in used) { adj[e.From].Add(e.To); edgeOf[(e.From, e.To)] = e; }

        var (_, _, members) = DsmBuilder.DependencyOrder(nodes, adj, _ => 1);
        var cycles = new List<ImportCycleDto>();
        foreach (var group in members.Values.Where(m => m.Count > 1))
        {
            var files = group.OrderBy(f => f, StringComparer.Ordinal).ToList();
            var inGroup = files.ToHashSet(StringComparer.Ordinal);
            var path = ShortestCycle(files[0], adj, inGroup);
            var steps = path.Zip(path.Skip(1), (a, b) => edgeOf[(a, b)]).Select(e => new ImportStepDto(e.From, e.To, e.Line, e.Confidence, e.Heuristic)).ToList();
            cycles.Add(new ImportCycleDto(files, path, steps));
        }
        cycles.Sort((a, b) => StringComparer.Ordinal.Compare(a.Files[0], b.Files[0]));
        return new ImportCyclesDto(used.Count, nodes.Count, includeHeuristic, skipped, cycles);
    }

    /// <summary>分量内从 start 出发回到 start 的最短环（BFS，邻居按路径排序，结果确定）。</summary>
    private static List<string> ShortestCycle(string start, Dictionary<string, HashSet<string>> adj, HashSet<string> inGroup)
    {
        var prev = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>([start]);
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            foreach (var w in adj[v].Where(inGroup.Contains).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (w == start)
                {
                    var path = new List<string> { start };
                    for (var x = v; x != start; x = prev[x]) path.Add(x);
                    path.Add(start);
                    path.Reverse();
                    return path;
                }
                if (prev.ContainsKey(w)) continue;
                prev[w] = v;
                queue.Enqueue(w);
            }
        }
        return [start, start]; // 分量 ≥ 2 时一定有环，走不到这里
    }
}
