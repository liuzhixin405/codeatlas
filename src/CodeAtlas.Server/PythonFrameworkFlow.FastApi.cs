using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using static CodeAtlas.Server.FrameworkFlowBuilder;

namespace CodeAtlas.Server;

/// <summary>
/// 框架流程 · FastAPI / Starlette。阶段：中间件 → 路由（占位）→ 依赖 → 收尾。
/// 中间件只认应用对象上的注册（app 所在文件）：add_middleware(X) 与 @app.middleware("http")，Starlette 每次把新的插到栈顶，所以按注册倒序画（后加的最外层）；
/// FastAPI(middleware=[Middleware(X)]) 在它们里面、按列表顺序。依赖见 <see cref="Dep"/>。
/// </summary>
public static partial class PythonFrameworkFlow
{
    private sealed record PyApp(string Path, string Var, int Pos, int OpenParen, int CloseParen);

    [GeneratedRegex(@"^[ \t]*(\w+)\s*(?::\s*[\w.]+\s*)?=\s*(?:[\w.]+\.)?FastAPI\s*\(", RegexOptions.Multiline)]
    private static partial Regex FastApiDecl();

    [GeneratedRegex(@"^[ \t]*(\w+)\s*(?::\s*[\w.]+\s*)?=\s*(?:[\w.]+\.)?APIRouter\s*\(", RegexOptions.Multiline)]
    private static partial Regex ApiRouterDecl();

    [GeneratedRegex(@"(?<![\w.])(\w+)\s*\.\s*include_router\s*\(")]
    private static partial Regex IncludeRouter();

    [GeneratedRegex(@"(?<![\w.])(?:\w+\.)?(?:Depends|Security)\s*\(")]
    private static partial Regex DependsCall();

    [GeneratedRegex(@"^[ \t]*(?:type[ \t]+)?(\w+)\s*(?::\s*[\w.]+\s*)?=\s*(?:typing\.|typing_extensions\.)?Annotated\s*\[", RegexOptions.Multiline)]
    private static partial Regex AnnotatedAlias();

    [GeneratedRegex(@"(?<![\w.])Middleware\s*\(")]
    private static partial Regex MiddlewareCtor();

    [GeneratedRegex(@"\byield\b")]
    private static partial Regex YieldWord();

    [GeneratedRegex(@"^[ \t]*from[ \t]+([.\w]+)[ \t]+import[ \t]+", RegexOptions.Multiline)]
    private static partial Regex FromImport();

    /// <summary>常见的框架 / 第三方中间件在做什么（external-group 行的说明）。</summary>
    private static string? KnownAsgiMiddleware(string name) => name switch
    {
        "CORSMiddleware" => L("跨域：预检请求直接返回，其余请求加 CORS 响应头", "CORS: answers preflight requests directly and adds CORS headers to the rest"),
        "GZipMiddleware" => L("响应压缩", "response compression"),
        "TrustedHostMiddleware" => L("校验 Host 头", "validates the Host header"),
        "HTTPSRedirectMiddleware" => L("HTTP 重定向到 HTTPS", "redirects HTTP to HTTPS"),
        "SessionMiddleware" => L("签名 Cookie 会话", "signed cookie sessions"),
        "ProxyHeadersMiddleware" => L("按 X-Forwarded-* 改写客户端地址", "rewrites the client address from X-Forwarded-*"),
        "SentryAsgiMiddleware" => L("上报异常到 Sentry", "reports exceptions to Sentry"),
        _ => null,
    };

    /// <summary>常见的框架安全依赖在做什么。</summary>
    private static string? KnownDependencies(string name) => name switch
    {
        "OAuth2PasswordBearer" => L("从 Authorization 头取 Bearer token，没有就 401", "reads the Bearer token from the Authorization header, 401 if missing"),
        "OAuth2PasswordRequestForm" => L("从表单解析 username / password", "parses username / password from the form"),
        "HTTPBearer" => L("从 Authorization 头取 Bearer 凭据", "reads Bearer credentials from the Authorization header"),
        "HTTPBasic" => L("解析 Basic 认证头", "parses the Basic auth header"),
        "APIKeyHeader" => L("从请求头取 API Key", "reads the API key from a request header"),
        "APIKeyQuery" => L("从查询参数取 API Key", "reads the API key from a query parameter"),
        "APIKeyCookie" => L("从 Cookie 取 API Key", "reads the API key from a cookie"),
        _ => null,
    };

    private static List<PyApp> FindFastApiApps(Py x) => FindApps(x, "FastAPI", FastApiDecl());

    private static List<PyApp> FindApps(Py x, string quick, Regex decl)
    {
        var list = new List<PyApp>();
        foreach (var (path, text) in x.Texts(t => t.Contains(quick, StringComparison.Ordinal)))
            foreach (Match m in decl.Matches(text))
            {
                var open = m.Index + m.Length - 1;
                var close = MatchPy(text, open);
                list.Add(new PyApp(path, m.Groups[1].Value, m.Index, open, close < 0 ? open : close));
            }
        return list;
    }

    /// <summary>一个依赖（函数 / 类 / 框架的安全依赖对象）：被哪些路由用到（含经别的依赖间接用到）、经过的别名、作用域注册、它自己的依赖。</summary>
    private sealed class Dep
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required string QualifiedName { get; init; }
        /// <summary>要展开调用的函数（类依赖是 __init__，实例依赖是 __call__）；外部依赖为 null。</summary>
        public MapSymbol? Fn { get; init; }
        /// <summary>页面跳转的符号（类依赖没有 __init__ 时是类本身）。</summary>
        public MapSymbol? Symbol { get; init; }
        public string? ExternalNote { get; init; }
        public int Seen { get; init; }
        public HashSet<long> Routes { get; } = [];
        public List<string> Aliases { get; } = [];
        public List<string> Scopes { get; } = [];
        public List<Dep> Subs { get; } = [];
        public bool Direct { get; set; }
    }

    private sealed record PyParam(string Name, string? Type, string? Default);

    private sealed record MwReg(bool IsFunction, string Name, string Expr, string Path, int Pos, string How, MapSymbol? Fn);

    private static FrameworkFlowDto BuildFastApi(Py x, List<PyApp> apps, List<string> notes)
    {
        var app = apps[0];
        var text = x.Text(app.Path)!;
        var ctorArgs = app.CloseParen > app.OpenParen ? text[(app.OpenParen + 1)..app.CloseParen] : "";
        var otherApps = string.Join(ListSep, apps.Skip(1).Select(a => Py.Where(a.Path, x.Text(a.Path)!, a.Pos)));
        notes.Add(L($"认出 FastAPI：{Py.Where(app.Path, text, app.Pos)} {app.Var} = FastAPI(...)", $"Recognized FastAPI: {Py.Where(app.Path, text, app.Pos)} {app.Var} = FastAPI(...)")
                  + (apps.Count > 1 ? L($"；另有 {apps.Count - 1} 个 FastAPI 实例（{otherApps}），按第一个画", $"; {Plural(apps.Count - 1, "other FastAPI instance", "other FastAPI instances")} ({otherApps}), drawn from the first one") : ""));

        // ---- 应用对象上的注册（源码顺序）----
        var registrations = new List<MwReg>();
        var exceptionRegs = new List<(string Exc, string Handler, string Path, int Pos, string How, MapSymbol? Fn)>();
        var receiver = new Regex(@"(?<![\w.])(@)?" + Regex.Escape(app.Var) + @"\s*\.\s*(add_middleware|middleware|exception_handler|add_exception_handler)\s*\(");
        foreach (Match m in receiver.Matches(text, app.CloseParen))
        {
            var open = m.Index + m.Length - 1;
            var close = MatchPy(text, open);
            if (close < 0) continue;
            var args = text[(open + 1)..close];
            var decorator = m.Groups[1].Success;
            switch (m.Groups[2].Value)
            {
                case "add_middleware" when FirstPositional(args) is { } cls:
                    registrations.Add(new MwReg(false, LastSegment(cls), cls, app.Path, m.Index, "add_middleware", null));
                    break;
                case "middleware" when decorator && DefAfter(text, close + 1) is { } def:
                    registrations.Add(new MwReg(true, def.Name, def.Name, app.Path, m.Index, $"@{app.Var}.middleware(\"http\")", x.DefinedAt(app.Path, def.Name, def.Line)));
                    break;
                case "exception_handler" when decorator && DefAfter(text, close + 1) is { } def:
                    exceptionRegs.Add(((FirstPositional(args) ?? "?").Trim(), def.Name, app.Path, m.Index, $"@{app.Var}.exception_handler", x.DefinedAt(app.Path, def.Name, def.Line)));
                    break;
                case "add_exception_handler":
                    var parts = SplitPy(args);
                    if (parts.Count >= 2)
                    {
                        var h = parts[1].Part.Trim();
                        exceptionRegs.Add((parts[0].Part.Trim(), h, app.Path, m.Index, "add_exception_handler", x.Callable(h, app.Path)));
                    }
                    break;
            }
        }
        // FastAPI(middleware=[Middleware(X), …]) / FastAPI(exception_handlers={Exc: handler})
        var ctorMiddleware = new List<MwReg>();
        if (Kwarg(ctorArgs, "middleware") is { } mwArg)
            foreach (Match m in MiddlewareCtor().Matches(mwArg.Value))
            {
                var open = m.Index + m.Length - 1;
                var close = MatchPy(mwArg.Value, open);
                if (close < 0 || FirstPositional(mwArg.Value[(open + 1)..close]) is not { } cls) continue;
                ctorMiddleware.Add(new MwReg(false, LastSegment(cls), cls, app.Path, app.OpenParen + 1 + mwArg.Start + m.Index, "FastAPI(middleware=[…])", null));
            }
        if (Kwarg(ctorArgs, "exception_handlers") is { } exArg && exArg.Value.IndexOf('{') is var brace and >= 0 && MatchPy(exArg.Value, brace) is var braceEnd and > 0)
            foreach (var (part, _) in SplitPy(exArg.Value[(brace + 1)..braceEnd]))
            {
                var colon = part.IndexOf(':');
                if (colon <= 0) continue;
                var h = part[(colon + 1)..].Trim();
                exceptionRegs.Add((part[..colon].Trim(), h, app.Path, app.Pos, "FastAPI(exception_handlers={…})", x.Callable(h, app.Path)));
            }

        // ---- 中间件：注册倒序（后加的在最外层），构造参数里的在里面 ----
        var execution = Enumerable.Reverse(registrations).Concat(ctorMiddleware).ToList();
        var middlewareNodes = new List<FlowNodeDto>();
        var middlewarePosts = new List<List<FlowNodeDto>>();
        var middlewareSymbols = new List<MapSymbol>();
        foreach (var reg in execution)
        {
            var where = Py.Where(reg.Path, text, reg.Pos);
            var line = LineAt(text, reg.Pos);
            if (reg.IsFunction)
            {
                if (reg.Fn is not { } fn)
                {
                    middlewareNodes.Add(Node(null, reg.Name, reg.Name, "external-group", StageMiddleware, L($"函数中间件（{reg.How}，{where}），没找到函数定义", $"Function middleware ({reg.How}, {where}); function definition not found"), [], reg.Path, line));
                    middlewarePosts.Add([]);
                    continue;
                }
                middlewareSymbols.Add(fn);
                var next = Params(x, fn).ElementAtOrDefault(1)?.Name ?? "call_next";
                var (node, post) = SplitNode(x, fn, [next], new Regex(@"\b" + Regex.Escape(next) + @"\s*\("), StageMiddleware, L($"函数中间件（{reg.How}，{where}）", $"Function middleware ({reg.How}, {where})"), reg.Path, line);
                middlewareNodes.Add(node);
                middlewarePosts.Add(post);
                continue;
            }
            var cls = x.Callable(reg.Name, reg.Path) is { Kind: SymbolKind.Class } c ? c : null;
            if (cls is null)
            {
                var desc = KnownAsgiMiddleware(reg.Name);
                middlewareNodes.Add(Node(null, reg.Name, reg.Expr, "external-group", StageMiddleware, L($"框架内置 / 第三方（{reg.How}，{where}）", $"Built-in / third-party ({reg.How}, {where})") + (desc is null ? "" : L("：", ": ") + desc), [], reg.Path, line));
                middlewarePosts.Add([]);
                continue;
            }
            middlewareSymbols.Add(cls);
            if (x.Method(cls, "dispatch") is { } dispatch)
            {
                var next = Params(x, dispatch).ElementAtOrDefault(1)?.Name ?? "call_next";
                var (node, post) = SplitNode(x, dispatch, [next], new Regex(@"\b" + Regex.Escape(next) + @"\s*\("), StageMiddleware, L($"自研中间件（BaseHTTPMiddleware.dispatch，{reg.How}，{where}）", $"Custom middleware (BaseHTTPMiddleware.dispatch, {reg.How}, {where})"), reg.Path, line);
                middlewareNodes.Add(node);
                middlewarePosts.Add(post);
            }
            else if (x.Method(cls, "__call__") is { } call)
            {
                var (node, post) = SplitNode(x, call, ["app"], new Regex(@"\bself\s*\.\s*app\s*\("), StageMiddleware, L($"自研 ASGI 中间件（__call__，{reg.How}，{where}）", $"Custom ASGI middleware (__call__, {reg.How}, {where})"), reg.Path, line);
                middlewareNodes.Add(node);
                middlewarePosts.Add(post);
            }
            else
            {
                middlewareNodes.Add(Node(cls.Id, cls.Name, cls.QualifiedName, "call", StageMiddleware, L($"自研中间件（{reg.How}，{where}），没找到 dispatch / __call__", $"Custom middleware ({reg.How}, {where}); no dispatch / __call__ found"), [], reg.Path, line));
                middlewarePosts.Add([]);
            }
        }

        // ---- 依赖 ----
        var (depNodes, yieldTails, depSymbols, depNotes) = BuildDependencies(x, app, ctorArgs, text);

        // ---- 出错时 ----
        var exceptionNodes = new List<FlowNodeDto>();
        var tailSymbols = new List<MapSymbol>();
        foreach (var (exc, handler, path, pos, how, fn) in exceptionRegs)
        {
            var note = L($"出错时：{exc}（{how}，{Py.Where(path, text, pos)}）", $"On error: {exc} ({how}, {Py.Where(path, text, pos)})");
            if (fn is not null)
            {
                tailSymbols.Add(fn);
                exceptionNodes.Add(x.FunctionNode(fn, StageTail, note));
            }
            else exceptionNodes.Add(Node(null, handler, handler, "external-group", StageTail, note, [], path, LineAt(text, pos)));
        }

        // ---- 组装 ----
        var routeKids = new List<FlowNodeDto>(depNodes);
        routeKids.AddRange(yieldTails.Nodes);
        tailSymbols.AddRange(yieldTails.Symbols);
        var routeNode = x.Http.Count > 0 ? Node(null, L("路由", "Routes"), "", "controllers", StageController, null, routeKids, null, null, x.Http.Count) : null;
        var rootKids = new List<FlowNodeDto>(middlewareNodes);
        if (routeNode is not null) rootKids.Add(routeNode);
        else rootKids.AddRange(routeKids);
        rootKids.AddRange(exceptionNodes);
        // 中间件后置：响应往回走，里层的先执行
        for (var i = middlewarePosts.Count - 1; i >= 0; i--) rootKids.AddRange(middlewarePosts[i]);
        foreach (var n in middlewarePosts.SelectMany(p => p)) if (n.SymbolId is { } id && x.ById.TryGetValue(id, out var s)) tailSymbols.Add(s);

        var rootActor = middlewareNodes.Count > 0 ? StageMiddleware : StageController;
        var root = Node(null, L("HTTP 请求", "HTTP request"), "", "entry", rootActor, null, rootKids, null, null);
        var stages = new List<FrameworkStageDto>();
        if (middlewareNodes.Count > 0) stages.Add(new FrameworkStageDto(StageMiddleware, L("中间件", "Middleware"), true, x.Dtos(middlewareSymbols)));
        if (routeNode is not null) stages.Add(new FrameworkStageDto(StageController, L("路由", "Routes"), true, x.EntrySymbols(x.Http)));
        if (depNodes.Count > 0) stages.Add(new FrameworkStageDto(StageDependency, L("依赖", "Dependencies"), false, x.Dtos(depSymbols)));
        if (yieldTails.Nodes.Count > 0 || exceptionNodes.Count > 0 || middlewarePosts.Any(p => p.Count > 0))
            stages.Add(new FrameworkStageDto(StageTail, L("收尾", "Teardown"), true, x.Dtos(tailSymbols)));

        // ---- Notes ----
        if (middlewareNodes.Count > 0)
        {
            var hows = string.Join(" / ", registrations.Select(r => r.How).Distinct());
            notes.Add(L($"中间件按注册倒序排：{hows} 后加的在最外层、请求先经过它（Starlette 每次把新中间件插到栈顶）", $"Middleware is in reverse registration order: the last one added via {hows} is outermost and sees the request first (Starlette pushes each new middleware onto the top of the stack)")
                      + (ctorMiddleware.Count > 0 ? L("；FastAPI(middleware=[…]) 里的在它们里面，按列表顺序", "; those in FastAPI(middleware=[…]) sit inside them, in list order") : "")
                      + L("；自研中间件 call_next 之后的部分放在收尾（洋葱倒序）；框架自己还有最外层的 ServerErrorMiddleware（未处理异常 → 500）与最内层的 ExceptionMiddleware（exception_handler 在这里执行）",
                          "; the part of custom middleware after call_next is in teardown (reverse onion order); the framework also has ServerErrorMiddleware outermost (unhandled exception → 500) and ExceptionMiddleware innermost (where exception_handler runs)"));
        }
        else notes.Add(L($"{Py.FileName(app.Path)} 里没有 {app.Var}.add_middleware / @{app.Var}.middleware：只有框架内置的 ServerErrorMiddleware 与 ExceptionMiddleware",
            $"No {app.Var}.add_middleware / @{app.Var}.middleware in {Py.FileName(app.Path)}: only the built-in ServerErrorMiddleware and ExceptionMiddleware"));
        if (routeNode is null) notes.Add(L("没有发现 HTTP 入口（@app.get / @router.post …）", "No HTTP entry points found (@app.get / @router.post …)"));
        notes.AddRange(depNotes);
        notes.Add(exceptionNodes.Count > 0
            ? L($"{exceptionNodes.Count} 个 exception_handler 放在收尾「出错时」", $"{Plural(exceptionNodes.Count, "exception_handler", "exception_handlers")} placed in teardown under 'On error'")
            : L("没有 exception_handler：HTTPException 与参数校验错误走 FastAPI 默认处理（返回 JSON 错误）", "No exception_handler: HTTPException and validation errors use FastAPI's default handling (JSON error response)"));
        return Assemble(x.Container, L("HTTP 请求", "HTTP request"), root, stages, [], notes, rootActor == StageMiddleware ? L("中间件", "Middleware") : L("路由", "Routes"));
    }

    /// <summary>在 next 调用处拆开的函数：前置是子节点，后置是收尾列的行（Note 写"X 后置"）。</summary>
    private static (FlowNodeDto Node, List<FlowNodeDto> Post) SplitNode(Py x, MapSymbol fn, string[] nextNames, Regex textPivot, string actor, string note, string? path, int? line, string? label = null)
    {
        var split = x.SplitAt(fn, nextNames, textPivot);
        label ??= x.Label(fn);
        var fullNote = note + (split.Conditional ? L($"；有条件（有分支不调 {nextNames[0]} 直接返回）", $"; conditional (some branches return without calling {nextNames[0]})") : "")
                       + (split.Found ? "" : L($"；没找到 {nextNames[0]} 调用", $"; no {nextNames[0]} call found"));
        var node = Node(fn.Id, label, fn.QualifiedName, "call", actor, fullNote, x.Expand(split.Pre, fn, actor, null), path ?? fn.Path, line ?? fn.StartLine);
        var post = split.Found ? x.Expand(split.Post, fn, StageTail, L($"{label} 后置（响应往回走）", $"{label} after (on the way back with the response)")) : [];
        return (node, post);
    }

    /// <summary>函数的参数（不含 self / cls / *args / **kwargs）：签名来自索引，没有就读 def 行。</summary>
    private static List<PyParam> Params(Py x, MapSymbol fn)
    {
        var sig = x.Src.Signature(fn.Id);
        if (sig is null && x.Text(fn.Path) is { } text)
        {
            var (s, _) = LineSpan(text, fn.StartLine, fn.StartLine);
            var open = text.IndexOf('(', s);
            var close = open < 0 ? -1 : MatchPy(text, open);
            if (close > open) sig = text[s..(close + 1)];
        }
        return ParseParams(sig);
    }

    private static List<PyParam> ParseParams(string? signature)
    {
        var list = new List<PyParam>();
        if (signature is null) return list;
        var open = signature.IndexOf('(');
        if (open < 0) return list;
        var close = MatchPy(signature, open);
        if (close < 0) close = signature.LastIndexOf(')');
        if (close <= open) return list;
        foreach (var (part, _) in SplitPy(signature[(open + 1)..close]))
        {
            var p = part.Trim();
            if (p.Length == 0 || p == "/" || p.StartsWith('*')) continue;
            string? def = null;
            var eq = TopLevelAssign(p);
            if (eq > 0) { def = p[(eq + 1)..].Trim(); p = p[..eq].Trim(); }
            string? type = null;
            var colon = p.IndexOf(':');
            if (colon > 0) { type = p[(colon + 1)..].Trim(); p = p[..colon].Trim(); }
            if (p is "self" or "cls") continue;
            list.Add(new PyParam(p, type, def));
        }
        return list;
    }

    /// <summary>s 里第一个 Depends(...) / Security(...) 的第一个位置实参（Depends() 为空串）；没有为 null。</summary>
    private static string? DependsArg(string s)
    {
        var m = DependsCall().Match(s);
        if (!m.Success) return null;
        var open = m.Index + m.Length - 1;
        var close = MatchPy(s, open);
        return close < 0 ? "" : FirstPositional(s[(open + 1)..close]) ?? "";
    }

    /// <summary>dependencies=[Depends(a), Depends(b)] 里的各个依赖（实参，位置）。</summary>
    private static List<(string Expr, int Pos)> DependsList(string s)
    {
        var list = new List<(string, int)>();
        foreach (Match m in DependsCall().Matches(s))
        {
            var open = m.Index + m.Length - 1;
            var close = MatchPy(s, open);
            if (close < 0) continue;
            list.Add((FirstPositional(s[(open + 1)..close]) ?? "", m.Index));
        }
        return list;
    }

    /// <summary>X[...] / X | None / Optional[X] 里的第一个名字（给 Depends() 用的类型）。</summary>
    private static string? TypeName(string? type)
    {
        if (type is null) return null;
        var t = Regex.Replace(type, @"^\s*(?:typing\.)?(?:Optional|Annotated)\s*\[", "");
        var m = Regex.Match(t, @"[\w.]+");
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// 依赖阶段：别名表（X = Annotated[T, Depends(f)]）、router 图（APIRouter 声明 + include_router），作用域依赖与每个路由的直接依赖，
    /// 依赖闭包（依赖自己的参数依赖，4 层内）记到每个依赖的 Routes；画出直接依赖（作用于全部路由的在前，再按路由数、首次出现），yield 之后的部分进收尾（倒序）。
    /// </summary>
    private static (List<FlowNodeDto> Nodes, (List<FlowNodeDto> Nodes, List<MapSymbol> Symbols) Yields, List<MapSymbol> Symbols, List<string> Notes)
        BuildDependencies(Py x, PyApp app, string ctorArgs, string appText)
    {
        // 别名：X = Annotated[T, Depends(f)]
        var aliases = new Dictionary<string, (string Expr, string? TypeHint, string Path)>(StringComparer.Ordinal);
        foreach (var (path, text) in x.Texts(t => t.Contains("Annotated", StringComparison.Ordinal) && (t.Contains("Depends", StringComparison.Ordinal) || t.Contains("Security", StringComparison.Ordinal))))
            foreach (Match m in AnnotatedAlias().Matches(text))
            {
                var open = m.Index + m.Length - 1;
                var close = MatchPy(text, open);
                if (close < 0) continue;
                var inner = text[(open + 1)..close];
                if (DependsArg(inner) is not { } arg) continue;
                aliases.TryAdd(m.Groups[1].Value, (arg, TypeName(SplitPy(inner).FirstOrDefault().Part), path));
            }

        // 框架安全依赖一类的模块级对象：name = Ctor(...)
        var assignments = new Dictionary<string, (string Ctor, string Path)?>(StringComparer.Ordinal);
        (string Ctor, string Path)? Assignment(string name, string fromFile)
        {
            if (assignments.TryGetValue(name, out var hit)) return hit;
            var rx = new Regex(@"^[ \t]*" + Regex.Escape(name) + @"\s*(?::[^=\n]+)?=\s*([\w.]+)\s*\(", RegexOptions.Multiline);
            foreach (var f in x.Files.OrderBy(f => f == fromFile ? 0 : 1))
                if (x.Text(f) is { } t && t.Contains(name, StringComparison.Ordinal) && rx.Match(t) is { Success: true } m)
                    return assignments[name] = (m.Groups[1].Value, f);
            return assignments[name] = null;
        }

        var deps = new Dictionary<string, Dep>(StringComparer.Ordinal);
        Dep? Resolve(string expr, string fromFile, string? typeHint, int depth)
        {
            var m = Regex.Match(expr.Trim(), @"^[\w.]+");
            var name = m.Success ? m.Value : typeHint;
            if (string.IsNullOrEmpty(name)) return null;
            var last = LastSegment(name);
            Dep d;
            if (x.Callable(last, fromFile) is { } sym)
            {
                var key = "s:" + sym.Id;
                if (deps.TryGetValue(key, out var old)) return old;
                var fn = sym.Kind == SymbolKind.Class ? x.Method(sym, "__init__") : sym;
                d = new Dep { Key = key, Label = sym.Kind == SymbolKind.Class ? sym.Name : x.Label(sym), QualifiedName = sym.QualifiedName, Fn = fn, Symbol = fn ?? sym, Seen = deps.Count };
            }
            else if (Assignment(last, fromFile) is { } a)
            {
                var ctor = LastSegment(a.Ctor);
                var cls = x.Callable(ctor, a.Path) is { Kind: SymbolKind.Class } c ? c : null;
                var key = "v:" + a.Path + ":" + last;
                if (deps.TryGetValue(key, out var old)) return old;
                d = cls is not null
                    ? new Dep { Key = key, Label = L($"{last}（{cls.Name} 实例）", $"{last} ({cls.Name} instance)"), QualifiedName = cls.QualifiedName, Fn = x.Method(cls, "__call__"), Symbol = x.Method(cls, "__call__") ?? cls, Seen = deps.Count }
                    : new Dep { Key = key, Label = $"{last} = {ctor}(…)", QualifiedName = a.Ctor, ExternalNote = KnownDependencies(ctor) ?? L("框架 / 第三方的依赖对象", "framework / third-party dependency object"), Seen = deps.Count };
            }
            else
            {
                var key = "e:" + last;
                if (deps.TryGetValue(key, out var old)) return old;
                d = new Dep { Key = key, Label = last, QualifiedName = name, ExternalNote = KnownDependencies(last) ?? L("框架 / 第三方的依赖", "framework / third-party dependency"), Seen = deps.Count };
            }
            deps[d.Key] = d;
            if (depth < 4 && d.Fn is { } f)
                foreach (var p in Params(x, f))
                    if (ParamDependency(p) is { } pd && Resolve(pd.Expr, f.Path, pd.TypeHint, depth + 1) is { } sub && sub != d && !d.Subs.Contains(sub))
                    {
                        d.Subs.Add(sub);
                        if (pd.Alias is { } al && !sub.Aliases.Contains(al)) sub.Aliases.Add(al);
                    }
            return d;
        }

        (string Expr, string? TypeHint, string? Alias)? ParamDependency(PyParam p)
        {
            if (p.Default is { } def && Regex.IsMatch(def, @"^(?:\w+\.)?(?:Depends|Security)\s*\(") && DependsArg(def) is { } arg) return (arg, TypeName(p.Type), null);
            if (p.Type is { } type)
            {
                if (type.Contains("Annotated", StringComparison.Ordinal) && DependsArg(type) is { } inline)
                {
                    var open = type.IndexOf('[');
                    var hint = open >= 0 ? TypeName(SplitPy(type[(open + 1)..]).FirstOrDefault().Part) : null;
                    return (inline, hint, null);
                }
                foreach (Match t in Regex.Matches(type, @"\w+"))
                    if (aliases.TryGetValue(t.Value, out var al)) return (al.Expr, al.TypeHint, t.Value);
            }
            return null;
        }

        // ---- router 图 ----
        var routers = new List<(string Path, string Var, int Pos, string Args)>();
        var includes = new List<(string Path, string Receiver, string Target, int Pos, string Args)>();
        foreach (var (path, text) in x.Texts(t => t.Contains("APIRouter", StringComparison.Ordinal) || t.Contains("include_router", StringComparison.Ordinal)))
        {
            foreach (Match m in ApiRouterDecl().Matches(text))
            {
                var open = m.Index + m.Length - 1;
                var close = MatchPy(text, open);
                routers.Add((path, m.Groups[1].Value, m.Index, close > open ? text[(open + 1)..close] : ""));
            }
            foreach (Match m in IncludeRouter().Matches(text))
            {
                var open = m.Index + m.Length - 1;
                var close = MatchPy(text, open);
                if (close < 0) continue;
                var args = text[(open + 1)..close];
                if (FirstPositional(args) is { } target) includes.Add((path, m.Groups[1].Value, target, m.Index, args));
            }
        }
        (string Path, string Var)? ResolveRouter(string path, string expr)
        {
            expr = expr.Trim();
            if (routers.Any(r => r.Path == path && r.Var == expr)) return (path, expr);
            static bool ModuleMatches(string file, string module) => module.Length > 0 && (ModuleName(file) == module || ModuleName(file).EndsWith("." + module, StringComparison.Ordinal));
            var dot = expr.LastIndexOf('.');
            if (dot > 0)
            {
                var v = expr[(dot + 1)..];
                var mod = LastSegment(expr[..dot]);
                var hit = routers.FirstOrDefault(r => r.Var == v && ModuleMatches(r.Path, mod));
                if (hit.Path is not null) return (hit.Path, hit.Var);
                // from app.api.routes import items as items_routes → items_routes.router
                if (ImportedModule(path, expr[..dot]) is { } real && routers.FirstOrDefault(r => r.Var == v && ModuleMatches(r.Path, LastSegment(real))) is { Path: not null } h2) return (h2.Path, h2.Var);
                return null;
            }
            // from app.api.routes.items import router as items_router
            if (ImportedName(path, expr) is { } imp)
            {
                var hit = routers.FirstOrDefault(r => r.Var == imp.Name && ModuleMatches(r.Path, imp.Module.TrimStart('.')));
                if (hit.Path is not null) return (hit.Path, hit.Var);
            }
            var same = routers.Where(r => r.Var == expr).ToList();
            return same.Count == 1 ? (same[0].Path, same[0].Var) : null;
        }
        (string Module, string Name)? ImportedName(string path, string local)
        {
            if (x.Text(path) is not { } t) return null;
            foreach (Match m in FromImport().Matches(t))
            {
                var start = m.Index + m.Length;
                string names;
                if (start < t.Length && t[start] == '(')
                {
                    var close = MatchPy(t, start);
                    names = close > start ? t[(start + 1)..close] : "";
                }
                else
                {
                    var nl = t.IndexOf('\n', start);
                    names = t[start..(nl < 0 ? t.Length : nl)];
                }
                foreach (var (part, _) in SplitPy(names.Trim()))
                {
                    var bits = Regex.Split(part.Trim(), @"\s+as\s+");
                    var orig = bits[0].Trim();
                    var alias = bits.Length > 1 ? bits[1].Trim() : orig;
                    if (alias == local) return (m.Groups[1].Value, orig);
                }
            }
            return null;
        }
        string? ImportedModule(string path, string local) => ImportedName(path, local) is { } imp ? imp.Module.TrimStart('.') + "." + imp.Name : null;

        var entryIds = x.Http.Select(e => e.SymbolId).ToHashSet();
        var scopeMemo = new Dictionary<(string, string), HashSet<long>>();
        HashSet<long> Scope((string Path, string Var) id, int depth)
        {
            if (scopeMemo.TryGetValue(id, out var hit)) return hit;
            if (id == (app.Path, app.Var)) return scopeMemo[id] = entryIds;
            var set = scopeMemo[id] = x.Http.Where(e => e.File == id.Path).Select(e => e.SymbolId).ToHashSet();
            if (depth < 8)
                foreach (var inc in includes.Where(i => i.Path == id.Path && i.Receiver == id.Var))
                    if (ResolveRouter(inc.Path, inc.Target) is { } target && target != id) set.UnionWith(Scope(target, depth + 1));
            return set;
        }

        // ---- 作用域依赖：FastAPI(dependencies=) → include_router(dependencies=) → APIRouter(dependencies=) ----
        var scoped = new List<(Dep Dep, HashSet<long> Scope)>();
        void AddScoped(string args, string path, string text, int pos, string how, HashSet<long> scope)
        {
            if (Kwarg(args, "dependencies") is not { } v) return;
            foreach (var (expr, _) in DependsList(v.Value))
                if (Resolve(expr, path, null, 0) is { } d)
                {
                    var note = L($"{how}（{Py.Where(path, text, pos)}）", $"{how} ({Py.Where(path, text, pos)})");
                    if (!d.Scopes.Contains(note)) d.Scopes.Add(note);
                    d.Direct = true;
                    scoped.Add((d, scope));
                }
        }
        AddScoped(ctorArgs, app.Path, appText, app.Pos, L("应用级依赖 FastAPI(dependencies=…)", "App-level dependency FastAPI(dependencies=…)"), entryIds);
        foreach (var inc in includes)
        {
            var target = ResolveRouter(inc.Path, inc.Target);
            AddScoped(inc.Args, inc.Path, x.Text(inc.Path)!, inc.Pos, L("include_router 依赖", "include_router dependency"), target is { } t ? Scope(t, 0) : entryIds);
        }
        foreach (var r in routers) AddScoped(r.Args, r.Path, x.Text(r.Path)!, r.Pos, L("APIRouter 依赖", "APIRouter dependency"), Scope((r.Path, r.Var), 0));
        var unresolvedScopes = includes.Count(i => Kwarg(i.Args, "dependencies") is not null && ResolveRouter(i.Path, i.Target) is null);

        // ---- 每个路由的直接依赖：作用域 → 装饰器 dependencies=[…] → 参数 ----
        foreach (var e in x.Http)
        {
            if (!x.ById.TryGetValue(e.SymbolId, out var sym)) continue;
            var direct = scoped.Where(s => s.Scope.Contains(e.SymbolId)).Select(s => s.Dep).ToList();
            if (x.Text(e.File) is { } text && DecoratorBlock(text, e.Line) is { } block)
                foreach (var list in DecoratorDependencies(text[block.Start..block.End]))
                    foreach (var (expr, _) in DependsList(list))
                        if (Resolve(expr, e.File, null, 0) is { } d) direct.Add(d);
            if (sym.Kind is SymbolKind.Function or SymbolKind.Method)
                foreach (var p in Params(x, sym))
                    if (ParamDependency(p) is { } pd && Resolve(pd.Expr, e.File, pd.TypeHint, 0) is { } d)
                    {
                        direct.Add(d);
                        if (pd.Alias is { } al && !d.Aliases.Contains(al)) d.Aliases.Add(al);
                    }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Mark(Dep d) { if (!seen.Add(d.Key)) return; d.Routes.Add(e.SymbolId); foreach (var s in d.Subs) Mark(s); }
            foreach (var d in direct) { d.Direct = true; Mark(d); }
        }

        // ---- 画 ----
        var total = x.Http.Count;
        var top = deps.Values.Where(d => d.Direct)
            .OrderByDescending(d => d.Scopes.Count > 0 && d.Routes.Count == total)
            .ThenByDescending(d => d.Routes.Count).ThenBy(d => d.Seen).ToList();
        var rendered = new HashSet<string>(StringComparer.Ordinal);
        var symbols = new List<MapSymbol>();
        var yieldNodes = new List<FlowNodeDto>();
        var yieldSymbols = new List<MapSymbol>();
        FlowNodeDto Render(Dep d)
        {
            var path = d.Symbol?.Path;
            int? line = d.Symbol?.StartLine;
            if (!rendered.Add(d.Key))
                return Node(d.Symbol?.Id, d.Label, d.QualifiedName, d.Symbol is null ? "external-group" : "call", StageDependency,
                    L("同一请求里只执行一次（FastAPI 缓存依赖结果），见上面", "Runs only once per request (FastAPI caches dependency results); see above"), [], path, line);
            if (d.Symbol is not null) symbols.Add(d.Symbol);
            var kids = d.Subs.Select(Render).ToList();
            var parts = new List<string> { L($"{d.Routes.Count} 个路由用到", $"used by {Plural(d.Routes.Count, "route", "routes")}") };
            if (d.Aliases.Count > 0) parts.Add(L("经 ", "via ") + string.Join(ListSep, d.Aliases));
            parts.AddRange(d.Scopes);
            if (d.ExternalNote is not null) parts.Add(d.ExternalNote);
            if (d.Fn is { } fn)
            {
                var text = x.Text(fn.Path);
                var (s, e) = text is null ? (0, 0) : LineSpan(text, fn.StartLine, fn.EndLine);
                if (text is not null && YieldWord().IsMatch(text[s..e]))
                {
                    var split = x.SplitAt(fn, [], YieldWord());
                    kids.AddRange(x.Expand(split.Pre, fn, StageDependency, null));
                    parts.Add(L("yield 依赖：yield 之后的部分在收尾", "yield dependency: the part after yield is in teardown"));
                    var (pivot, _) = LineSpan(text, split.PivotLine, split.PivotLine);
                    var inWith = Regex.IsMatch(text[s..Math.Max(s, pivot)], @"^[ \t]*(?:async[ \t]+)?with\b", RegexOptions.Multiline);
                    var inFinally = Regex.IsMatch(text[Math.Min(e, pivot)..e], @"^[ \t]*finally\s*:", RegexOptions.Multiline);
                    if (split.Post.Count > 0 || inWith || inFinally)
                    {
                        yieldSymbols.Add(fn);
                        yieldNodes.Insert(0, Node(fn.Id, L($"{d.Label}（yield 之后）", $"{d.Label} (after yield)"), fn.QualifiedName, "call", StageTail,
                            L($"依赖 {d.Label} 的收尾：路由函数返回后接着执行 yield 之后的部分", $"Teardown of dependency {d.Label}: the part after yield runs once the route function returns")
                            + (inWith ? L("；yield 在 with 块里，退出 with 时释放资源", "; yield is inside a with block, resources are released when it exits") : "")
                            + (inFinally ? L("；finally 里做清理", "; cleanup in finally") : ""),
                            x.Expand(split.Post, fn, StageTail, null), fn.Path, split.PivotLine));
                    }
                }
                else kids.AddRange(x.Expand(x.KeptCalls(fn.Id), fn, StageDependency, null));
            }
            return Node(d.Symbol?.Id, d.Label, d.QualifiedName, d.Symbol is null ? "external-group" : "call", StageDependency, string.Join(L("；", "; "), parts), Cap(kids, StageDependency), path, line);
        }
        var nodes = new List<FlowNodeDto>();
        foreach (var d in top)
            if (!rendered.Contains(d.Key)) nodes.Add(Render(d));

        var notes = new List<string>();
        if (nodes.Count > 0)
        {
            notes.Add(L($"依赖按被多少路由用到排序（共 {total} 个 HTTP 入口，作用于全部路由的排最前），不是执行顺序：每个路由先解 include_router / APIRouter / 装饰器上的依赖，再按参数顺序解；"
                        + "依赖自己的依赖画在它下面（先解），同一请求里同一依赖只执行一次",
                        $"Dependencies are sorted by how many routes use them ({Plural(total, "HTTP entry point", "HTTP entry points")} in total; those applying to all routes come first), not by execution order: each route first resolves dependencies from include_router / APIRouter / decorators, then its parameters in order; "
                        + "a dependency's own dependencies are drawn beneath it (resolved first), and the same dependency runs only once per request"));
            if (aliases.Count > 0)
            {
                var aliasList = string.Join(ListSep, aliases.Keys.Take(6)) + (aliases.Count > 6 ? " …" : "");
                notes.Add(L($"认出 {aliases.Count} 个依赖别名（{aliasList}，X = Annotated[T, Depends(f)]）", $"Recognized {Plural(aliases.Count, "dependency alias", "dependency aliases")} ({aliasList}, X = Annotated[T, Depends(f)])"));
            }
            if (yieldNodes.Count > 0) notes.Add(L($"{yieldNodes.Count} 个 yield 依赖：yield 之后的部分放在收尾（后解的先收尾）", $"{Plural(yieldNodes.Count, "yield dependency", "yield dependencies")}: the part after yield is in teardown (last resolved tears down first)"));
        }
        else notes.Add(L("路由参数里没有 Depends(...)，也没有 router 级依赖：没有依赖阶段", "No Depends(...) in route parameters and no router-level dependencies: no dependency stage"));
        if (unresolvedScopes > 0) notes.Add(L($"{unresolvedScopes} 处 include_router(dependencies=…) 没推断出挂的是哪个 router，按全部路由计", $"Could not infer which router {Plural(unresolvedScopes, "include_router(dependencies=…) call", "include_router(dependencies=…) calls")} attach to; counted for all routes"));
        return (nodes, (yieldNodes, yieldSymbols), symbols, notes);
    }

    /// <summary>装饰器块里每处 dependencies=[…] 的方括号内容。</summary>
    private static IEnumerable<string> DecoratorDependencies(string block)
    {
        foreach (Match m in Regex.Matches(block, @"\bdependencies\s*=\s*\["))
        {
            var open = m.Index + m.Length - 1;
            var close = MatchPy(block, open);
            if (close > open) yield return block[(open + 1)..close];
        }
    }
}
