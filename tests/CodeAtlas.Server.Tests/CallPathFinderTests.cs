using System.Net;
using System.Net.Http.Json;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>两点间调用路径（CallPathFinder）：最短优先、分派边与低置信度边标记、环、conduit、外部符号、上限与截断、合并成时序树。</summary>
public class CallPathFinderTests
{
    private static MapSymbol Sym(long id, SymbolKind kind, string qname, string path, long? parent = null) => new(id, kind, qname, path, parent, 1, 9);

    // Ctl.Create ─calls 1.0→ Svc.Place ─calls 1.0→ Repo.Save
    //            ─dispatch→  Handler.Handle ─calls 0.9→ Repo.Save
    //            ─calls 0.3→ Util.Log ─calls 1.0→ Repo.Save（多候选之一）
    // Svc.Place ─calls 1.0→ Svc.Check ─calls 1.0→ Svc.Place（环）；Svc.Place ─conduit→ Bus.Send ─calls→ Repo.Save（不走）
    // Svc.Place ─calls→ external:Db.Open（外部符号不当中间节点）
    private static readonly List<MapSymbol> Symbols =
    [
        Sym(1, SymbolKind.Class, "Acme.Ctl", "Api/Ctl.cs"),
        Sym(2, SymbolKind.Method, "Acme.Ctl.Create()", "Api/Ctl.cs", 1),
        Sym(10, SymbolKind.Class, "Acme.Svc", "App/Svc.cs"),
        Sym(11, SymbolKind.Method, "Acme.Svc.Place()", "App/Svc.cs", 10),
        Sym(12, SymbolKind.Method, "Acme.Svc.Check()", "App/Svc.cs", 10),
        Sym(20, SymbolKind.Class, "Acme.Repo", "Infra/Repo.cs"),
        Sym(21, SymbolKind.Method, "Acme.Repo.Save()", "Infra/Repo.cs", 20),
        Sym(30, SymbolKind.Class, "Acme.Handler", "App/Handler.cs"),
        Sym(31, SymbolKind.Method, "Acme.Handler.Handle(Cmd)", "App/Handler.cs", 30),
        Sym(40, SymbolKind.Function, "util.ts:log", "util.ts"),
        Sym(50, SymbolKind.Method, "Acme.Bus.Send()", "App/Bus.cs"),
        Sym(90, SymbolKind.External, "external:Db.Open", "<external>"),
    ];

    private static readonly List<MapEdge> Edges =
    [
        new(2, 11, EdgeKind.Calls, 1.0, Line: 5),
        new(2, 11, EdgeKind.Calls, 0.7, Line: 3),          // 同一对的弱边：只留 1.0 那条
        new(2, 31, EdgeKind.Dispatches, 1.0, "dispatch:Cmd", Line: 6),
        new(2, 40, EdgeKind.Calls, 0.3, Line: 7),
        new(11, 21, EdgeKind.Calls, 1.0, Line: 12),
        new(11, 12, EdgeKind.Calls, 1.0, Line: 13),
        new(12, 11, EdgeKind.Calls, 1.0, Line: 20),
        new(11, 50, EdgeKind.Calls, 1.0, "conduit", Line: 14),
        new(50, 21, EdgeKind.Calls, 1.0, Line: 2),
        new(11, 90, EdgeKind.Calls, 0.5, Line: 15),
        new(31, 21, EdgeKind.Calls, 0.9, Line: 30),
        new(40, 21, EdgeKind.Calls, 1.0, Line: 1),
        new(31, 2, EdgeKind.Dispatches, 1.0, "handles", Line: 1), // handles 不是运行时去向
    ];

    private static string Render(CallPathDto p) => string.Join(" ", p.Steps.Select(s => (s.Weak ? "⇢" : "→") + s.Label));

    [Fact]
    public void ListsAllPaths_ShortestFirst_StrongestFirstAmongEqualLength_MarksDispatchAndWeakEdges()
    {
        var r = CallPathFinder.Find(Symbols, Edges, 2, 21);
        Assert.Equal(new[]
        {
            "→Svc.Place →Repo.Save",
            "⇢Handler.Handle →Repo.Save",
            "⇢log →Repo.Save",
        }, r.Paths.Select(Render));
        Assert.Equal(new[] { 1.0, 0.9, 0.3 }, r.Paths.Select(p => p.MinConfidence));
        Assert.Equal(new[] { false, true, true }, r.Paths.Select(p => p.Inferred));
        var dispatch = r.Paths[1].Steps[0];
        Assert.Equal(("dispatch", "dispatch:Cmd", "Api/Ctl.cs", 6), (dispatch.Kind, dispatch.Via, dispatch.Path, dispatch.Line));
        Assert.Equal((5, 1.0), (r.Paths[0].Steps[0].Line, r.Paths[0].Steps[0].Confidence)); // 同一对取 confidence 最高的那条边
        Assert.Equal(("Ctl.Create", "Repo.Save", false, (string?)null), (r.FromLabel, r.ToLabel, r.Truncated, r.Note));
        Assert.Equal((CallPathFinder.DefaultMaxDepth, CallPathFinder.DefaultMaxPaths), (r.MaxDepth, r.MaxPaths));
    }

    [Fact]
    public void FlowTree_MergesPaths_UnderTheStart_WithKindsActorsAndWeakNote()
    {
        var f = CallPathFinder.Find(Symbols, Edges, 2, 21).Flow;
        Assert.Equal(("Ctl.Create → Repo.Save", "entry", 2L, 7), (f.EntryLabel, f.Root.Kind, f.EntrySymbolId, f.Nodes));
        Assert.Equal(new[] { ("Svc.Place", "call", (string?)null), ("Handler.Handle", "dispatch", (string?)null), ("log", "call", (string?)"低置信度") },
            f.Root.Children.Select(c => (c.Label, c.Kind, c.Note)));
        Assert.All(f.Root.Children, c => Assert.Equal("Repo.Save", Assert.Single(c.Children).Label));
        Assert.Equal(new[] { "type|Acme.Ctl", "type|Acme.Svc", "type|Acme.Repo", "type|Acme.Handler", "file|util.ts" }, f.Actors.Select(a => a.Key));
        Assert.Equal(("Svc", "type|Acme.Svc"), (f.Actors[1].Label, f.Root.Children[0].Actor));
    }

    [Fact]
    public void DepthAndPathLimits_AreHonoured_AndReportTruncation()
    {
        var two = CallPathFinder.Find(Symbols, Edges, 2, 21, maxPaths: 2);
        Assert.Equal(2, two.Paths.Count);
        Assert.True(two.Truncated);

        var one = CallPathFinder.Find(Symbols, Edges, 2, 21, maxDepth: 1);
        Assert.Empty(one.Paths);
        Assert.False(one.Truncated);
        Assert.Equal("1 步之内没有从 Ctl.Create 到 Repo.Save 的调用路径（只走 calls 与分派边，置信度 ≥ 0.3）", one.Note);
        Assert.Equal(1, one.Flow.Nodes);
    }

    [Fact]
    public void ConduitHandlesAndExternals_AreNotWalked_CyclesDoNotRepeat_ExternalCanBeTheEnd()
    {
        // Bus.Send 只经 conduit 边可达：没有路径
        Assert.Empty(CallPathFinder.Find(Symbols, Edges, 2, 50).Paths);
        // Handler → Ctl 只有 handles 边
        Assert.Empty(CallPathFinder.Find(Symbols, Edges, 31, 2).Paths);
        // 环：Svc.Check → Svc.Place → Repo.Save，不会再绕回 Check
        Assert.Equal(new[] { "→Svc.Place →Repo.Save" }, CallPathFinder.Find(Symbols, Edges, 12, 21).Paths.Select(Render));
        // 外部符号可以当终点（0.5 < 0.7 算低置信度）
        var ext = CallPathFinder.Find(Symbols, Edges, 2, 90);
        Assert.Equal(new[] { "→Svc.Place ⇢Db.Open" }, ext.Paths.Select(Render));
        Assert.Equal("ext|", ext.Flow.Actors[^1].Key);
        Assert.Equal("起点和终点是同一个符号", CallPathFinder.Find(Symbols, Edges, 2, 2).Note);
    }
}

/// <summary>HTTP 层：GET /api/symbols/{id}/paths?to= 在真实 tree-sitter 索引的小 TS 仓库上。</summary>
public class CallPathApiTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private HttpClient C => fx.Client;

    private async Task<long> IdOf(string query, string qualifiedName) =>
        (await C.GetFromJsonAsync<List<SymbolDto>>($"/api/search?q={Uri.EscapeDataString(query)}"))!.Single(s => s.QualifiedName == qualifiedName).Id;

    [Fact]
    public async Task Paths_FromMainToHelper_GoThroughSvcFind()
    {
        var main = await IdOf("main", "src/app.ts:main");
        var helper = await IdOf("helper", "src/util/format.ts:helper");
        var r = (await C.GetFromJsonAsync<CallPathsDto>($"/api/symbols/{main}/paths?to={helper}"))!;
        var p = Assert.Single(r.Paths);
        Assert.Equal(new[] { ("Svc.find", "src/app.ts", 2, false), ("helper", "src/svc.ts", 3, false) }, p.Steps.Select(s => (s.Label, s.Path, s.Line, s.Weak)));
        Assert.Equal("main → helper", r.Flow.EntryLabel);

        var none = (await C.GetFromJsonAsync<CallPathsDto>($"/api/symbols/{helper}/paths?to={main}&depth=3"))!;
        Assert.Empty(none.Paths);
        Assert.Equal(3, none.MaxDepth);
        Assert.Equal(HttpStatusCode.NotFound, (await C.GetAsync($"/api/symbols/{main}/paths?to=987654321")).StatusCode);
    }
}
