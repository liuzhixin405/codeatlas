using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using static CodeAtlas.Server.FrameworkFlowBuilder;

namespace CodeAtlas.Server;

/// <summary>Node 框架流程 · Express 与 Koa（两者都是 app.use 一串中间件 + 路由器挂载）；NestJS 的 app.use 也用这里的实参分类与中间件分析。</summary>
public static partial class NodeFrameworkFlow
{
    // ---- use(...) 的一个实参 ----
    internal abstract record UseArg(int Start, int End);
    /// <summary>内联函数 (req, res, next) =&gt; {…}。</summary>
    internal sealed record InlineArg(JsFunction Fn, int Start, int End) : UseArg(Start, End);
    /// <summary>仓库里的中间件函数 / 方法（名字、a.b、require('./x')）。</summary>
    internal sealed record FnArg(MapSymbol Symbol, int Start, int End) : UseArg(Start, End);
    /// <summary>调用仓库里的工厂 rateLimit({…})，Returned 是它 return 出来的中间件（找不到为 null）。</summary>
    internal sealed record FactoryArg(MapSymbol Factory, JsFunction? Returned, int Start, int End) : UseArg(Start, End);
    /// <summary>框架内置 / 第三方：express.json()、cors()、helmet()。</summary>
    internal sealed record ExternalArg(string Label, string? Package, bool Builtin, int Start, int End) : UseArg(Start, End);
    /// <summary>路由器挂载：Express Router、koa-router 的 router.routes()。</summary>
    internal sealed record MountArg(string File, string Var, int Start, int End, string? Display = null) : UseArg(Start, End);
    internal sealed record UnknownArg(string Text, int Start, int End) : UseArg(Start, End);

    /// <summary>一处注册：Kind = use / route（app.get('/x', …)，只用来判断"路由之后"）/ error（Koa 的 app.on('error', fn)）。</summary>
    internal sealed record AppEvent(string Kind, string File, int Pos, string? Path, IReadOnlyList<UseArg> Args, string? Via, string Receiver, string? RouteLabel, bool Conditional, IReadOnlyList<string>? RouteMiddleware = null);

    /// <summary>中间件分析：符号（内联为 null）、显示名、体按 next 切开的结果、是否错误中间件（四个参数）、是否两个参数的终结处理器、附加说明。</summary>
    internal sealed record Mw(MapSymbol? Symbol, string Label, string Qn, string Kind, string Path, BodySplit Split, bool IsError, bool Terminal, List<string> Notes);

    private static readonly HashSet<string> HttpVerbs = new(StringComparer.Ordinal) { "get", "post", "put", "delete", "patch", "head", "options", "all", "del" };
    private static readonly HashSet<string> NotHelpers = new(StringComparer.Ordinal)
        { "if", "for", "while", "switch", "return", "function", "require", "catch", "await", "typeof", "new", "super", "console.log", "console.info" };

    /// <summary>收集过程中的累计：中间件列的符号、收尾（后置按进入顺序，最后倒过来）、路由之后、出错时、响应发完时。</summary>
    private sealed class Acc
    {
        public readonly List<MapSymbol> MwSymbols = [];
        public readonly List<MapSymbol> TailSymbols = [];
        public readonly List<List<FlowNodeDto>> Posts = [];
        public readonly List<FlowNodeDto> AfterRoutes = [];
        public readonly List<FlowNodeDto> Errors = [];
        public readonly List<FlowNodeDto> Finishes = [];
        public readonly List<string> Helpers = [];
        public readonly List<string> Files = [];
        public readonly List<string> RouteMiddleware = [];
        public int RouteMiddlewareRoutes;
        public readonly List<string> Mounts = [];
        public int RouterMiddleware;
    }

    private static FrameworkFlowDto BuildExpressLike(Ctx x, AppDecl app)
    {
        var koa = app.Framework == "koa";
        var flavor = koa ? "koa" : "express";
        var fwName = FrameworkName(app.Framework);
        var t = x.Text(app.File)!;
        var acc = new Acc();
        var events = new List<AppEvent>();
        CollectEvents(x, app.File, app.Pos, t.Code.Length, app.Var, null, 0, [], events, [], flavor, acc);
        var scoped = ProcessScope(x, events, "", false, acc, flavor, 0, []);

        var pre = scoped.Where(p => !p.Mount).Select(p => p.Node).ToList();
        var mounts = scoped.Where(p => p.Mount).Select(p => p.Node).ToList();
        var tail = new List<FlowNodeDto>();
        foreach (var group in Enumerable.Reverse(acc.Posts)) tail.AddRange(group);
        tail.AddRange(acc.AfterRoutes);
        tail.AddRange(acc.Errors);
        tail.AddRange(acc.Finishes);
        var httpCount = x.Entries.Count(e => e.Kind == "http");
        var underRoutes = new List<FlowNodeDto>([.. mounts, .. tail]);
        var placeholder = httpCount > 0 ? Node(null, L("路由", "Routes"), "", "controllers", StageController, null, underRoutes, null, null, httpCount) : null;
        var children = new List<FlowNodeDto>(pre);
        if (placeholder is not null) children.Add(placeholder); else children.AddRange(underRoutes);
        var root = Node(null, L("HTTP 请求", "HTTP request"), "", "entry", pre.Count > 0 ? StageMiddleware : StageController, null, children, null, null);

        var present = new HashSet<string>(StringComparer.Ordinal);
        void Collect(FlowNodeDto n) { present.Add(n.Actor); foreach (var c in n.Children) Collect(c); }
        foreach (var c in children) Collect(c);
        var stages = new List<FrameworkStageDto>();
        if (present.Contains(StageMiddleware)) stages.Add(new FrameworkStageDto(StageMiddleware, L("中间件", "Middleware"), true, acc.MwSymbols.DistinctBy(s => s.Id).Select(x.Dto).ToList()));
        if (present.Contains(StageController)) stages.Add(new FrameworkStageDto(StageController, L("路由", "Routes"), true, x.EntrySymbols("http")));
        if (present.Contains(StageTail)) stages.Add(new FrameworkStageDto(StageTail, L("收尾", "Teardown"), true, acc.TailSymbols.DistinctBy(s => s.Id).Select(x.Dto).ToList()));

        // ---- Notes ----
        var notes = new List<string> { L($"认出 {fwName}（{x.Where(app.File, app.Pos)} 的 {app.Var} = {(koa ? "new Koa()" : "express()")}）", $"Recognized {fwName} ({app.Var} = {(koa ? "new Koa()" : "express()")} at {x.Where(app.File, app.Pos)})") };
        if (pre.Count == 0 && acc.RouterMiddleware == 0) notes.Add(L($"{app.Var} 上没有 {app.Var}.use(...) 注册的中间件", $"No middleware registered with {app.Var}.use(...)"));
        else
            notes.Add(L($"中间件按 {string.Join("、", acc.Files.Select(MapSymbol.FileName))} 里 {app.Var}.use(...) 的书写顺序", $"Middleware follows the source order of {app.Var}.use(...) in {string.Join(", ", acc.Files.Select(MapSymbol.FileName))}")
                      + (acc.Helpers.Count > 0 ? L($"；{string.Join("、", acc.Helpers.Distinct().Select(h => h + "(" + app.Var + ")"))} 里注册的按调用位置摊进来", $"; middleware registered inside {string.Join(", ", acc.Helpers.Distinct().Select(h => h + "(" + app.Var + ")"))} is spliced in at the call site") : ""));
        if (acc.Mounts.Count > 0)
            notes.Add(L($"路由挂载 {acc.Mounts.Count} 处（{string.Join("、", acc.Mounts.Take(6))}{(acc.Mounts.Count > 6 ? " 等" : "")}）画在路由占位下面", $"{Plural(acc.Mounts.Count, "route mount", "route mounts")} ({string.Join(", ", acc.Mounts.Take(6))}{(acc.Mounts.Count > 6 ? ", …" : "")}) drawn under the routes placeholder")
                      + (acc.RouterMiddleware > 0 ? L($"；router 级中间件 {acc.RouterMiddleware} 个挂在各自的挂载节点下，只对那个前缀生效", $"; {acc.RouterMiddleware} router-level middleware hang under their mount nodes and apply only to that prefix") : ""));
        else notes.Add(koa ? L("没找到 app.use(router.routes()) 一类的路由挂载", "No route mount like app.use(router.routes()) found") : L($"没找到 {app.Var}.use('/前缀', router) 一类的路由挂载：路由直接注册在 {app.Var} 上", $"No route mount like {app.Var}.use('/prefix', router) found: routes are registered directly on {app.Var}"));
        if (acc.RouteMiddlewareRoutes > 0)
            notes.Add(L($"路由级中间件 {string.Join("、", acc.RouteMiddleware.Take(6))}{(acc.RouteMiddleware.Count > 6 ? " 等" : "")} 写在 {acc.RouteMiddlewareRoutes} 条路由的注册里（router.get('/x', 中间件, 处理函数)），只对那条路由生效，没画进骨架", $"Route-level middleware {string.Join(", ", acc.RouteMiddleware.Take(6))}{(acc.RouteMiddleware.Count > 6 ? ", …" : "")} is written in the registration of {Plural(acc.RouteMiddlewareRoutes, "route", "routes")} (router.get('/x', middleware, handler)); it applies only to that route and is not drawn in the skeleton"));
        if (acc.Posts.Any(p => p.Count > 0) || acc.Finishes.Count > 0)
            notes.Add(L("收尾：中间件里 next() 之后的调用按中间件倒序（洋葱往回走）", "Teardown: calls after next() in middleware, in reverse middleware order (the onion unwinding)") + (acc.Finishes.Count > 0 ? L("；res.on('finish') 回调里的算响应发完时，排在最后", "; calls in res.on('finish') callbacks run once the response has been sent, so they come last") : ""));
        if (acc.AfterRoutes.Count > 0)
            notes.Add(L($"{string.Join("、", acc.AfterRoutes.Select(n => n.Label))} 注册在路由之后：没有路由匹配（或路由调了 next()）时才走到", $"{string.Join(", ", acc.AfterRoutes.Select(n => n.Label))} registered after the routes: reached only when no route matches (or a route calls next())"));
        var catches = acc.Posts.SelectMany(p => p).Count(n => n.Note is { } nn && nn.Contains("catch", StringComparison.Ordinal));
        if (koa)
            notes.Add(acc.Errors.Count > 0 || catches > 0
                ? L($"出错时：{(catches > 0 ? "中间件 try { await next() } catch 里的调用" : "")}{(catches > 0 && acc.Errors.Count > 0 ? "、" : "")}{string.Join("、", acc.Errors.Select(n => n.Label))}", $"On error: {(catches > 0 ? "calls in the middleware's try { await next() } catch" : "")}{(catches > 0 && acc.Errors.Count > 0 ? ", " : "")}{string.Join(", ", acc.Errors.Select(n => n.Label))}")
                : L("没找到错误处理（try { await next() } catch 的中间件或 app.on('error')）：出错时走 Koa 默认的 ctx.onerror", "No error handling found (middleware with try { await next() } catch, or app.on('error')): errors go to Koa's default ctx.onerror"));
        else
            notes.Add(acc.Errors.Count > 0
                ? L($"出错时：{string.Join("、", acc.Errors.Select(n => n.Label))}（四个参数的错误中间件，按注册顺序）", $"On error: {string.Join(", ", acc.Errors.Select(n => n.Label))} (four-argument error middleware, in registration order)")
                : L("没找到错误中间件（四个参数的 (err, req, res, next)）：出错时走 Express 默认的错误处理（打印堆栈、回 500）", "No error middleware found (four arguments: (err, req, res, next)): errors go to Express's default error handler (prints the stack, responds 500)"));
        if (httpCount == 0) notes.Add(L("没有发现 HTTP 路由入口", "No HTTP route entries found"));
        return Assemble(x.Container, L("HTTP 请求", "HTTP request"), root, stages, [], notes, L("入口", "Entry"));
    }

    // ================= 收集注册 =================

    /// <summary>
    /// [start, end) 里 receiver 上的 use / 路由 / on('error')，按位置排；途中把 receiver 传进去的仓库函数（registerSecurity(app)、require('./x')(app)）
    /// 进去接着找（3 层，参数换成它的第一个形参）。seen 防止同一处被收两次（函数定义在同文件、又被调用）。
    /// </summary>
    private static void CollectEvents(Ctx x, string file, int start, int end, string receiver, string? via, int depth, HashSet<long> visited,
        List<AppEvent> into, HashSet<(string, int)> seen, string flavor, Acc acc)
    {
        if (x.Text(file) is not { } t) return;
        var code = t.Code;
        end = Math.Min(end, code.Length);
        if (!acc.Files.Contains(file)) acc.Files.Add(file);
        var items = new List<(int Pos, ChainCall? Call, MapSymbol? Helper, string? HelperName)>();
        foreach (var c in ReceiverCalls(code, receiver, start, end)) items.Add((c.Pos, c, null, null));
        if (depth < 3)
        {
            foreach (Match m in Regex.Matches(code[..end], @"(?<![\w$.])([\w$]+(?:\s*\.\s*[\w$]+)?)\s*\(\s*" + Regex.Escape(receiver) + @"\s*[,)]"))
            {
                if (m.Index < start) continue;
                var name = Regex.Replace(m.Groups[1].Value, @"\s", "");
                if (NotHelpers.Contains(name) || name == receiver || WordBefore(code, m.Index, out _) == "function") continue;
                if (x.Resolve(name, file) is { Symbol: { Kind: SymbolKind.Function or SymbolKind.Method } h }) items.Add((m.Index, null, h, name));
            }
            foreach (Match m in Regex.Matches(t.NoComments[..end], @"require\s*\(\s*['""]([^'""]+)['""]\s*\)\s*\(\s*" + Regex.Escape(receiver) + @"\s*[,)]"))
                if (m.Index >= start && x.ResolveFile(file, m.Groups[1].Value) is { } f && x.DefaultExport(f) is { Kind: SymbolKind.Function or SymbolKind.Method } h)
                    items.Add((m.Index, null, h, x.Label(h)));
        }
        foreach (var item in items.OrderBy(i => i.Pos))
        {
            if (item.Call is { } c)
            {
                if (!seen.Add((file, c.Pos))) continue;
                if (EventOf(x, file, c, receiver, via, start, flavor) is { } ev) into.Add(ev);
                continue;
            }
            var helper = item.Helper!;
            if (!visited.Add(helper.Id) || x.FunctionOf(helper) is not { Params.Count: > 0 } fn || fn.Params[0] == "{}") continue;
            var before = into.Count;
            CollectEvents(x, helper.Path, fn.BodyStart, fn.End, fn.Params[0], item.HelperName, depth + 1, visited, into, seen, flavor, acc);
            if (into.Count > before) acc.Helpers.Add(item.HelperName!);
        }
    }

    /// <summary>一次 receiver.xxx(...) → 注册事件；不相干的调用（set / listen / disable…）返回 null。</summary>
    private static AppEvent? EventOf(Ctx x, string file, ChainCall c, string receiver, string? via, int scopeStart, string flavor)
    {
        var t = x.Text(file)!;
        var (args, _) = SplitArgsAt(t.Code, c.Open);
        string? Lit(int k) => k < args.Count ? ArgParser.Literal(t.NoComments[args[k].Start..args[k].End]) : null;
        var cond = CtxAt(t.Code, c.Pos, scopeStart) == CallContext.Cond;
        switch (c.Name)
        {
            case "use":
            {
                var path = Lit(0) is { } p && p.StartsWith('/') ? p : null;
                var list = new List<UseArg>();
                foreach (var a in args.Skip(path is null ? 0 : 1))
                {
                    if (t.Code[a.Start] == '[')
                    {
                        // use([a, b])
                        var (inner, _) = SplitArgsAt(t.Code, a.Start);
                        list.AddRange(inner.Select(i => Classify(x, file, i.Start, i.End, flavor)));
                    }
                    else list.Add(Classify(x, file, a.Start, a.End, flavor));
                }
                return new AppEvent("use", file, c.Pos, path, list, via, receiver, null, cond);
            }
            case "route" when Lit(0) is { } rp:
                return new AppEvent("route", file, c.Pos, null, [], via, receiver, $"route {rp}", cond);
            case "on" when flavor == "koa" && Lit(0) == "error" && args.Count > 1:
                return new AppEvent("error", file, c.Pos, null, [Classify(x, file, args[1].Start, args[1].End, flavor)], via, receiver, null, cond);
            default:
                if (HttpVerbs.Contains(c.Name) && args.Count >= 2 && Lit(0) is { } vp && (vp.StartsWith('/') || vp == "*"))
                    return new AppEvent("route", file, c.Pos, null, [], via, receiver, $"{(c.Name == "del" ? "DELETE" : c.Name.ToUpperInvariant())} {vp}", cond,
                        args.Count > 2 ? args.Skip(1).Take(args.Count - 2).Select(a => Regex.Replace(t.NoComments[a.Start..a.End].Trim(), @"\s+", " ")).Where(a => a.Length <= 40 && !a.StartsWith('{')).ToList() : null);
                return null;
        }
    }

    /// <summary>路由器（文件, 变量）上的 use / 路由：声明处的链（Router().use(a)）与之后的 r.use(...)。</summary>
    private static List<AppEvent> RouterEvents(Ctx x, string file, string var, RouterDecl? decl, string flavor)
    {
        var list = new List<AppEvent>();
        if (x.Text(file) is not { } t) return list;
        var calls = new List<ChainCall>();
        if (decl is { ChainAt: >= 0 }) calls.AddRange(ChainFrom(t.Code, decl.ChainAt));
        if (var != "default") calls.AddRange(ReceiverCalls(t.Code, var, 0, t.Code.Length));
        foreach (var c in calls.DistinctBy(c => c.Pos).OrderBy(c => c.Pos))
            if (EventOf(x, file, c, var == "default" ? "router" : var, null, 0, flavor) is { Kind: not "error" } ev) list.Add(ev);
        return list;
    }

    // ================= 实参分类 =================

    private static bool IsBuiltinPackage(string pkg, string flavor) => flavor switch
    {
        "koa" => pkg == "koa",
        "nest" => pkg.StartsWith("@nestjs/", StringComparison.Ordinal),
        _ => pkg == "express",
    };

    /// <summary>use(...) 的一个实参（文件里的 [s, e)）是什么：内联函数 / 仓库函数 / 工厂调用 / 内置或第三方 / 路由器挂载。</summary>
    internal static UseArg Classify(Ctx x, string file, int s, int e, string flavor, int depth = 0)
    {
        var t = x.Text(file)!;
        var code = t.Code[s..e].Trim();
        var nc = t.NoComments[s..e].Trim();
        if (Regex.IsMatch(code, @"^(?:async\s+)?(?:function\b|\(|[\w$]+\s*=>)"))
            return FunctionAt(file, t.Code, s, e) is { } fn ? new InlineArg(fn, s, e) : new UnknownArg(nc, s, e);
        Match m;
        if ((m = Regex.Match(code, @"^([\w$]+)\s*\.\s*(?:routes|middleware)\s*\(\s*\)$")).Success)
            return RouterRef(x, m.Groups[1].Value, file, s, e) ?? (UseArg)new UnknownArg(nc, s, e);
        if ((m = Regex.Match(code, @"^([\w$]+)\s*\.\s*allowedMethods\s*\(")).Success)
            return new ExternalArg(m.Groups[1].Value + ".allowedMethods", "koa-router", false, s, e);
        if ((m = Regex.Match(nc, @"^require\s*\(\s*['""]([^'""]+)['""]\s*\)$")).Success)
        {
            var spec = m.Groups[1].Value;
            if (x.ResolveFile(file, spec) is not { } f) return new ExternalArg(spec, spec, IsBuiltinPackage(spec, flavor), s, e);
            if (x.ExportedRouter(f) is { } rv) return new MountArg(f, rv, s, e);
            return x.DefaultExport(f) is { Kind: SymbolKind.Function or SymbolKind.Method } d ? new FnArg(d, s, e) : new UnknownArg(nc, s, e);
        }
        if (Regex.IsMatch(code, @"^[\w$]+$")) return Ident(x, code, file, s, e, flavor, depth);
        if ((m = Regex.Match(code, @"^([\w$]+)\s*\.\s*([\w$]+)$")).Success)
        {
            var label = m.Groups[1].Value + "." + m.Groups[2].Value;
            if (RouterRef(x, m.Groups[1].Value, file, s, e) is { } mount && m.Groups[2].Value == "router") return mount;
            var r = x.Resolve(label, file);
            if (r?.Symbol is { Kind: SymbolKind.Function or SymbolKind.Method } f) return new FnArg(f, s, e);
            if (r?.Package is { } pkg) return new ExternalArg(label, pkg, IsBuiltinPackage(pkg, flavor), s, e);
            if (m.Groups[1].Value == "express") return new ExternalArg(label, "express", true, s, e);
            return new UnknownArg(nc, s, e);
        }
        if ((m = Regex.Match(code, @"^((?:[\w$]+\s*\.\s*)*[\w$]+)\s*(?:<[^<>]*>)?\s*\(")).Success)
        {
            var callee = Regex.Replace(m.Groups[1].Value, @"\s", "");
            var parts = callee.Split('.');
            var r = x.Resolve(string.Join('.', parts.Take(2)), file);
            if (parts[0] == "express" || r?.Package == "express" && flavor == "express") return new ExternalArg(callee, "express", true, s, e);
            if (r?.Package is { } pkg) return new ExternalArg(callee, pkg, IsBuiltinPackage(pkg, flavor), s, e);
            if (r?.Symbol is { Kind: SymbolKind.Function or SymbolKind.Method } f) return new FactoryArg(f, ReturnedFunction(x, f), s, e);
            return new ExternalArg(callee, null, false, s, e);
        }
        return new UnknownArg(nc, s, e);
    }

    /// <summary>标识符实参：本文件 / 导入的路由器 → 挂载；仓库函数；npm 包；const limiter = rateLimit({…}) 这种变量看它的初始值。</summary>
    private static UseArg Ident(Ctx x, string name, string file, int s, int e, string flavor, int depth)
    {
        if (RouterRef(x, name, file, s, e) is { } mount) return mount;
        var r = x.Resolve(name, file);
        if (r?.Symbol is { Kind: SymbolKind.Function or SymbolKind.Method } f) return new FnArg(f, s, e);
        if (r?.Package is { } pkg) return new ExternalArg(name, pkg, IsBuiltinPackage(pkg, flavor), s, e);
        var t = x.Text(file)!;
        if (depth < 2 && Regex.Match(t.Code, @"(?<![\w$.])(?:const|let|var)\s+" + Regex.Escape(name) + @"\s*(?::\s*[\w$.<>]+\s*)?=\s*") is { Success: true } d)
        {
            var rs = d.Index + d.Length;
            var re = ExpressionEnd(t.Code, rs);
            if (re > rs)
            {
                var inner = Classify(x, file, rs, re, flavor, depth + 1);
                return inner switch
                {
                    ExternalArg ea => ea with { Label = L($"{name}（{ea.Label}）", $"{name} ({ea.Label})"), Start = s, End = e },
                    UnknownArg => new UnknownArg(name, s, e),
                    _ => inner,
                };
            }
        }
        return new UnknownArg(name, s, e);
    }

    /// <summary>名字是不是路由器：本文件的路由器变量，或 import / require 进来的文件导出的路由器。</summary>
    private static MountArg? RouterRef(Ctx x, string name, string file, int s, int e)
    {
        if (x.Routers.ContainsKey((file, name))) return new MountArg(file, name, s, e, name);
        if (x.Resolve(name, file) is not { File: { } f } r || f == file) return null;
        var rv = r.Export == "default" ? x.ExportedRouter(f) : x.Routers.ContainsKey((f, r.Export)) ? r.Export : null;
        return rv is null ? null : new MountArg(f, rv, s, e, name);
    }

    /// <summary>从 i 起的一个表达式的末尾：顶层的 ';'，或顶层换行且下一行不是 '.' 续写。</summary>
    private static int ExpressionEnd(string code, int i)
    {
        var depth = 0;
        for (var k = i; k < code.Length; k++)
        {
            var c = code[k];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') { if (--depth < 0) return k; }
            else if (depth == 0 && c == ';') return k;
            else if (depth == 0 && c == '\n')
            {
                var n = SkipWs(code, k);
                if (n >= code.Length || code[n] != '.') return k;
            }
        }
        return code.Length;
    }

    /// <summary>工厂函数 return 出来的中间件：return (req, res, next) =&gt; {…} / return function (…) {…}，或柯里化箭头 opts =&gt; (req, res, next) =&gt; …。</summary>
    private static JsFunction? ReturnedFunction(Ctx x, MapSymbol factory)
    {
        if (x.FunctionOf(factory) is not { } fn || x.Text(factory.Path) is not { } t) return null;
        var code = t.Code;
        if (fn.BodyStart < code.Length && code[fn.BodyStart] != '{')
            return FunctionAt(factory.Path, code, fn.BodyStart, fn.End, 80) is { } inner && code[fn.BodyStart..inner.Start].Trim() is "" or "async" ? inner : null;
        foreach (Match m in Regex.Matches(code[..Math.Min(fn.End, code.Length)], @"(?<![\w$.])return\s+"))
        {
            if (m.Index < fn.BodyStart) continue;
            var after = m.Index + m.Length;
            var head = code[after..Math.Min(code.Length, after + 200)];
            if (!Regex.IsMatch(head, @"^(?:async\s+)?(?:function\b[^(]*)?\(|^(?:async\s+)?[\w$]+\s*=>")) continue;
            if (FunctionAt(factory.Path, code, after, fn.End, 200) is { } r) return r;
        }
        return null;
    }

    // ================= 中间件分析与画节点 =================

    /// <summary>中间件实参 → 体按 next 切开（Express：第 3 个参数是 next，四个参数是错误中间件、next 是第 4 个；Koa：(ctx, next)）。不是函数类实参返回 null。</summary>
    internal static Mw? AnalyzeMw(Ctx x, UseArg arg, string flavor, string receiver)
    {
        JsFunction? fn;
        List<FrameworkCall> calls;
        MapSymbol? sym;
        string label;
        string path;
        var notes = new List<string>();
        switch (arg)
        {
            case InlineArg i:
                fn = i.Fn; sym = null; path = fn.Path; label = L($"{receiver}.use（内联中间件）", $"{receiver}.use (inline middleware)");
                calls = x.CallsInSpan(fn.Path, fn.Start, fn.End, fn.BodyStart);
                break;
            case FnArg f:
                sym = f.Symbol; path = sym.Path; label = x.Label(sym); fn = x.FunctionOf(sym); calls = x.CallsOf(sym);
                break;
            case FactoryArg fa:
                sym = fa.Factory; path = sym.Path; label = x.Label(sym) + "()"; fn = fa.Returned;
                calls = fa.Returned is { } r ? x.CallsInSpan(sym.Path, r.Start, r.End, r.BodyStart) : x.CallsOf(sym);
                notes.Add(fa.Returned is null ? L($"中间件工厂 {x.Label(sym)}(…)：没找到它 return 的中间件，列的是工厂本身的调用", $"Middleware factory {x.Label(sym)}(…): the middleware it returns was not found; listing the factory's own calls") : L($"中间件工厂：{x.Label(sym)}(…) 返回的中间件", $"Middleware factory: middleware returned by {x.Label(sym)}(…)"));
                break;
            default:
                return null;
        }
        var ps = fn?.Params ?? [];
        var isError = flavor != "koa" && ps.Count >= 4;
        if (isError && arg is InlineArg) label = L($"{receiver}.use（内联错误中间件）", $"{receiver}.use (inline error middleware)");
        var nextName = flavor == "koa" ? ps.ElementAtOrDefault(1) : isError ? ps.ElementAtOrDefault(3) : ps.ElementAtOrDefault(2);
        var terminal = flavor != "koa" && ps.Count == 2;
        var split = fn is null ? new BodySplit(calls.Where(JsKeep).ToList(), [], [], false, false) : SplitAtNext(x, fn, calls, nextName);
        if (!isError && fn is not null)
        {
            if (split.Conditional) notes.Add(L("有条件（不是每条路都调 next：有分支直接返回 / 抛异常）", "Conditional (not every path calls next: some branches return / throw)"));
            if (!split.HasNext) notes.Add(terminal ? L("两个参数、不调 next：请求在这里结束", "Two arguments, no next call: the request ends here") : L("没找到 next 调用", "No next call found"));
        }
        return new Mw(sym, label, sym?.QualifiedName ?? "", sym is null ? "external-group" : "call", path, split, isError, terminal, notes);
    }

    private static bool LooksLikeErrorHandler(string label) => Regex.IsMatch(label, @"(?i)error(handler|middleware)|errorhandler|onerror");

    /// <summary>一个作用域（app 或某个路由器）里的事件 → 节点：中间件（actor middleware）与挂载（actor controller，Mount = true），按注册顺序；收尾的累计进 acc。</summary>
    private static List<(FlowNodeDto Node, bool Mount)> ProcessScope(Ctx x, List<AppEvent> events, string prefix, bool router, Acc acc, string flavor, int depth,
        HashSet<(string, string)> visiting)
    {
        var result = new List<(FlowNodeDto, bool)>();
        var lastRoute = events.FindLastIndex(e => e.Kind == "route" || e.Args.Any(a => a is MountArg));
        string? firstRoute = null;
        var fwName = flavor == "koa" ? "Koa" : flavor == "nest" ? "Nest" : "Express";
        for (var idx = 0; idx < events.Count; idx++)
        {
            var ev = events[idx];
            var where = x.Where(ev.File, ev.Pos);
            var line = x.LineOf(ev.File, ev.Pos);
            var via = ev.Via is null ? "" : L($"经 {ev.Via}()；", $"via {ev.Via}(); ");
            var scope = JoinRoute(prefix, ev.Path ?? "");
            var regCond = ev.Conditional ? L("；条件分支里注册", "; registered inside a conditional branch") : "";
            if (ev.Kind == "route")
            {
                firstRoute ??= ev.RouteLabel;
                if (ev.RouteMiddleware is { Count: > 0 } rm) { acc.RouteMiddlewareRoutes++; foreach (var n in rm) if (!acc.RouteMiddleware.Contains(n)) acc.RouteMiddleware.Add(n); }
                continue;
            }
            if (ev.Kind == "error")
            {
                foreach (var arg in ev.Args)
                {
                    var mw = AnalyzeMw(x, arg, flavor, ev.Receiver);
                    if (mw?.Symbol is { } es) acc.TailSymbols.Add(es);
                    var kids = mw is null ? [] : Rows(mw.Split.Pre.Concat(mw.Split.Post), mw.Path, StageTail, null);
                    acc.Errors.Add(Node(mw?.Symbol?.Id, mw?.Symbol is null ? $"{ev.Receiver}.on('error')" : mw.Label, mw?.Qn ?? "", mw?.Kind ?? "external-group", StageTail,
                        L($"出错时（{ev.Receiver}.on('error')：Koa 的错误事件，{where}）", $"On error ({ev.Receiver}.on('error'): Koa's error event, {where})"), kids, ev.File, line));
                }
                continue;
            }
            var after = lastRoute >= 0 && idx > lastRoute;
            foreach (var arg in ev.Args)
            {
                if (arg is MountArg mount)
                {
                    var node = MountNode(x, mount, ev, prefix, acc, flavor, depth, visiting);
                    firstRoute ??= node.Label;
                    result.Add((node, true));
                    continue;
                }
                var scopeNote = router ? L($"router 级中间件（只对 {scope} 下的路由生效，{where}）", $"router-level middleware (applies only to routes under {scope}, {where})") : ev.Path is not null ? L($"只对 {ev.Path} 生效（{where}）", $"applies only to {ev.Path} ({where})") : null;
                var routeAfter = firstRoute is null ? "" : L($"；注册在 {firstRoute} 之后：对它前面注册的路由不生效", $"; registered after {firstRoute}: does not apply to routes registered before it");
                if (arg is ExternalArg ext)
                {
                    var kind = ext.Builtin ? L($"{fwName} 内置", $"{fwName} built-in") : ext.Package is { } pkg ? L($"第三方中间件（{pkg}）", $"third-party middleware ({pkg})") : L("不在仓库里", "not in the repo");
                    var note = via + kind + (scopeNote is null ? L($"（{where}）", $" ({where})") : L("；", "; ") + scopeNote) + regCond;
                    if (LooksLikeErrorHandler(ext.Label)) { acc.Errors.Add(Node(null, ext.Label, "", "external-group", StageTail, L("出错时：", "On error: ") + note, [], ev.File, line)); continue; }
                    if (after) { acc.AfterRoutes.Add(Node(null, ext.Label, "", "external-group", StageTail, AfterRoutesNote(where) + L("；", "; ") + kind, [], ev.File, line)); continue; }
                    if (router) acc.RouterMiddleware++;
                    result.Add((Node(null, ext.Label, "", "external-group", StageMiddleware, note + routeAfter, [], ev.File, line), false));
                    continue;
                }
                if (arg is UnknownArg u)
                {
                    var text = u.Text.Length > 40 ? u.Text[..40] + "…" : u.Text;
                    result.Add((Node(null, $"{ev.Receiver}.use({text})", "", "external-group", StageMiddleware, via + L($"没认出是什么（{where}）", $"not recognized ({where})"), [], ev.File, line), false));
                    continue;
                }
                if (AnalyzeMw(x, arg, flavor, ev.Receiver) is not { } m) continue;
                var extra = m.Notes.Count == 0 ? "" : L("；", "; ") + string.Join(L("；", "; "), m.Notes);
                if (m.IsError)
                {
                    if (m.Symbol is { } es) acc.TailSymbols.Add(es);
                    acc.Errors.Add(Node(m.Symbol?.Id, m.Label, m.Qn, m.Kind, StageTail,
                        L($"出错时（四个参数的错误中间件，{where}）", $"On error (four-argument error middleware, {where})") + (via.Length > 0 ? L("；", "; ") + via.TrimEnd('；', ';', ' ') : "") + (router ? L($"；只管 {scope} 下的路由", $"; only for routes under {scope}") : ""),
                        Rows(m.Split.Pre.Concat(m.Split.Post), m.Path, StageTail, null), ev.File, line));
                    continue;
                }
                if (after)
                {
                    if (m.Symbol is { } asym) acc.TailSymbols.Add(asym);
                    acc.AfterRoutes.Add(Node(m.Symbol?.Id, m.Label, m.Qn, m.Kind, StageTail, AfterRoutesNote(where) + (m.Terminal ? L("（404 兜底）", " (404 fallback)") : "") + extra,
                        Rows(m.Split.Pre.Concat(m.Split.Post), m.Path, StageTail, null), ev.File, line));
                    continue;
                }
                if (m.Symbol is { } ms) acc.MwSymbols.Add(ms);
                if (router) acc.RouterMiddleware++;
                result.Add((Node(m.Symbol?.Id, m.Label, m.Qn, m.Kind, StageMiddleware, via + (scopeNote ?? L($"自研中间件（{where}）", $"custom middleware ({where})")) + extra + regCond + routeAfter,
                    Rows(m.Split.Pre, m.Path, StageMiddleware, null), ev.File, line), false));
                acc.Posts.Add(Rows(m.Split.Post, m.Path, StageTail, L($"{m.Label} 后置", $"{m.Label} after")));
                acc.Finishes.AddRange(Rows(m.Split.Finish, m.Path, StageTail, L($"{m.Label}：响应发完时（res.on('finish')）", $"{m.Label}: once the response has been sent (res.on('finish'))")));
                foreach (var c in m.Split.Post.Concat(m.Split.Finish))
                    if (c.TargetId is { } tid && x.ById.TryGetValue(tid, out var ts) && ts.Kind != SymbolKind.External) acc.TailSymbols.Add(ts);
            }
        }
        return result;
    }

    private static string AfterRoutesNote(string where) => L($"路由之后注册（{where}）：没有路由匹配、或路由调了 next() 时才走到", $"Registered after the routes ({where}): reached only when no route matches or a route calls next()");

    /// <summary>挂载节点：label "挂载 /api（router）"，指向路由器文件的模块；子节点是路由器里的 router.use(...) 与再往下的挂载。</summary>
    private static FlowNodeDto MountNode(Ctx x, MountArg mount, AppEvent ev, string prefix, Acc acc, string flavor, int depth, HashSet<(string, string)> visiting)
    {
        var decl = x.Routers.GetValueOrDefault((mount.File, mount.Var));
        var full = JoinRoute(prefix, ev.Path ?? "", decl?.Prefix ?? "");
        var name = mount.Display ?? (mount.Var == "default" ? Ctx.FileStem(mount.File) : mount.Var);
        acc.Mounts.Add(full + " → " + MapSymbol.FileName(mount.File));
        var kids = new List<FlowNodeDto>();
        if (depth < 6 && visiting.Add((mount.File, mount.Var)))
        {
            kids = ProcessScope(x, RouterEvents(x, mount.File, mount.Var, decl, flavor), full, true, acc, flavor, depth + 1, visiting).Select(p => p.Node).ToList();
            visiting.Remove((mount.File, mount.Var));
        }
        var routes = x.Entries.Count(e => e.Kind == "http" && e.File == mount.File);
        var mod = x.Module(mount.File);
        var note = (ev.Via is null ? "" : L($"经 {ev.Via}()；", $"via {ev.Via}(); ")) + L($"路由挂载（{x.Where(ev.File, ev.Pos)}）", $"Route mount ({x.Where(ev.File, ev.Pos)})") + (routes > 0 ? L($"：{MapSymbol.FileName(mount.File)} 里 {routes} 条路由", $": {Plural(routes, "route", "routes")} in {MapSymbol.FileName(mount.File)}") : "");
        return Node(mod?.Id, L($"挂载 {full}（{name}）", $"Mount {full} ({name})"), mod?.QualifiedName ?? mount.File, mod is null ? "external-group" : "call", StageController, note,
            Cap(kids, StageMiddleware), ev.File, x.LineOf(ev.File, ev.Pos));
    }
}
