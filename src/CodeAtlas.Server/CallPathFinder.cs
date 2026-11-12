using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// GET /api/symbols/{from}/paths?to=&lt;id&gt; 与 CLI <c>query &lt;A&gt; --to &lt;B&gt;</c> 的结果：A 到 B 的调用路径（每条是 A → … → B 的一串边）。
/// </summary>
/// <param name="Paths">按步数升序；同样长的按"最弱一步的 confidence"降序，再按发现顺序。</param>
/// <param name="MaxDepth">本次用的最大步数（路径最多几条边）。</param>
/// <param name="MaxPaths">本次用的路径数上限。</param>
/// <param name="Truncated">还有没列出的路径（超过 MaxPaths），或搜索步数超过 <see cref="CallPathFinder.MaxExpansions"/> 提前停下。</param>
/// <param name="Flow">把全部路径按共同前缀合并成一棵树，与时序图同一个 FlowDto：面板直接用时序图渲染器画；参与者按所属类型（没有类型的按文件）分列。</param>
/// <param name="Note">没找到路径时的说明；有路径时为 null。</param>
public sealed record CallPathsDto(
    long FromId, string FromLabel, long ToId, string ToLabel, IReadOnlyList<CallPathDto> Paths, int MaxDepth, int MaxPaths, bool Truncated,
    FlowDto Flow, string? Note);

/// <param name="MinConfidence">路径上最弱一步的 confidence。</param>
/// <param name="Inferred">路径经过分派边（dispatches）或低置信度边（&lt; <see cref="CallPathFinder.WeakConfidence"/>）：不是每一步都有确定的直接调用证据。</param>
public sealed record CallPathDto(IReadOnlyList<CallPathStepDto> Steps, double MinConfidence, bool Inferred);

/// <summary>路径上的一步：FromId 调 / 分派到 ToId。</summary>
/// <param name="Kind">call（calls 边）/ dispatch（dispatches 分派边）。</param>
/// <param name="Via">边的来历：impl（接口 → 实现派生）、render（JSX 渲染）、callback（函数作为实参传出）、dispatch:T（分派的消息类型）；普通调用为 null。</param>
/// <param name="Path">调用处所在文件（调用方的文件）。</param>
/// <param name="Weak">这一步是分派边或 confidence &lt; <see cref="CallPathFinder.WeakConfidence"/>：图里画虚线。</param>
public sealed record CallPathStepDto(
    long FromId, long ToId, string Label, string QualifiedName, string Kind, string? Via, double Confidence, string Path, int Line, int? Pos, bool Weak);

/// <summary>
/// 两点间调用路径：从 A 出发沿运行时去向（calls，含 via=impl / render / callback 的派生边；dispatches 分派边）找到 B 的简单路径（不重复经过同一个符号）。
/// <list type="bullet">
/// <item>先从 B 沿反向边做一趟有界 BFS 得到"到 B 还差几步"，深度优先只走还来得及到 B 的边，所以不会在到不了 B 的分支里打转。</item>
/// <item>按步数逐层加深：先列最短的，同样长的按边的 confidence 从高到低走；凑够 MaxPaths 条就停。</item>
/// <item>同一对 (调用方, 被调方) 多条边（同一方法里调了两次、或接口派生边与直接边并存）只取 confidence 最高、行号最靠前的一条。</item>
/// <item>不走 conduit（分派管道本身：调 mediator.Send 的那一下，dispatches 边已经直接指向 Handler）与 dispatches 的 handles（Handler → 消息类型）；
/// calls 边低于 <see cref="MinConfidence"/> 的不走（与影响范围、调用大纲口径一致，0.3 的多候选边会走、但标成低置信度）。</item>
/// <item>外部符号（external:…）不能当中间节点（索引里没有它的实现），但可以当终点 B。</item>
/// </list>
/// 默认值（<see cref="DefaultMaxDepth"/> 步、<see cref="DefaultMaxPaths"/> 条）写在这里，CLI 与服务端共用。
/// </summary>
public static class CallPathFinder
{
    public const int DefaultMaxDepth = 8;
    public const int MaxDepthLimit = 12;
    public const int DefaultMaxPaths = 10;
    public const int MaxPathsLimit = 50;
    /// <summary>calls 边低于这个 confidence 不走。</summary>
    public const double MinConfidence = 0.3;
    /// <summary>低于这个 confidence 的一步算"低置信度"（与调用图里边变细的门槛一致），图里画虚线。</summary>
    public const double WeakConfidence = 0.7;
    /// <summary>深度优先最多展开这么多次边，超过就停下并标 Truncated（防止稠密图里组合爆炸）。</summary>
    public const int MaxExpansions = 200_000;

    private static readonly HashSet<string> SkipVia = ["conduit", "handles"];

    internal static bool IsPathEdge(MapEdge e) =>
        (e.Kind == EdgeKind.Calls && e.Confidence >= MinConfidence && e.Via != "conduit")
        || (e.Kind == EdgeKind.Dispatches && e.Via is not null && !SkipVia.Contains(e.Via));

    internal static bool IsWeak(MapEdge e) => e.Kind == EdgeKind.Dispatches || e.Confidence < WeakConfidence;

    public static CallPathsDto Find(
        IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges, long fromId, long toId,
        int maxDepth = DefaultMaxDepth, int maxPaths = DefaultMaxPaths)
    {
        maxDepth = Math.Clamp(maxDepth, 1, MaxDepthLimit);
        maxPaths = Math.Clamp(maxPaths, 1, MaxPathsLimit);
        var byId = symbols.ToDictionary(s => s.Id);
        var from = byId[fromId];
        var to = byId[toId];

        // 每对 (from, to) 只留一条：confidence 最高，同样高的取行号小的
        var best = new Dictionary<(long, long), MapEdge>();
        foreach (var e in edges)
        {
            if (!IsPathEdge(e) || e.FromId == e.ToId || !byId.ContainsKey(e.ToId) || !byId.ContainsKey(e.FromId)) continue;
            var key = (e.FromId, e.ToId);
            if (!best.TryGetValue(key, out var cur) || e.Confidence > cur.Confidence || (e.Confidence == cur.Confidence && e.Line < cur.Line)) best[key] = e;
        }
        var outgoing = best.Values
            .OrderByDescending(e => e.Confidence).ThenBy(e => e.Kind == EdgeKind.Dispatches ? 1 : 0).ThenBy(e => e.Line).ThenBy(e => e.ToId)
            .ToLookup(e => e.FromId);
        var incoming = best.Values.ToLookup(e => e.ToId);

        // 反向 BFS：dist[v] = v 到 B 的最少步数（≤ maxDepth）
        var dist = new Dictionary<long, int> { [toId] = 0 };
        var queue = new Queue<long>([toId]);
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            if (dist[v] >= maxDepth) continue;
            foreach (var e in incoming[v])
            {
                if (dist.ContainsKey(e.FromId) || byId[e.FromId].Kind == SymbolKind.External) continue;
                dist[e.FromId] = dist[v] + 1;
                queue.Enqueue(e.FromId);
            }
        }

        var found = new List<List<MapEdge>>();
        var truncated = false;
        var expansions = 0;
        if (fromId != toId && dist.TryGetValue(fromId, out var shortest))
        {
            var path = new List<MapEdge>();
            var onPath = new HashSet<long> { fromId };
            // 逐层加深：第 len 轮只收恰好 len 步的路径（更短的上一轮已经收了）
            for (var len = shortest; len <= maxDepth && !truncated; len++)
            {
                var layer = new List<List<MapEdge>>();
                Dfs(fromId, len, layer);
                found.AddRange(layer.OrderByDescending(p => p.Min(e => e.Confidence))); // 稳定排序：同样弱的保持发现顺序
                if (found.Count > maxPaths) { truncated = true; break; }
            }
            if (found.Count > maxPaths) found = found.Take(maxPaths).ToList();

            void Dfs(long v, int remaining, List<List<MapEdge>> into)
            {
                foreach (var e in outgoing[v])
                {
                    if (truncated) return;
                    // 多收一条用来判断"还有没列出的"
                    if (found.Count + into.Count > maxPaths) { truncated = true; return; }
                    if (++expansions > MaxExpansions) { truncated = true; return; }
                    var w = e.ToId;
                    if (onPath.Contains(w) || !dist.TryGetValue(w, out var dw) || dw > remaining - 1) continue;
                    if (w == toId)
                    {
                        if (remaining == 1) into.Add([.. path, e]);
                        continue; // 终点不再往下走
                    }
                    if (byId[w].Kind == SymbolKind.External) continue;
                    path.Add(e); onPath.Add(w);
                    Dfs(w, remaining - 1, into);
                    path.RemoveAt(path.Count - 1); onPath.Remove(w);
                }
            }
        }

        var dtos = found.Select(p => new CallPathDto(
            p.Select(e => Step(e, byId)).ToList(),
            p.Min(e => e.Confidence),
            p.Any(IsWeak))).ToList();
        string? note = dtos.Count > 0 ? null
            : fromId == toId ? L("起点和终点是同一个符号", "Start and end are the same symbol")
            : expansions > MaxExpansions ? L($"搜索超过 {MaxExpansions} 步仍没找到，可能是图太稠密", $"No path found within {MaxExpansions} search steps; the graph may be too dense")
            : L($"{maxDepth} 步之内没有从 {Label(from, byId)} 到 {Label(to, byId)} 的调用路径（只走 calls 与分派边，置信度 ≥ {MinConfidence}）",
                $"No call path from {Label(from, byId)} to {Label(to, byId)} within {maxDepth} steps (only calls and dispatch edges with confidence ≥ {MinConfidence})");
        var flow = ToFlow(from, to, dtos, truncated, note, byId);
        return new CallPathsDto(fromId, Label(from, byId), toId, Label(to, byId), dtos, maxDepth, maxPaths, truncated, flow, note);
    }

    private static CallPathStepDto Step(MapEdge e, IReadOnlyDictionary<long, MapSymbol> byId)
    {
        var t = byId[e.ToId];
        return new CallPathStepDto(e.FromId, e.ToId, Label(t, byId), t.QualifiedName, e.Kind == EdgeKind.Dispatches ? "dispatch" : "call", e.Via,
            e.Confidence, byId[e.FromId].Path, e.Line, e.Pos, IsWeak(e));
    }

    /// <summary>"类型.方法"；构造函数与类型同名时只写类型名；没有所属类型的写自己的名字（模块写文件名）。</summary>
    internal static string Label(MapSymbol s, IReadOnlyDictionary<long, MapSymbol> byId)
    {
        if (s.Kind == SymbolKind.Module) return MapSymbol.FileName(s.Path);
        if (s.Kind == SymbolKind.External)
        {
            // 外部符号写最后两段（Db.Open、ILogger.LogInformation），与调用图一致
            var parts = s.QualifiedName["external:".Length..].Split('.');
            return string.Join(".", parts.Skip(Math.Max(0, parts.Length - 2)));
        }
        if (s.Kind is SymbolKind.Method or SymbolKind.Function && Owner(s, byId) is { } owner)
            return owner.Name == s.Name ? s.Name : owner.Name + "." + s.Name;
        return s.Name;
    }

    private static MapSymbol? Owner(MapSymbol s, IReadOnlyDictionary<long, MapSymbol> byId) =>
        s.ParentId is { } p && byId.TryGetValue(p, out var o) && o.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum ? o : null;

    /// <summary>
    /// 路径合并成树：根是 A（kind = entry），每条路径按共同前缀挂下去（同一个父节点下同一个目标、同一种边只出现一次）。
    /// 节点 Kind：call / dispatch；Note：低置信度的一步写"低置信度"，图里虚线。参与者：所属类型（没有就按文件），外部符号一列 external。
    /// </summary>
    private static FlowDto ToFlow(MapSymbol from, MapSymbol to, IReadOnlyList<CallPathDto> paths, bool truncated, string? note, IReadOnlyDictionary<long, MapSymbol> byId)
    {
        var actors = new Dictionary<string, FlowActorDto>(StringComparer.Ordinal);
        string ActorOf(MapSymbol s)
        {
            if (s.Kind == SymbolKind.External) { actors.TryAdd("ext|", new FlowActorDto("ext|", L("外部 / 框架", "External / framework"), "external")); return "ext|"; }
            var owner = Owner(s, byId);
            var key = owner is not null ? "type|" + owner.QualifiedName : "file|" + s.Path;
            actors.TryAdd(key, new FlowActorDto(key, owner?.Name ?? MapSymbol.FileName(s.Path), "project"));
            return key;
        }

        var rootActor = ActorOf(from);
        var root = new Node(from.Id, "entry", null, 1.0, rootActor, null, from.Path, from.StartLine > 0 ? from.StartLine : null, null, 0);
        var count = 1;
        foreach (var p in paths)
        {
            var cur = root;
            foreach (var s in p.Steps)
            {
                var next = cur.Children.FirstOrDefault(c => c.SymbolId == s.ToId && c.Kind == s.Kind);
                if (next is null)
                {
                    next = new Node(s.ToId, s.Kind, s.Via, s.Confidence, ActorOf(byId[s.ToId]), s.Weak && s.Kind == "call" ? L("低置信度", "low confidence") : null,
                        s.Path, s.Line > 0 ? s.Line : null, s.Pos, cur.Depth + 1);
                    cur.Children.Add(next);
                    count++;
                }
                cur = next;
            }
        }

        FlowNodeDto ToDto(Node n) => new(n.SymbolId, Label(byId[n.SymbolId], byId), byId[n.SymbolId].QualifiedName, n.Kind, n.Actor, n.Via, n.Confidence,
            1, n.Depth, n.Children.Select(ToDto).ToList(), 0, n.Note, n.Path, n.Line, n.Pos);

        // 起点列在最左，其余按首次出现（字典保持插入顺序），外部列放最后
        var ordered = actors.Values.Where(a => a.Kind != "external").Concat(actors.Values.Where(a => a.Kind == "external")).ToList();
        return new FlowDto(from.Id, Label(from, byId) + " → " + Label(to, byId), null, "", ordered, ToDto(root), [], count, truncated, note);
    }

    private sealed record Node(long SymbolId, string Kind, string? Via, double Confidence, string Actor, string? Note, string? Path, int? Line, int? Pos, int Depth)
    {
        public List<Node> Children { get; } = [];
    }
}
