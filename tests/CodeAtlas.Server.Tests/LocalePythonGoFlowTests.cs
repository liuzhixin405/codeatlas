using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 框架流程 · Python / Go 的英文输出：阶段名、节点标签、Note、Notes 都是英文，且没有中文残留；
/// gin.Default() 内置中间件的识别（按 Note 里的 "gin.Default()"）在英文下照样生效。
/// </summary>
[Collection("Locale")]
public class LocalePythonGoFlowTests
{
    private static List<FlowNodeDto> All(FlowNodeDto n) => [n, .. n.Children.SelectMany(All)];

    private static void NoChinese(FrameworkFlowDto f)
    {
        Assert.DoesNotMatch("[一-鿿]", f.Flow.EntryLabel);
        Assert.All(f.Stages, s => Assert.DoesNotMatch("[一-鿿]", s.Label));
        Assert.All(f.Flow.Actors, a => Assert.DoesNotMatch("[一-鿿]", a.Label));
        Assert.All(All(f.Flow.Root), n =>
        {
            Assert.DoesNotMatch("[一-鿿]", n.Label);
            Assert.DoesNotMatch("[一-鿿]", n.Note ?? "");
        });
        Assert.All(f.Notes, n => Assert.DoesNotMatch("[一-鿿]", n));
    }

    // ================= FastAPI =================

    private const string FastApiMain =
        "from fastapi import Depends, FastAPI\n" +                         // 1
        "from starlette.middleware.cors import CORSMiddleware\n" +         // 2
        "\n" +                                                             // 3
        "app = FastAPI()\n" +                                              // 4
        "app.add_middleware(CORSMiddleware, allow_origins=[\"*\"])\n" +    // 5
        "\n" +                                                             // 6
        "\n" +                                                             // 7
        "def get_db():\n" +                                                // 8
        "    return 1\n" +                                                 // 9
        "\n" +                                                             // 10
        "\n" +                                                             // 11
        "@app.get(\"/ping\")\n" +                                          // 12
        "def ping(db=Depends(get_db)):\n" +                                // 13
        "    return db\n";                                                 // 14

    private static FrameworkInput FastApiInput()
    {
        List<MapSymbol> symbols =
        [
            new(1, SymbolKind.Module, "svc/main.py", "svc/main.py", null, 1, 14),
            new(2, SymbolKind.Function, "svc.main.get_db", "svc/main.py", 1, 8, 9),
            new(3, SymbolKind.Function, "svc.main.ping", "svc/main.py", 1, 13, 14),
        ];
        var container = new ArchNode("svc", ArchNodeKind.Container, "python-service", "svc", "svc", ArchSource.Discovered, [], ["svc"], []);
        var files = new Dictionary<string, string> { ["svc/main.py"] = FastApiMain };
        var signatures = new Dictionary<long, string> { [2] = "def get_db():", [3] = "def ping(db=Depends(get_db)):" };
        return new FrameworkInput(symbols, [], container, [new(3, "svc", "python.fastapi", "http", "/ping", "GET", "svc/main.py", 13)], [],
            new FrameworkSources
            {
                Calls = _ => [],
                Signature = id => signatures.GetValueOrDefault(id),
                ReadFile = p => files.GetValueOrDefault(p),
            });
    }

    [Fact]
    public void FastApi_StagesNodesAndNotes_InEnglish()
    {
        using var _ = Loc.Use("en");
        var f = PythonFrameworkFlow.Build(FastApiInput());

        Assert.Equal(["Middleware", "Routes", "Dependencies"], f.Stages.Select(s => s.Label));
        Assert.Equal("HTTP request", f.Flow.EntryLabel);
        Assert.Equal("HTTP request", f.Flow.Root.Label);

        var cors = f.Flow.Root.Children[0];
        Assert.Equal("CORSMiddleware", cors.Label);
        Assert.Equal("Built-in / third-party (add_middleware, main.py:5): CORS: answers preflight requests directly and adds CORS headers to the rest", cors.Note);

        var routes = f.Flow.Root.Children[1];
        Assert.Equal(("Routes", "controllers", 1), (routes.Label, routes.Kind, routes.Count));
        var dep = Assert.Single(routes.Children);
        Assert.Equal(("get_db", "used by 1 route"), (dep.Label, dep.Note));

        Assert.Contains("Recognized FastAPI: main.py:4 app = FastAPI(...)", f.Notes);
        Assert.Contains(f.Notes, n => n.StartsWith("Middleware is in reverse registration order: the last one added via add_middleware is outermost", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.StartsWith("Dependencies are sorted by how many routes use them (1 HTTP entry point in total;", StringComparison.Ordinal));
        Assert.Contains("No exception_handler: HTTPException and validation errors use FastAPI's default handling (JSON error response)", f.Notes);
        NoChinese(f);
    }

    [Fact]
    public void FastApi_ChineseUnchanged_ByDefault()
    {
        var f = PythonFrameworkFlow.Build(FastApiInput());
        Assert.Equal(["中间件", "路由", "依赖"], f.Stages.Select(s => s.Label));
        Assert.Equal("1 个路由用到", Assert.Single(f.Flow.Root.Children[1].Children).Note);
    }

    // ================= Go (gin) =================

    private const string GinMain =
        "package main\n" +                          // 1
        "\n" +                                      // 2
        "import (\n" +                              // 3
        "\t\"github.com/gin-gonic/gin\"\n" +        // 4
        ")\n" +                                     // 5
        "\n" +                                      // 6
        "func main() {\n" +                         // 7
        "\tr := gin.Default()\n" +                  // 8
        "\tr.GET(\"/ping\", ping)\n" +              // 9
        "\tr.Run()\n" +                             // 10
        "}\n" +                                     // 11
        "\n" +                                      // 12
        "func ping(c *gin.Context) {\n" +           // 13
        "}\n";                                      // 14

    private static FrameworkInput GinInput()
    {
        List<MapSymbol> symbols =
        [
            new(5000, SymbolKind.Module, "main.go", "main.go", null, 1, 14),
            new(1, SymbolKind.Function, "shop.main", "main.go", null, 7, 11),
            new(2, SymbolKind.Function, "shop.ping", "main.go", null, 13, 14),
        ];
        var files = new Dictionary<string, string> { ["main.go"] = GinMain };
        return new FrameworkInput(symbols, [], new ArchNode("shop", ArchNodeKind.Container, "go-service", "shop", "", ArchSource.Discovered, [], [""], []),
            [new EntryPoint(2, "shop", "go.gin", "http", "/ping", "GET", "main.go", 9)], [],
            new FrameworkSources
            {
                Calls = _ => [],
                Signature = _ => null,
                ReadFile = p => files.GetValueOrDefault(p),
            });
    }

    [Fact]
    public void Gin_StagesNodesAndNotes_InEnglish()
    {
        using var _ = Loc.Use("en");
        var f = GoFrameworkFlow.Build(GinInput());

        Assert.Equal(["Middleware", "Routes", "Teardown"], f.Stages.Select(s => s.Label));
        Assert.Equal(("HTTP request", "HTTP request"), (f.Flow.EntryLabel, f.Flow.Root.Label));

        var kids = f.Flow.Root.Children;
        Assert.Equal(["gin.Logger()", "gin.Recovery()", "Routes", "gin.Recovery()", "gin.Logger()"], kids.Select(k => k.Label));
        Assert.Equal("gin.Default() built-in (main.go:8)", kids[0].Note);
        Assert.Equal("gin route tree: finds the handler by method + path (route group middleware is merged into each route's handler chain at registration)", kids[2].Note);
        Assert.Equal("gin.Recovery after: recovers from panics and responds with 500 (gin.Default() built-in)", kids[3].Note);
        Assert.Equal("gin.Logger after: writes the access log after the request completes (duration, status code) (gin.Default() built-in)", kids[4].Note);

        Assert.Contains("Recognized gin (main.go:8 r := gin.Default())", f.Notes);
        Assert.Contains("gin.Default() comes with the Logger and Recovery middleware, placed first; their after parts (access log, panic recovery) are in the teardown column", f.Notes);
        // 两个内置中间件靠 Note 里的 "gin.Default()" 认出，不算 r.Use：英文下也走"没有 r.Use"这条
        Assert.Contains("No r.Use: the only global middleware is gin.Default()'s built-in Logger and Recovery", f.Notes);
        Assert.DoesNotContain(f.Notes, n => n.StartsWith("Global middleware follows", StringComparison.Ordinal));
        Assert.Contains("Route-level middleware (r.GET(path, mw, handler), chi's r.With) is not drawn", f.Notes);
        NoChinese(f);
    }

    [Fact]
    public void Gin_ChineseUnchanged_ByDefault()
    {
        var f = GoFrameworkFlow.Build(GinInput());
        Assert.Equal(["中间件", "路由", "收尾"], f.Stages.Select(s => s.Label));
        Assert.Equal("gin.Default() 内置（main.go:8）", f.Flow.Root.Children[0].Note);
        Assert.Contains("没有 r.Use：全局中间件只有 gin.Default() 内置的 Logger、Recovery", f.Notes);
    }
}
