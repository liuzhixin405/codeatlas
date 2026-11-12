using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>文件级 import 环：确定边 / 启发式边（confidence &lt; 1、一条 import 展开到多个文件）、强连通分量与最短环路径。</summary>
public class ImportCyclesTests
{
    private static MapSymbol Module(long id, string path) => new(id, SymbolKind.Module, path, path, null);

    // a → b → c → a（确定边）；d → a（不在环里）；c → c 自己不算；
    // e ⇄ f 只靠一条展开到 f、g 两个文件的 import（同一行，包级）闭合：启发式；
    // h → i 0.95（启发式），i → h 确定：只有 --edges all 才成环；a → Namespace 不是文件
    private static readonly List<MapSymbol> Symbols =
    [
        Module(1, "src/a.ts"), Module(2, "src/b.ts"), Module(3, "src/c.ts"), Module(4, "src/d.ts"),
        Module(5, "pkg/e.go"), Module(6, "lib/f.go"), Module(7, "lib/g.go"),
        Module(8, "h.py"), Module(9, "i.py"),
        new(20, SymbolKind.Namespace, "Acme", "Acme.cs", null),
    ];

    private static readonly List<MapEdge> Edges =
    [
        new(1, 2, EdgeKind.Imports, 1.0, Line: 1),
        new(2, 3, EdgeKind.Imports, 1.0, Line: 2),
        new(3, 1, EdgeKind.Imports, 1.0, Line: 3),
        new(3, 3, EdgeKind.Imports, 1.0, Line: 4),
        new(4, 1, EdgeKind.Imports, 1.0, Line: 1),
        new(1, 20, EdgeKind.Imports, 1.0, Line: 5),
        new(1, 2, EdgeKind.Calls, 1.0, Line: 9),      // 不是 import
        new(5, 6, EdgeKind.Imports, 1.0, Line: 3),
        new(5, 7, EdgeKind.Imports, 1.0, Line: 3),
        new(6, 5, EdgeKind.Imports, 1.0, Line: 2),
        new(8, 9, EdgeKind.Imports, 0.95, Line: 1),
        new(9, 8, EdgeKind.Imports, 1.0, Line: 1),
    ];

    [Fact]
    public void CertainEdgesOnly_ByDefault_FindsTheTsCycle_WithItsShortestPath()
    {
        var r = ImportCycles.Find(Symbols, Edges);
        var c = Assert.Single(r.Cycles);
        Assert.Equal(new[] { "src/a.ts", "src/b.ts", "src/c.ts" }, c.Files);
        Assert.Equal(new[] { "src/a.ts", "src/b.ts", "src/c.ts", "src/a.ts" }, c.Path);
        Assert.Equal(new[] { ("src/a.ts", 1), ("src/b.ts", 2), ("src/c.ts", 3) }, c.Steps.Select(s => (s.From, s.Line)));
        Assert.All(c.Steps, s => Assert.False(s.Heuristic));
        // 确定边：a→b、b→c、c→a、d→a、f→e、i→h；启发式：e→f、e→g（同一行展开到两个文件）、h→i（0.95）
        Assert.Equal((6, 8, false, 3), (r.Edges, r.Files, r.IncludeHeuristic, r.SkippedHeuristic));
    }

    [Fact]
    public void AllEdges_AddsPackageFanoutAndLowConfidenceCycles_MarkedHeuristic()
    {
        var r = ImportCycles.Find(Symbols, Edges, includeHeuristic: true);
        Assert.Equal(new[] { "h.py", "lib/f.go", "src/a.ts" }, r.Cycles.Select(c => c.Files[0]));
        var go = r.Cycles[1];
        Assert.Equal(new[] { "lib/f.go", "pkg/e.go", "lib/f.go" }, go.Path);
        Assert.Equal(new[] { false, true }, go.Steps.Select(s => s.Heuristic));
        var py = r.Cycles[0];
        Assert.Equal(new[] { ("h.py", 0.95, true), ("i.py", 1.0, false) }, py.Steps.Select(s => (s.From, s.Confidence, s.Heuristic)));
        Assert.Equal((9, 0), (r.Edges, r.SkippedHeuristic));
    }

    [Fact]
    public void NoImports_NoCycles()
    {
        var r = ImportCycles.Find(Symbols, [new(4, 1, EdgeKind.Imports, 1.0, Line: 1)]);
        Assert.Empty(r.Cycles);
        Assert.Equal((1, 2), (r.Edges, r.Files));
    }
}
