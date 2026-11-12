using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using static CodeAtlas.Server.FrameworkFlowBuilder;

namespace CodeAtlas.Server;

/// <summary>
/// 框架流程 · Java（java-service）：Spring MVC / WebFlux / Spring Cloud Gateway。由 <see cref="FrameworkFlowBuilder.Build"/> 按容器 SubKind 分派进来。
/// 注解、继承关系不一定都在索引里，规则以源码文本为准（去掉注释与字符串后扫，注解实参用原文；见 <see cref="JavaSource"/>）。阶段与识别规则：
/// <list type="number">
/// <item>middleware（过滤器）：implements Filter（有 doFilter）/ extends OncePerRequestFilter、GenericFilterBean 一类（Servlet）；implements WebFilter（WebFlux）。
/// 注册：@Component 一类、@Bean 方法的返回类型、FilterRegistrationBean（setFilter / new / 泛型实参认过滤器，setOrder、addUrlPatterns、setEnabled(false)）、@WebFilter；
/// 顺序 = FilterRegistrationBean.setOrder ＞ @Bean 方法上的 @Order ＞ 类上的 @Order ＞ Ordered.getOrder()，都没有按 LOWEST_PRECEDENCE；小的在外层先执行。
/// Spring Security：有 SecurityFilterChain / SecurityWebFilterChain Bean、WebSecurityConfigurerAdapter 或 spring-boot-starter-security 依赖时画一行"Spring Security 过滤器链"
/// （external-group，order -100），HttpSecurity.addFilterBefore / After / At 加进去的过滤器按书写顺序挂在它下面。
/// @Bean 返回框架过滤器（CorsFilter…）画成 external-group；@Bean 返回 Filter / WebFilter 本身（lambda）挂那个 @Bean 方法。
/// doFilter / doFilterInternal / filter 在 chain.doFilter(…) / chain.filter(…) 之前的调用是子节点，之后的进收尾（Note "XxxFilter 后置"，按洋葱倒序）。</item>
/// <item>dispatcher：一行外部节点 DispatcherServlet（MVC）/ DispatcherHandler（WebFlux），下面挂拦截器、网关链、控制器与收尾。</item>
/// <item>interceptor（拦截器，只 MVC）：addInterceptors(InterceptorRegistry) 里 registry.addInterceptor(…) 的书写顺序（.order(n) 优先，同序按书写），
/// .addPathPatterns / .excludePathPatterns 进 Note；仓库里的 HandlerInterceptor：preHandle 的调用作子节点，postHandle / afterCompletion 进收尾（倒序）。
/// 没注册的 HandlerInterceptor 实现追加在最后，阶段 OrderKnown = false。</item>
/// <item>Spring Cloud Gateway（WebFlux + gateway 依赖）：DispatcherHandler 下一行"网关过滤器链"，GlobalFilter（getOrder / @Order）与 default-filters（配置顺序 1、2…）混排，
/// 名字对得上的 *GatewayFilterFactory 挂它的 apply；各路由的 filters 合成一行；最后一行"转发到下游服务"。</item>
/// <item>controller：占位 Kind = "controllers"，Count = HTTP 入口数；下面挂切面与控制器之后的事。</item>
/// <item>aspect（切面）：@Aspect 类按 @Order / getOrder 排（没有就 LOWEST，同序 → OrderKnown = false）；@Around 在 proceed() 前后拆分、@Before 进入时、
/// @AfterReturning / @AfterThrowing / @After 与 @Around 的后置进收尾（同一切面内出来的顺序：AfterReturning / AfterThrowing → After → Around 后置）；切点表达式（含 @Pointcut 引用）写进 Note。
/// @Bean 返回的框架切面（TimedAspect…）与 @Cacheable 一类的缓存代理画成 external-group。</item>
/// <item>tail（收尾）：各后置、@Transactional（一行说明：方法返回后提交）、ResponseBodyAdvice.beforeBodyWrite、@ExceptionHandler（控制器内的先，再 @ControllerAdvice 按 @Order；Note "出错时"）、
/// HandlerExceptionResolver。</item>
/// </list>
/// 不是 Web 的容器（没有 HTTP 入口、没有 Web 依赖、也没认出过滤器 / 拦截器）画成"入口"占位，下面只有切面与事务。找不到的阶段不画，Notes 里说为什么。
/// </summary>
public static partial class SpringFrameworkFlow
{
    private static readonly HashSet<string> ServletFilterBases = new(StringComparer.Ordinal)
    {
        "OncePerRequestFilter", "GenericFilterBean", "HttpFilter", "AbstractRequestLoggingFilter", "AbstractAuthenticationProcessingFilter",
        "UsernamePasswordAuthenticationFilter", "BasicAuthenticationFilter", "AuthenticationFilter", "AbstractPreAuthenticatedProcessingFilter",
    };
    private static readonly HashSet<string> InterceptorTypes = new(StringComparer.Ordinal) { "HandlerInterceptor", "AsyncHandlerInterceptor", "HandlerInterceptorAdapter", "WebRequestInterceptor" };
    private static readonly HashSet<string> GatewayFactoryBases = new(StringComparer.Ordinal)
        { "AbstractGatewayFilterFactory", "GatewayFilterFactory", "AbstractNameValueGatewayFilterFactory", "AbstractChangeRequestUriGatewayFilterFactory" };
    private static readonly string[] ComponentAnnotations = ["Component", "Service", "Configuration", "Repository", "Controller", "RestController", "ControllerAdvice", "RestControllerAdvice"];
    private static readonly string[] AdviceAnnotations = ["Around", "Before", "After", "AfterReturning", "AfterThrowing"];
    private static readonly string[] CacheAnnotations = ["Cacheable", "CachePut", "CacheEvict", "Caching"];
    private static readonly string[] ServletChainTypes = ["FilterChain"];
    /// <summary>HttpSecurity 上配内置功能的方法（Note 里列出来，看得出 csrf 关没关、是不是无状态）。</summary>
    private static readonly HashSet<string> SecurityDsl = new(StringComparer.Ordinal)
    {
        "csrf", "cors", "httpBasic", "formLogin", "oauth2Login", "oauth2ResourceServer", "sessionManagement", "authorizeRequests", "authorizeHttpRequests",
        "authorizeExchange", "exceptionHandling", "logout", "headers", "rememberMe", "anonymous", "x509", "saml2Login",
    };
    private static readonly string[] ReactiveChainTypes = ["WebFilterChain", "GatewayFilterChain"];

    private static readonly Regex SecurityChainBean = new(@"\b(SecurityFilterChain|SecurityWebFilterChain)\s+(\w+)\s*\(");
    private static readonly Regex SecurityAdapter = new(@"\bextends\s+(?:[\w.]+\.)?WebSecurityConfigurerAdapter\b");
    private static readonly Regex AddSecurityFilter = new(@"\.\s*(addFilter(?:Before|After|At)?)\s*\(");
    private static readonly Regex ClassLiteral = new(@"([\w.]+)\s*\.\s*class\b");

    /// <summary>一个过滤器 / 拦截器 / 切面通知：一行节点（自研的挂方法，框架的 external-group）加它的前置调用、后置调用与已经建好的收尾节点。</summary>
    private sealed class Unit
    {
        public required string Label;
        /// <summary>后置 Note 里的名字（类短名）。</summary>
        public required string Owner;
        public string QualifiedName = "";
        public long? SymbolId;
        public string Kind = "call";
        public readonly List<string> Notes = [];
        public long? Order;
        public string? OrderHow;
        public string? Path;
        public int? Line;
        public string? CallPath;
        public List<FrameworkCall> Pre = [];
        public List<FrameworkCall> Post = [];
        public readonly List<Unit> Inner = [];
        public readonly List<FlowNodeDto> Exits = [];
        public readonly List<FlowNodeDto> After = [];
        public MapSymbol? Symbol;
        public bool Registered = true;
        public int Seq;
        public long Effective => Order ?? Lowest;

        public FlowNodeDto ToNode(World w, string actor)
        {
            var kids = CallPath is null ? new List<FlowNodeDto>() : w.CallNodes(Pre, CallPath, actor);
            kids.AddRange(Inner.Select(i => i.ToNode(w, actor)));
            return Node(Kind == "call" ? SymbolId : null, Label, QualifiedName, Kind, actor, Notes.Count == 0 ? null : string.Join(L("；", "; "), Notes), Cap(kids, actor), Path, Line);
        }

        /// <summary>自己与里面的（安全链里的过滤器），进入顺序。</summary>
        public IEnumerable<Unit> Flatten() => Inner.SelectMany(i => i.Flatten()).Prepend(this);
    }

    public static FrameworkFlowDto Build(FrameworkInput input)
    {
        var w = new World(input);
        var container = input.Container;
        var notes = new List<string>();
        var entries = input.Entries.Where(e => e.ContainerKey == container.Key && !PipelineBuilder.IsTest(e.File, "")).ToList();
        var http = entries.Where(e => e.Kind == "http").ToList();

        var deps = Dependencies(w.Src.ReadFile, container.Path ?? container.Members.FirstOrDefault() ?? "");
        bool Dep(Func<string, bool> p) => deps.Any(p);
        var mvcDep = Dep(d => d.Contains("starter-web", StringComparison.Ordinal) && !d.Contains("webflux", StringComparison.Ordinal)
                              || d.Contains("config-server", StringComparison.Ordinal) || d.Contains("eureka-server", StringComparison.Ordinal) || d.Contains("admin-starter-server", StringComparison.Ordinal)
                              || d.Contains("gateway", StringComparison.Ordinal) && d.Contains("webmvc", StringComparison.Ordinal));
        var gatewayDep = Dep(d => d.Contains("gateway", StringComparison.Ordinal) && d.StartsWith("spring-cloud", StringComparison.Ordinal) && !d.Contains("webmvc", StringComparison.Ordinal) && !d.Contains("mvc", StringComparison.Ordinal));
        var fluxDep = Dep(d => d.Contains("webflux", StringComparison.Ordinal)) || gatewayDep;

        // ---- 类型分类 ----
        var servletFilters = new List<MapSymbol>();
        var webFilters = new List<MapSymbol>();
        var globalFilters = new List<MapSymbol>();
        var factories = new List<MapSymbol>();
        var interceptorClasses = new List<MapSymbol>();
        var responseAdvices = new List<MapSymbol>();
        var exceptionResolvers = new List<MapSymbol>();
        foreach (var t in w.Types.Where(t => t.Kind == SymbolKind.Class && !w.IsAbstract(t)))
        {
            var supers = w.Supers(t);
            if (supers.Overlaps(ServletFilterBases) || supers.Contains("Filter") && w.MethodIn(t, "doFilter") is not null) servletFilters.Add(t);
            else if (supers.Contains("WebFilter")) webFilters.Add(t);
            else if (supers.Contains("GlobalFilter")) globalFilters.Add(t);
            else if (supers.Overlaps(GatewayFactoryBases)) factories.Add(t);
            if (supers.Overlaps(InterceptorTypes)) interceptorClasses.Add(t);
            if (supers.Contains("ResponseBodyAdvice")) responseAdvices.Add(t);
            if (supers.Contains("HandlerExceptionResolver") || supers.Contains("AbstractHandlerExceptionResolver") || supers.Contains("DataFetcherExceptionHandler")) exceptionResolvers.Add(t);
        }
        var reactive = fluxDep && !mvcDep || !mvcDep && !fluxDep && (webFilters.Count > 0 || globalFilters.Count > 0) && servletFilters.Count == 0 && interceptorClasses.Count == 0;
        var gateway = reactive && (gatewayDep || globalFilters.Count > 0 || factories.Count > 0);

        // ---- 过滤器 ----
        var (filterUnits, filterNotes, filterOrderKnown) = Filters(w, reactive, reactive ? webFilters : servletFilters, deps);

        // ---- 拦截器（只 MVC）----
        var (interceptors, interceptorNotes, interceptorsOrderKnown) = reactive ? (new List<Unit>(), new List<string>(), true) : Interceptors(w, interceptorClasses);

        var isWeb = http.Count > 0 || mvcDep || fluxDep || filterUnits.Count > 0 || interceptors.Count > 0;
        // 纯 gRPC 服务（grpc-java 的 ServerBuilder，没有 Web 依赖）：画 gRPC 这条链（拦截器 → 服务方法），与别的语言一致
        if (!isWeb && FrameworkFlowBuilder.GrpcOnly(input, "java", []) is { } grpcFlow) return grpcFlow;

        // ---- 网关 ----
        Unit? gatewayUnit = null;
        var gatewayNotes = new List<string>();
        if (gateway) (gatewayUnit, gatewayNotes) = Gateway(w, globalFilters, factories, container.Path ?? container.Members.FirstOrDefault() ?? "");

        // ---- 切面、代理 ----
        var (aspectEnter, aspectExit, aspectSymbols, aspectNotes, aspectsOrderKnown) = Aspects(w);

        // ---- 收尾：事务、响应体改写、异常处理 ----
        var tailSymbols = new List<MapSymbol>();
        var transactional = Transactional(w);
        var bodyAdvice = new List<FlowNodeDto>();
        foreach (var t in responseAdvices)
            if (w.MethodIn(t, "beforeBodyWrite") is { } m)
            {
                tailSymbols.Add(m);
                bodyAdvice.Add(Node(m.Id, $"{World.ShortName(t)}.beforeBodyWrite", m.QualifiedName, "call", StageTail, L("控制器返回后、写响应体之前（ResponseBodyAdvice）", "after the controller returns, before the response body is written (ResponseBodyAdvice)"),
                    w.CallNodes(w.Calls(m), m.Path, StageTail), m.Path, m.StartLine));
            }
        var (exceptionNodes, exceptionSymbols) = ExceptionHandlers(w, exceptionResolvers);
        tailSymbols.AddRange(exceptionSymbols);

        // ================= 组装 =================
        // 控制器之后：切面进入 → 事务 → 切面出来 → 响应体改写
        var controllerKids = new List<FlowNodeDto>();
        controllerKids.AddRange(aspectEnter);
        if (transactional is not null) controllerKids.Add(transactional);
        controllerKids.AddRange(aspectExit);
        controllerKids.AddRange(bodyAdvice);

        var rootKids = new List<FlowNodeDto>();
        var filterFlat = filterUnits.SelectMany(u => u.Flatten()).ToList();
        FlowNodeDto? entryNode = null;
        if (isWeb)
        {
            rootKids.AddRange(filterUnits.Select(u => u.ToNode(w, StageMiddleware)));
            var dispatcherKids = new List<FlowNodeDto>();
            dispatcherKids.AddRange(interceptors.Select(u => u.ToNode(w, StageInterceptor)));
            if (gatewayUnit is not null)
            {
                var g = gatewayUnit.ToNode(w, StageMiddleware);
                var posts = Enumerable.Reverse(gatewayUnit.Inner).SelectMany(u => u.Post.Where(JavaKeep).Select(c => CallNode(c, u.CallPath, StageTail, L($"{u.Owner} 后置", $"{u.Owner} after")))).ToList();
                dispatcherKids.Add(g with { Children = [.. g.Children, .. posts] });
            }
            if (http.Count > 0)
            {
                entryNode = Node(null, L("控制器", "Controllers"), "", "controllers", StageController, null, controllerKids, null, null, http.Count);
                dispatcherKids.Add(entryNode);
            }
            else dispatcherKids.AddRange(controllerKids);
            // 出来：postHandle（倒序）→ 出错时的异常处理 → afterCompletion（倒序）
            foreach (var u in Enumerable.Reverse(interceptors)) dispatcherKids.AddRange(u.Exits);
            dispatcherKids.AddRange(exceptionNodes);
            foreach (var u in Enumerable.Reverse(interceptors)) dispatcherKids.AddRange(u.After);
            var dispatcherLabel = reactive ? L("DispatcherHandler（Spring WebFlux 内置）", "DispatcherHandler (built into Spring WebFlux)") : L("DispatcherServlet（Spring 内置）", "DispatcherServlet (built into Spring)");
            var dispatcherNote = reactive
                ? L("WebFlux 的分派器：WebFilter 之后进来，按路由找到处理函数", "WebFlux dispatcher: entered after the WebFilters, finds the handler function by route") + (gateway ? L("；路由转发的请求交给网关过滤器链，本服务自己的接口走控制器", "; routed requests go to the gateway filter chain, this service's own endpoints go to controllers") : "")
                : L("Spring MVC 的前端控制器：过滤器之后进来，按 @RequestMapping 找到控制器方法，前后跑拦截器，出错交给 @ExceptionHandler", "Spring MVC front controller: entered after the filters, finds the controller method by @RequestMapping, runs interceptors around it, and hands errors to @ExceptionHandler");
            rootKids.Add(Node(null, dispatcherLabel, "", "external-group", StageDispatcher, dispatcherNote, dispatcherKids, null, null));
            // 过滤器后置：洋葱往回走
            foreach (var u in Enumerable.Reverse(filterFlat))
                foreach (var c in u.Post.Where(JavaKeep))
                {
                    rootKids.Add(CallNode(c, u.CallPath, StageTail, L($"{u.Owner} 后置", $"{u.Owner} after")));
                    if (c.TargetId is { } tid && w.ById.TryGetValue(tid, out var ts) && ts.Kind != SymbolKind.External) tailSymbols.Add(ts);
                }
        }
        else
        {
            var others = entries.Count;
            if (others > 0)
            {
                entryNode = Node(null, L("入口（常驻服务 / 任务 / 消息消费）", "Entry (hosted services / jobs / message consumers)"), "", "controllers", StageController, null, controllerKids, null, null, others);
                rootKids.Add(entryNode);
            }
            else rootKids.AddRange(controllerKids);
        }

        // ---- 阶段 ----
        var present = new HashSet<string>(StringComparer.Ordinal);
        void Collect(FlowNodeDto n) { present.Add(n.Actor); foreach (var c in n.Children) Collect(c); }
        foreach (var k in rootKids) Collect(k);
        var stages = new List<FrameworkStageDto>();
        if (present.Contains(StageMiddleware))
        {
            var syms = filterFlat.Concat(gatewayUnit?.Inner ?? []).Select(u => u.Symbol).Where(s => s is not null).DistinctBy(s => s!.Id).Select(s => w.Dto(s!)).ToList();
            stages.Add(new FrameworkStageDto(StageMiddleware, L("过滤器", "Filters"), filterOrderKnown, syms));
        }
        if (present.Contains(StageDispatcher)) stages.Add(new FrameworkStageDto(StageDispatcher, L("分派器", "Dispatcher"), true, []));
        if (present.Contains(StageInterceptor))
            stages.Add(new FrameworkStageDto(StageInterceptor, L("拦截器", "Interceptors"), interceptorsOrderKnown,
                interceptors.Select(u => u.Symbol).Where(s => s is not null).DistinctBy(s => s!.Id).Select(s => w.Dto(s!)).ToList()));
        if (entryNode is not null)
            stages.Add(new FrameworkStageDto(StageController, isWeb ? L("控制器", "Controllers") : L("入口", "Entry"), true,
                (isWeb ? http : entries).Select(e => w.ById.GetValueOrDefault(e.SymbolId)).Where(s => s is not null).DistinctBy(s => s!.Id).Select(s => w.Dto(s!)).ToList()));
        if (present.Contains(StageAspect)) stages.Add(new FrameworkStageDto(StageAspect, L("切面", "Aspects"), aspectsOrderKnown, aspectSymbols.DistinctBy(s => s.Id).Select(w.Dto).ToList()));
        if (present.Contains(StageTail))
        {
            void TailSyms(FlowNodeDto n)
            {
                if (n.Actor == StageTail && n.SymbolId is { } id && w.ById.TryGetValue(id, out var s) && s.Kind != SymbolKind.External) tailSymbols.Add(s);
                foreach (var c in n.Children) TailSyms(c);
            }
            foreach (var k in rootKids) TailSyms(k);
            stages.Add(new FrameworkStageDto(StageTail, L("收尾", "Teardown"), filterOrderKnown && interceptorsOrderKnown && aspectsOrderKnown, tailSymbols.DistinctBy(s => s.Id).Select(w.Dto).ToList()));
        }

        // ---- Notes ----
        if (!isWeb) notes.Add(L("这个容器不是 Web 服务（没有 HTTP 入口、没有 spring-boot-starter-web / webflux 依赖，也没认出过滤器 / 拦截器）：入口是常驻服务 / 任务 / 消息消费", "This container is not a web service (no HTTP entry points, no spring-boot-starter-web / webflux dependency, and no recognized filters / interceptors): entry points are hosted services / jobs / message consumers"));
        else
        {
            notes.Add(reactive ? "Spring WebFlux" + (gateway ? " + Spring Cloud Gateway" : "") + L("：WebFilter → DispatcherHandler → 处理函数（没有 HandlerInterceptor）", ": WebFilter → DispatcherHandler → handler function (no HandlerInterceptor)")
                               : L("Spring MVC：过滤器 → DispatcherServlet → 拦截器 → 控制器（切面包在 Bean 方法外面）", "Spring MVC: filters → DispatcherServlet → interceptors → controllers (aspects wrap bean methods)"));
            notes.AddRange(filterNotes);
            if (!reactive) notes.AddRange(interceptorNotes);
            notes.AddRange(gatewayNotes);
            var serverDep = deps.FirstOrDefault(d => d.Contains("config-server", StringComparison.Ordinal) || d.Contains("eureka-server", StringComparison.Ordinal) || d.Contains("admin-starter-server", StringComparison.Ordinal));
            if (http.Count == 0)
                notes.Add(L("没有发现 HTTP 入口（@RestController / @Controller / 路由函数）", "No HTTP entry points found (@RestController / @Controller / router functions)") + (gateway ? L("：网关只做路由转发", ": the gateway only routes requests") : serverDep is not null ? L($"：接口由框架提供（{serverDep}）", $": endpoints are provided by the framework ({serverDep})") : ""));
            var nonHttp = entries.Count - http.Count;
            if (nonHttp > 0) notes.Add(L($"另有 {nonHttp} 个非 HTTP 入口（main / @Scheduled / 消息监听），没画在这条请求链上", $"Plus {Plural(nonHttp, "non-HTTP entry point", "non-HTTP entry points")} (main / @Scheduled / message listeners), not drawn in this request pipeline"));
        }
        notes.AddRange(aspectNotes);
        if (transactional is null) notes.Add(L("没有发现 @Transactional", "No @Transactional found"));
        if (exceptionNodes.Count == 0 && isWeb && !reactive) notes.Add(L("没有发现异常处理（@ControllerAdvice / @RestControllerAdvice 里的 @ExceptionHandler、HandlerExceptionResolver）：出错走 Spring Boot 默认的 /error", "No exception handling found (@ExceptionHandler in @ControllerAdvice / @RestControllerAdvice, HandlerExceptionResolver): errors go to Spring Boot's default /error"));
        if (!present.Contains(StageTail)) notes.Add(L("没有发现收尾（过滤器 / 拦截器 / 切面的后置、@Transactional、异常处理）", "No teardown found (after-steps of filters / interceptors / aspects, @Transactional, exception handling)"));
        if (rootKids.Count == 0) return Empty(container, notes);

        var rootActor = rootKids[0].Actor;
        var rootLabel = isWeb ? L("HTTP 请求", "HTTP request") : L("入口", "Entry");
        var root = Node(null, rootLabel, "", "entry", rootActor, null, rootKids, null, null);
        return Assemble(container, rootLabel, root, stages, [], notes, rootLabel);
    }

    // ================= 过滤器 =================

    private sealed record Registration(string TypeName, string How, long? Order, string? OrderHow, string Path, int Line, List<string> Urls, bool Disabled, bool Anonymous, MapSymbol? Bean);

    private static (List<Unit> Units, List<string> Notes, bool OrderKnown) Filters(World w, bool reactive, List<MapSymbol> filterClasses, List<string> deps)
    {
        var notes = new List<string>();
        var chainTypes = reactive ? ReactiveChainTypes : ServletChainTypes;
        string[] chainMembers = reactive ? ["filter"] : ["doFilter"];
        var filterNames = filterClasses.Select(World.ShortName).ToHashSet(StringComparer.Ordinal);
        var regs = new List<Registration>();
        var lambdaBeans = new List<(MapSymbol Method, long? Order, string? OrderHow)>();

        foreach (var m in w.Declared.Where(s => s.Kind is SymbolKind.Method or SymbolKind.Function).OrderBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.StartLine))
        {
            if (w.Span(m) is not { } span || w.Text(m.Path) is not { } t) continue;
            var body = t.Code[span.Start..span.End];
            var where = $"{World.FileName(m.Path)}:{m.StartLine}";
            // FilterRegistrationBean
            if (body.Contains("FilterRegistrationBean", StringComparison.Ordinal))
            {
                var candidates = new List<(string Name, bool Anonymous)>();
                foreach (Match g in Regex.Matches(body, @"FilterRegistrationBean\s*<\s*([\w.]+)\s*>")) candidates.Add((MapBuilder.LastSegment(g.Groups[1].Value), false));
                foreach (Match s in Regex.Matches(body, @"\.\s*setFilter\s*\("))
                {
                    var open = span.Start + s.Index + s.Length - 1;
                    var close = MatchBracket(t.Code, open, '(', ')');
                    if (close > open && w.ExprType(t.Code[(open + 1)..close], m.Path) is { Type: { } st } r) candidates.Add((st, r.Anonymous));
                }
                foreach (Match n in Regex.Matches(body, @"\bnew\s+([\w.]+)\s*(?:<[^()]*>)?\s*\("))
                {
                    var name = MapBuilder.LastSegment(n.Groups[1].Value);
                    if (name != "FilterRegistrationBean") candidates.Add((name, false));
                    else
                    {
                        // new FilterRegistrationBean<>(jwtFilter()) / (new XFilter())
                        var open = span.Start + n.Index + n.Length - 1;
                        var close = MatchBracket(t.Code, open, '(', ')');
                        var args = close > open ? SplitTopLevel(t.Code[(open + 1)..close]) : [];
                        if (args.Count > 0 && args[0].Trim().Length > 0 && w.ExprType(args[0], m.Path) is { Type: { } ct } r) candidates.Add((ct, r.Anonymous));
                    }
                }
                var pick = candidates.FirstOrDefault(c => filterNames.Contains(c.Name));
                if (pick.Name is null) pick = candidates.FirstOrDefault(c => c.Name.EndsWith("Filter", StringComparison.Ordinal));
                if (pick.Name is not null)
                {
                    long? order = null;
                    string? orderHow = null;
                    var so = Regex.Match(body, @"\.\s*setOrder\s*\(");
                    if (so.Success)
                    {
                        var open = span.Start + so.Index + so.Length - 1;
                        var close = MatchBracket(t.Code, open, '(', ')');
                        if (close > open && w.EvalOrder(t.Code[(open + 1)..close], m.Path) is { } v) { order = v; orderHow = $"FilterRegistrationBean.setOrder({OrderText(v)})"; }
                    }
                    var urls = new List<string>();
                    foreach (Match u in Regex.Matches(body, @"\.\s*(?:addUrlPatterns|setUrlPatterns)\s*\("))
                        urls.AddRange(World.Literals(World.RawArgs(t.Code, t.Raw, span.Start + u.Index + 1)));
                    var disabled = Regex.IsMatch(body, @"\.\s*setEnabled\s*\(\s*false\s*\)");
                    regs.Add(new Registration(pick.Name, L($"FilterRegistrationBean（{where}）", $"FilterRegistrationBean ({where})"), order, orderHow, m.Path, m.StartLine, urls, disabled, pick.Anonymous, m));
                    continue;
                }
            }
            // @Bean 方法返回过滤器
            if (w.Annotation(m, "Bean") is null || w.ReturnType(m) is not { } rt) continue;
            var (beanOrder, beanHow) = w.Annotation(m, "Order") is { } oa && w.EvalOrder(JavaSource.Keyword(oa.Args, "value") ?? oa.Args, m.Path) is { } ov
                ? ((long?)ov, L($"@Bean 上的 @Order({OrderText(ov)})", $"@Order({OrderText(ov)}) on the @Bean")) : ((long?)null, (string?)null);
            if (filterNames.Contains(rt)) regs.Add(new Registration(rt, L($"@Bean {World.ShortName(m)}()（{where}）", $"@Bean {World.ShortName(m)}() ({where})"), beanOrder, beanHow, m.Path, m.StartLine, [], false, false, m));
            else if (rt is "Filter" or "WebFilter") lambdaBeans.Add((m, beanOrder, beanHow));
            else if (rt.EndsWith("Filter", StringComparison.Ordinal) && rt != "GlobalFilter" && !w.TypeByName.ContainsKey(rt))
                regs.Add(new Registration(rt, L($"@Bean {World.ShortName(m)}()（{where}）", $"@Bean {World.ShortName(m)}() ({where})"), beanOrder, beanHow, m.Path, m.StartLine, [], false, false, m));
        }

        // Spring Security：链在哪、addFilterBefore / After / At 加了谁
        string? securityWhere = null;
        var securityDsl = new List<string>();
        var securityAdds = new List<(string? Type, bool Anonymous, string Expr, string How, string Path, int Line, int Pos)>();
        foreach (var path in w.Files)
        {
            if (w.Text(path) is not { } t) continue;
            var code = t.Code;
            foreach (Match m in SecurityChainBean.Matches(code))
                if (reactive == (m.Groups[1].Value == "SecurityWebFilterChain"))
                    securityWhere ??= L($"{m.Groups[1].Value} {m.Groups[2].Value}()（{World.FileName(path)}:{World.LineAt(code, m.Index)}）", $"{m.Groups[1].Value} {m.Groups[2].Value}() ({World.FileName(path)}:{World.LineAt(code, m.Index)})");
            if (!reactive && SecurityAdapter.Match(code) is { Success: true } a) securityWhere ??= L($"WebSecurityConfigurerAdapter（{World.FileName(path)}:{World.LineAt(code, a.Index)}）", $"WebSecurityConfigurerAdapter ({World.FileName(path)}:{World.LineAt(code, a.Index)})");
            if (!code.Contains("HttpSecurity", StringComparison.Ordinal)) continue;
            // http.csrf().disable().cors().and()… / http.csrf(c -> c.disable())：记下配了哪些内置功能
            foreach (Match r in Regex.Matches(code, @"\b(?:Server)?HttpSecurity\s+(\w+)\s*[,)]"))
            {
                var calls = ScanReceiver(code, r.Groups[1].Value, 0, code.Length);
                for (var i = 0; i < calls.Count; i++)
                {
                    if (!SecurityDsl.Contains(calls[i].Name)) continue;
                    var off = i + 1 < calls.Count && calls[i + 1].Name == "disable" || Regex.IsMatch(calls[i].Args, @"\.\s*disable\s*\(|AbstractHttpConfigurer\s*::\s*disable");
                    var item = calls[i].Name + (off ? L("（关闭）", " (disabled)") : "");
                    if (!securityDsl.Contains(item)) securityDsl.Add(item);
                }
            }
            foreach (Match m in AddSecurityFilter.Matches(code))
            {
                var open = m.Index + m.Length - 1;
                var close = MatchBracket(code, open, '(', ')');
                if (close < 0) continue;
                var args = SplitTopLevel(code[(open + 1)..close]);
                if (args.Count == 0 || args[0].Trim().Length == 0) continue;
                var anchor = args.Count > 1
                    ? ClassLiteral.Match(args[1]) is { Success: true } cl ? MapBuilder.LastSegment(cl.Groups[1].Value) : MapBuilder.LastSegment(args[1].Trim())
                    : null;
                var (type, anonymous) = w.ExprType(args[0], path);
                securityAdds.Add((type, anonymous, args[0].Trim(), anchor is null ? m.Groups[1].Value : $"{m.Groups[1].Value}({anchor})", path, World.LineAt(code, m.Index), m.Index));
            }
        }
        var securityDep = deps.Any(d => d.Contains("starter-security", StringComparison.Ordinal) || d.Contains("starter-oauth2-resource-server", StringComparison.Ordinal));
        var hasSecurity = securityWhere is not null || securityDep || securityAdds.Count > 0;

        // ---- 单元 ----
        var units = new List<Unit>();
        var seq = 0;
        var byName = filterClasses.ToDictionary(World.ShortName, c => c, StringComparer.Ordinal);
        Unit RepoFilter(MapSymbol cls)
        {
            var main = reactive ? w.MethodIn(cls, "filter") : w.MethodIn(cls, "doFilterInternal", "doFilter", "attemptAuthentication");
            var u = new Unit
            {
                Label = main is null ? World.ShortName(cls) : World.ShortName(cls) + "." + World.ShortName(main), Owner = World.ShortName(cls),
                SymbolId = main?.Id ?? cls.Id, QualifiedName = (main ?? cls).QualifiedName, Path = main?.Path ?? cls.Path, Line = main?.StartLine ?? cls.StartLine,
                CallPath = main?.Path, Symbol = main ?? cls, Seq = seq++,
            };
            if (main is not null)
            {
                if (main.ParentId != cls.Id && w.Owner(main) is { } o) u.Notes.Add(L($"方法写在父类 {World.ShortName(o)}", $"method defined in base class {World.ShortName(o)}"));
                var split = w.SplitAtChain(main, chainTypes, chainMembers);
                u.Pre = split.Pre;
                u.Post = split.Post;
                if (split.Conditional) u.Notes.Add(L("有条件（有分支不往下传直接返回）", "conditional (some branches return without passing the request on)"));
                if (!split.HasChain) u.Notes.Add(reactive ? L("没找到 chain.filter(exchange) 调用", "no chain.filter(exchange) call found") : L("没找到 chain.doFilter(…) 调用（请求可能到此为止）", "no chain.doFilter(…) call found (the request may stop here)"));
            }
            return u;
        }

        // 安全链里的
        var inSecurity = new HashSet<string>(StringComparer.Ordinal);
        Unit? security = null;
        if (hasSecurity)
        {
            security = new Unit
            {
                Label = L("Spring Security 过滤器链", "Spring Security filter chain"), Owner = "Spring Security", Kind = "external-group", Order = SecurityOrder, OrderHow = L("Spring Security 默认 order -100", "Spring Security default order -100"), Seq = seq++,
            };
            security.Notes.Add(securityWhere ?? (securityDep ? L("依赖 spring-boot-starter-security，没找到 SecurityFilterChain 配置（用默认的那套）", "depends on spring-boot-starter-security; no SecurityFilterChain configuration found (uses the defaults)") : L("HttpSecurity 配置", "HttpSecurity configuration")));
            security.Notes.Add(reactive ? L("WebFilterChainProxy，order -100", "WebFilterChainProxy, order -100") : L("DelegatingFilterProxy → FilterChainProxy，order -100（认证、授权、CSRF 等内置过滤器在里面）", "DelegatingFilterProxy → FilterChainProxy, order -100 (contains built-in filters for authentication, authorization, CSRF, etc.)"));
            foreach (var add in securityAdds.OrderBy(a => a.Path, StringComparer.Ordinal).ThenBy(a => a.Pos))
            {
                var where = L($"{add.How}（{World.FileName(add.Path)}:{add.Line}）", $"{add.How} ({World.FileName(add.Path)}:{add.Line})");
                if (add.Type is { } tn && byName.TryGetValue(tn, out var cls) && !add.Anonymous)
                {
                    var u = RepoFilter(cls);
                    u.Notes.Insert(0, where);
                    u.Path = add.Path;
                    u.Line = add.Line;
                    // 过滤器同时是 Bean（@Component / @Bean）而没有 setEnabled(false)：Spring Boot 还会把它注册成普通过滤器
                    var bean = w.Annotation(cls, ComponentAnnotations) is { } comp ? "@" + comp.Name
                        : regs.FirstOrDefault(r => r.TypeName == tn && r.How.StartsWith("@Bean", StringComparison.Ordinal)) is { } br ? br.How : null;
                    if (bean is not null && !regs.Any(r => r.TypeName == tn && r.Disabled))
                        u.Notes.Add(L($"同时是 {bean}：Spring Boot 还会把它注册成普通 Servlet 过滤器", $"also a {bean}: Spring Boot also registers it as a regular servlet filter") + (w.Supers(cls).Contains("OncePerRequestFilter") ? L("（OncePerRequestFilter 会跳过第二次）", " (OncePerRequestFilter skips the second run)") : L("，会执行两次", ", so it runs twice")));
                    security.Inner.Add(u);
                    inSecurity.Add(tn);
                }
                else
                {
                    var label = add.Anonymous ? L($"匿名 {add.Type}", $"anonymous {add.Type}") : add.Type ?? add.Expr;
                    var u = new Unit { Label = label, Owner = label, Kind = "external-group", Path = add.Path, Line = add.Line, Seq = seq++ };
                    u.Notes.Add(where);
                    security.Inner.Add(u);
                }
            }
            if (securityDsl.Count > 0) security.Notes.Add(L("配置了 ", "configured: ") + string.Join(ListSep, securityDsl));
            if (security.Inner.Count > 1) security.Notes.Add(L("自研过滤器按 addFilterXxx 的书写顺序画（实际位置由锚点过滤器决定）", "custom filters drawn in addFilterXxx call order (the actual position depends on the anchor filter)"));
            units.Add(security);
        }

        foreach (var cls in filterClasses)
        {
            var name = World.ShortName(cls);
            var own = regs.Where(r => r.TypeName == name).ToList();
            var active = own.Where(r => !r.Disabled).ToList();
            var component = w.Annotation(cls, ComponentAnnotations);
            var webFilter = w.Annotation(cls, "WebFilter");
            if (own.Count > 0 && active.Count == 0)
            {
                notes.Add(L($"{name} 的 FilterRegistrationBean 写了 setEnabled(false)：不作为 Servlet 过滤器注册", $"{name}'s FilterRegistrationBean calls setEnabled(false): not registered as a servlet filter") + (inSecurity.Contains(name) ? L("（只在 Security 链里）", " (only in the Security chain)") : ""));
                continue;
            }
            // 在 Security 链里、另外只是个 Bean：只画在链里（显式的 FilterRegistrationBean 才另画一行）
            if (inSecurity.Contains(name) && !active.Any(r => r.How.StartsWith("FilterRegistrationBean", StringComparison.Ordinal))) continue;
            var u = RepoFilter(cls);
            var reg = active.FirstOrDefault(r => r.How.StartsWith("FilterRegistrationBean", StringComparison.Ordinal)) ?? active.FirstOrDefault();
            if (reg is not null)
            {
                u.Notes.Insert(0, reg.How);
                u.Path = reg.Path;
                u.Line = reg.Line;
                if (reg.Urls.Count > 0) u.Notes.Add(L("路径 ", "paths ") + string.Join(", ", reg.Urls));
            }
            else if (component is not null) u.Notes.Insert(0, L($"@{component.Name}（{World.FileName(cls.Path)}:{cls.StartLine}）", $"@{component.Name} ({World.FileName(cls.Path)}:{cls.StartLine})"));
            else if (webFilter is not null)
            {
                u.Notes.Insert(0, L($"@WebFilter（{World.FileName(cls.Path)}:{cls.StartLine}，要 @ServletComponentScan）", $"@WebFilter ({World.FileName(cls.Path)}:{cls.StartLine}, requires @ServletComponentScan)"));
                var urls = World.Literals(webFilter.Args ?? "");
                if (urls.Count > 0) u.Notes.Add(L("路径 ", "paths ") + string.Join(", ", urls));
            }
            else
            {
                u.Registered = false;
                u.Notes.Insert(0, L("没找到注册位置（@Component / @Bean / FilterRegistrationBean / addFilterBefore），可能没生效，顺序未知", "no registration found (@Component / @Bean / FilterRegistrationBean / addFilterBefore); may not be active, order unknown"));
            }
            var (typeOrder, typeHow) = w.TypeOrder(cls);
            (u.Order, u.OrderHow) = reg?.Order is not null ? (reg.Order, reg.OrderHow) : (typeOrder, typeHow);
            if (webFilter is not null && reg is null && component is null) (u.Order, u.OrderHow) = (null, L("@WebFilter 不认 @Order（Servlet 容器按扫描顺序）", "@WebFilter ignores @Order (the servlet container uses scan order)"));
            units.Add(u);
        }
        // 框架过滤器（@Bean / FilterRegistrationBean 里 new 的）
        foreach (var r in regs.Where(r => !byName.ContainsKey(r.TypeName) && !r.Disabled))
        {
            var u = new Unit { Label = r.Anonymous ? L($"匿名 {r.TypeName}", $"anonymous {r.TypeName}") : r.TypeName, Owner = r.TypeName, Kind = "external-group", Order = r.Order, OrderHow = r.OrderHow, Path = r.Path, Line = r.Line, Seq = seq++ };
            u.Notes.Add(L($"框架 / 第三方过滤器，{r.How}", $"framework / third-party filter, {r.How}"));
            if (r.Urls.Count > 0) u.Notes.Add(L("路径 ", "paths ") + string.Join(", ", r.Urls));
            units.Add(u);
        }
        // @Bean 返回 Filter / WebFilter 本身（lambda / 匿名类）：挂那个 @Bean 方法
        foreach (var (m, order, how) in lambdaBeans)
        {
            var split = w.SplitAtChain(m, chainTypes, chainMembers);
            var u = new Unit
            {
                Label = w.MemberLabel(m), Owner = World.ShortName(m) + "()", SymbolId = m.Id, QualifiedName = m.QualifiedName, Order = order, OrderHow = how,
                Path = m.Path, Line = m.StartLine, CallPath = m.Path, Pre = split.Pre, Post = split.Post, Symbol = m, Seq = seq++,
            };
            u.Notes.Add(L($"@Bean 返回的 {w.ReturnType(m)}（lambda / 匿名类，{World.FileName(m.Path)}:{m.StartLine}）", $"{w.ReturnType(m)} returned by a @Bean (lambda / anonymous class, {World.FileName(m.Path)}:{m.StartLine})"));
            units.Add(u);
        }

        units = units.OrderBy(u => u.Registered ? 0 : 1).ThenBy(u => u.Effective).ThenBy(u => u.Seq).ToList();
        foreach (var u in units.Where(u => u != security && u.OrderHow is not null)) u.Notes.Add(L("顺序 ", "order: ") + u.OrderHow);
        var registered = units.Where(u => u.Registered).ToList();
        var ties = registered.GroupBy(u => u.Effective).Where(g => g.Count() > 1).ToList();
        var orderKnown = registered.Count == units.Count && ties.Count == 0;

        if (units.Count == 0)
            notes.Insert(0, reactive ? L("没有发现自研 WebFilter（implements WebFilter）", "No custom WebFilter found (implements WebFilter)") : L("没有发现自研过滤器（implements Filter / extends OncePerRequestFilter、GenericFilterBean）", "No custom filters found (implements Filter / extends OncePerRequestFilter, GenericFilterBean)"));
        else
        {
            var basis = L("过滤器按 FilterRegistrationBean.setOrder / @Order / Ordered.getOrder 排（小的在外层先执行），没写顺序的按 LOWEST_PRECEDENCE 排在后面", "Filters are ordered by FilterRegistrationBean.setOrder / @Order / Ordered.getOrder (smaller values are outer and run first); filters without an order use LOWEST_PRECEDENCE and come last");
            if (ties.Count > 0) basis += L("；", "; ") + string.Join(ListSep, ties.Select(g => string.Join(L(" 与 ", " and "), g.Select(u => u.Owner)) + L($" 同为 {OrderText(g.Key)}", $" both at {OrderText(g.Key)}"))) + L("，它们之间的先后不确定", ": their relative order is undefined");
            if (registered.Count < units.Count) basis += L($"；{units.Count - registered.Count} 个过滤器没找到注册位置，排在最后", $"; {Plural(units.Count - registered.Count, "filter has", "filters have")} no registration and come last");
            notes.Insert(0, basis);
            if (security is not null) notes.Insert(1, L("Spring Security 过滤器链按 order -100 排在里面", "The Spring Security filter chain is placed at order -100") + (security.Inner.Count > 0 ? L($"，链里有 {security.Inner.Count} 个经 addFilterXxx 加进去的过滤器", $"; the chain contains {Plural(security.Inner.Count, "filter", "filters")} added via addFilterXxx") : ""));
        }
        notes.Add(reactive ? L("Spring Boot / WebFlux 自带的 WebFilter（观测、HiddenHttpMethodFilter…）没画", "WebFilters built into Spring Boot / WebFlux (observation, HiddenHttpMethodFilter…) are not drawn") : L("Spring Boot 自带的过滤器（字符编码、表单内容、RequestContextFilter、观测…）没画", "Filters built into Spring Boot (character encoding, form content, RequestContextFilter, observation…) are not drawn"));
        return (units, notes, orderKnown);
    }

    // ================= 拦截器 =================

    private static (List<Unit> Units, List<string> Notes, bool OrderKnown) Interceptors(World w, List<MapSymbol> classes)
    {
        var notes = new List<string>();
        var units = new List<Unit>();
        var registered = new HashSet<long>();
        var where = new List<string>();
        var anyOrder = false;
        var seq = 0;
        foreach (var m in w.Declared.Where(s => s.Kind == SymbolKind.Method && World.ShortName(s) == "addInterceptors").OrderBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.StartLine))
        {
            if (w.Span(m) is not { } span || w.Text(m.Path) is not { } t) continue;
            var receiver = w.Params(m).FirstOrDefault(p => p.Type == "InterceptorRegistry").Name ?? "registry";
            Unit? current = null;
            var count = 0;
            foreach (var call in ScanReceiver(t.Code, receiver, span.Start, span.End))
            {
                var line = World.LineAt(t.Code, call.Pos);
                if (call.Name is "addInterceptor" or "addWebRequestInterceptor")
                {
                    count++;
                    current = InterceptorUnit(w, call.Args, m.Path, line, call.Name, seq++);
                    if (current.Symbol is { } s && w.Owner(s) is { } owner) registered.Add(owner.Id);
                    units.Add(current);
                    continue;
                }
                if (current is null) continue;
                var raw = World.RawArgs(t.Code, t.Raw, call.Pos);
                switch (call.Name)
                {
                    case "addPathPatterns":
                        current.Notes.Add(L("路径 ", "paths ") + PatternText(raw));
                        break;
                    case "excludePathPatterns":
                        current.Notes.Add(L("排除 ", "excludes ") + PatternText(raw));
                        break;
                    case "order":
                        if (w.EvalOrder(call.Args, m.Path) is { } v) { current.Order = v; current.Notes.Add($"order({OrderText(v)})"); anyOrder = true; }
                        break;
                }
            }
            if (count > 0) where.Add(L($"{w.MemberLabel(m)}（{World.FileName(m.Path)}:{m.StartLine}）", $"{w.MemberLabel(m)} ({World.FileName(m.Path)}:{m.StartLine})"));
        }
        units = units.OrderBy(u => u.Order ?? 0).ThenBy(u => u.Seq).ToList();
        var known = true;
        foreach (var cls in classes.Where(c => !registered.Contains(c.Id)).OrderBy(c => c.QualifiedName, StringComparer.Ordinal))
        {
            var u = InterceptorUnit(w, null, cls.Path, cls.StartLine, null, seq++, cls);
            u.Registered = false;
            u.Notes.Insert(0, L("没找到 registry.addInterceptor 注册位置，顺序未知", "no registry.addInterceptor registration found, order unknown"));
            units.Add(u);
            known = false;
        }
        if (units.Count == 0) notes.Add(L("没有发现拦截器（WebMvcConfigurer.addInterceptors 里没有 registry.addInterceptor，也没有 HandlerInterceptor 实现）", "No interceptors found (no registry.addInterceptor in WebMvcConfigurer.addInterceptors and no HandlerInterceptor implementations)"));
        else
        {
            if (where.Count > 0) notes.Add(L($"拦截器按 {string.Join(ListSep, where)} 里 registry.addInterceptor 的书写顺序", $"Interceptors in the order of registry.addInterceptor calls in {string.Join(ListSep, where)}")
                                           + (anyOrder ? L("（写了 .order(n) 的先按 order 排）", " (those with .order(n) are sorted by order first)") : "")
                                           + L("：preHandle 顺着走，postHandle / afterCompletion 倒着回来", ": preHandle runs in order, postHandle / afterCompletion run in reverse"));
            var builtIn = units.Where(u => u.Kind == "external-group").Select(u => u.Label).ToList();
            if (builtIn.Count > 0) notes.Add(L($"{string.Join(ListSep, builtIn)} 是 Spring / 第三方的拦截器，只画一行", $"{string.Join(ListSep, builtIn)}: Spring / third-party interceptors, drawn as one row each"));
            if (!known) notes.Add(L("有 HandlerInterceptor 实现没找到注册位置，追加在最后", "Some HandlerInterceptor implementations have no registration and are appended at the end"));
        }
        return (units, notes, known);
    }

    /// <summary>addPathPatterns 的实参：字符串字面量，没有（常量 / 列表变量）就用原文。</summary>
    private static string PatternText(string raw) => World.Literals(raw) is { Count: > 0 } list ? string.Join(", ", list) : raw.Trim();

    private static Unit InterceptorUnit(World w, string? expr, string path, int line, string? how, int seq, MapSymbol? cls = null)
    {
        var anonymous = false;
        if (cls is null && expr is not null)
        {
            var (type, anon) = w.ExprType(expr, path);
            anonymous = anon;
            if (type is not null && !anon && w.TypeByName.TryGetValue(type, out var c) && c.Kind == SymbolKind.Class) cls = c;
            if (cls is null)
            {
                var label = anon ? L($"匿名 {type}", $"anonymous {type}") : type ?? expr.Trim();
                var ext = new Unit { Label = label, Owner = label, Kind = "external-group", Path = path, Line = line, Seq = seq };
                ext.Notes.Add(L($"registry.{how}（{World.FileName(path)}:{line}）", $"registry.{how} ({World.FileName(path)}:{line})") + (anon ? L("，匿名类", ", anonymous class") : L("，Spring / 第三方内置", ", built into Spring / third-party")));
                return ext;
            }
        }
        var name = World.ShortName(cls!);
        var pre = w.MethodIn(cls!, "preHandle");
        var u = new Unit
        {
            Label = pre is null ? name : name + ".preHandle", Owner = name, SymbolId = pre?.Id ?? cls!.Id, QualifiedName = (pre ?? cls!).QualifiedName,
            Path = path, Line = line, CallPath = pre?.Path, Symbol = pre ?? cls, Seq = seq,
        };
        if (how is not null) u.Notes.Add(L($"registry.{how}（{World.FileName(path)}:{line}）", $"registry.{how} ({World.FileName(path)}:{line})"));
        if (pre is not null)
        {
            u.Pre = w.Calls(pre).Where(JavaKeep).ToList();
            if (w.Span(pre) is { } sp && w.Text(pre.Path) is { } t && Regex.IsMatch(t.Code[sp.Start..sp.End], @"\breturn\s+false\b"))
                u.Notes.Add(L("preHandle 返回 false 时请求到此为止（不进控制器）", "when preHandle returns false the request stops here (never reaches the controller)"));
        }
        if (w.MethodIn(cls!, "postHandle") is { } post)
            u.Exits.Add(Node(post.Id, name + ".postHandle", post.QualifiedName, "call", StageTail, L($"{name} 后置：控制器返回后、渲染视图前（出错时不执行）", $"{name} after: after the controller returns, before the view renders (skipped on error)"),
                w.CallNodes(w.Calls(post), post.Path, StageTail), post.Path, post.StartLine));
        if (w.MethodIn(cls!, "afterCompletion") is { } after)
            u.After.Add(Node(after.Id, name + ".afterCompletion", after.QualifiedName, "call", StageTail, L($"{name} 收尾：请求完成后（无论成败，视图渲染之后）", $"{name} completion: after the request completes (success or failure, after view rendering)"),
                w.CallNodes(w.Calls(after), after.Path, StageTail), after.Path, after.StartLine));
        return u;
    }

    // ================= 网关 =================

    private static (Unit Unit, List<string> Notes) Gateway(World w, List<MapSymbol> globals, List<MapSymbol> factories, string containerPath)
    {
        var notes = new List<string>();
        var (defaults, routes) = GatewayConfig(w.Src.ReadFile, containerPath);
        var group = new Unit { Label = L("网关过滤器链（Spring Cloud Gateway）", "Gateway filter chain (Spring Cloud Gateway)"), Owner = "Gateway", Kind = "external-group" };
        group.Notes.Add(L("路由转发的请求：RoutePredicateHandlerMapping 匹配路由 → FilteringWebHandler 按 order 跑 GlobalFilter 与路由过滤器", "Routed requests: RoutePredicateHandlerMapping matches the route → FilteringWebHandler runs GlobalFilters and route filters by order"));
        var seq = 0;
        var items = new List<Unit>();
        foreach (var g in globals)
        {
            var main = w.MethodIn(g, "filter");
            var (order, how) = w.TypeOrder(g);
            var u = new Unit
            {
                Label = main is null ? World.ShortName(g) : World.ShortName(g) + ".filter", Owner = World.ShortName(g), SymbolId = main?.Id ?? g.Id,
                QualifiedName = (main ?? g).QualifiedName, Path = main?.Path ?? g.Path, Line = main?.StartLine ?? g.StartLine, CallPath = main?.Path,
                Symbol = main ?? g, Order = order, OrderHow = how, Seq = seq++,
            };
            u.Notes.Add(L("GlobalFilter（每条路由都过）", "GlobalFilter (applies to every route)") + (how is null ? L("；没写 getOrder / @Order，按 LOWEST_PRECEDENCE", "; no getOrder / @Order, uses LOWEST_PRECEDENCE") : L("；顺序 ", "; order: ") + how));
            if (main is not null)
            {
                var split = w.SplitAtChain(main, ReactiveChainTypes, ["filter"]);
                (u.Pre, u.Post) = (split.Pre, split.Post);
                if (split.Conditional) u.Notes.Add(L("有条件（有分支不往下传直接返回）", "conditional (some branches return without passing the request on)"));
                if (!split.HasChain) u.Notes.Add(L("没找到 chain.filter(exchange) 调用", "no chain.filter(exchange) call found"));
            }
            items.Add(u);
        }
        var factoryByName = factories.ToDictionary(f => Regex.Replace(World.ShortName(f), "(Gateway)?FilterFactory$", ""), f => f, StringComparer.Ordinal);
        for (var i = 0; i < defaults.Count; i++)
        {
            var d = defaults[i];
            var line = L($"default-filters 第 {i + 1} 个（{World.FileName(d.File)}:{d.Line}）", $"default-filters #{i + 1} ({World.FileName(d.File)}:{d.Line})") + (d.Args.Length > 0 ? L($"，参数 {d.Args}", $", args {d.Args}") : "");
            if (factoryByName.TryGetValue(d.Name, out var f) && w.MethodIn(f, "apply") is { } apply)
            {
                var split = w.SplitAtChain(apply, ReactiveChainTypes, ["filter"]);
                var u = new Unit
                {
                    Label = World.ShortName(f) + ".apply", Owner = World.ShortName(f), SymbolId = apply.Id, QualifiedName = apply.QualifiedName, Path = d.File, Line = d.Line,
                    CallPath = apply.Path, Pre = split.Pre, Post = split.Post, Symbol = apply, Order = i + 1, Seq = seq++,
                };
                u.Notes.Add(line + L("；自研过滤器工厂", "; custom filter factory"));
                items.Add(u);
            }
            else
            {
                var u = new Unit { Label = d.Name, Owner = d.Name, Kind = "external-group", Path = d.File, Line = d.Line, Order = i + 1, Seq = seq++ };
                u.Notes.Add(line + L("；Gateway 内置过滤器工厂", "; built-in Gateway filter factory"));
                items.Add(u);
            }
        }
        group.Inner.AddRange(items.OrderBy(u => u.Effective).ThenBy(u => u.Seq));
        var routeFilters = routes.Where(r => r.Filters.Count > 0).ToList();
        if (routeFilters.Count > 0)
        {
            var u = new Unit { Label = L("各路由自己的过滤器", "Per-route filters"), Owner = "routes", Kind = "external-group", Path = routeFilters[0].File, Line = routeFilters[0].Line, Seq = seq++ };
            u.Notes.Add(string.Join(L("；", "; "), routeFilters.Take(8).Select(r => $"{r.Id}: {string.Join(", ", r.Filters.Select(f => f.Name))}")) + (routeFilters.Count > 8 ? L($"；还有 {routeFilters.Count - 8} 条", $"; {routeFilters.Count - 8} more") : ""));
            group.Inner.Add(u);
        }
        var forward = new Unit { Label = L("转发到下游服务", "Forward to downstream service"), Owner = "forward", Kind = "external-group", Seq = seq++ };
        forward.Notes.Add(L("NettyRoutingFilter（lb:// 先经 ReactiveLoadBalancerClientFilter 选实例），Gateway 内置，order 最后", "NettyRoutingFilter (lb:// first picks an instance via ReactiveLoadBalancerClientFilter), built into Gateway, ordered last"));
        group.Inner.Add(forward);

        notes.Add(L($"网关：{globals.Count} 个 GlobalFilter，{defaults.Count} 个 default-filters，{routes.Count} 条路由（配置文件里的）；GlobalFilter 按 getOrder / @Order、default-filters 按配置顺序编号 1、2… 混排", $"Gateway: {Plural(globals.Count, "GlobalFilter", "GlobalFilters")}, {defaults.Count} default-filters, {Plural(routes.Count, "route", "routes")} (from configuration files); GlobalFilters by getOrder / @Order and default-filters numbered 1, 2… in configuration order are interleaved"));
        if (routes.Count > 0)
            notes.Add(L("路由：", "Routes: ") + string.Join(ListSep, routes.Take(8).Select(r => r.Id + (r.Path is null ? "" : L($"（{r.Path}）", $" ({r.Path})")) + (r.Uri is null ? "" : $" → {r.Uri}"))) + (routes.Count > 8 ? L($" 等 {routes.Count} 条", $" ({routes.Count} in total)") : ""));
        if (routes.Count == 0 && w.Files.Any(p => w.Text(p)?.Code.Contains("RouteLocatorBuilder", StringComparison.Ordinal) == true))
            notes.Add(L("路由用 RouteLocatorBuilder 在代码里配置（没展开）", "Routes are configured in code with RouteLocatorBuilder (not expanded)"));
        return (group, notes);
    }

    // ================= 切面 =================

    private static (List<FlowNodeDto> Enter, List<FlowNodeDto> Exit, List<MapSymbol> Symbols, List<string> Notes, bool OrderKnown) Aspects(World w)
    {
        var notes = new List<string>();
        var enter = new List<FlowNodeDto>();
        var exit = new List<FlowNodeDto>();
        var symbols = new List<MapSymbol>();
        var aspects = w.Types.Where(t => t.Kind == SymbolKind.Class && w.Annotation(t, "Aspect") is not null).ToList();

        // 命名切点：@Pointcut("…") void serviceLayer()
        var pointcuts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in w.Declared.Where(s => s.Kind == SymbolKind.Method))
            if (w.Annotation(m, "Pointcut") is { } pc && PointcutText(w, m, pc) is { } text) pointcuts.TryAdd(World.ShortName(m), text);
        string Expand(string expr) => Regex.Replace(expr, @"(?<![\w.@])(?:[\w]+\.)*(\w+)\s*\(\s*\)", m => pointcuts.TryGetValue(m.Groups[1].Value, out var v) ? $"{m.Value} = {v}" : m.Value);

        var ordered = aspects.Select((a, i) => (Aspect: a, Order: w.TypeOrder(a), Seq: i)).OrderBy(x => x.Order.Order ?? Lowest).ThenBy(x => x.Seq).ToList();
        var exitsByAspect = new List<List<FlowNodeDto>>();
        foreach (var (aspect, (order, how), _) in ordered)
        {
            var name = World.ShortName(aspect);
            var advices = w.Methods(aspect).Select(m => (Method: m, Ann: w.Annotation(m, AdviceAnnotations))).Where(x => x.Ann is not null).ToList();
            var aspectExit = new List<FlowNodeDto>();
            var orderNote = how is null ? L("没写 @Order，按 LOWEST_PRECEDENCE", "no @Order, uses LOWEST_PRECEDENCE") : L("顺序 ", "order: ") + how;
            var around = new List<(MapSymbol, FrameworkCall[])>();
            foreach (var kind in new[] { "Around", "Before" })
                foreach (var (m, ann) in advices.Where(x => x.Ann!.Name == kind))
                {
                    symbols.Add(m);
                    var pc = PointcutText(w, m, ann!) is { } p ? L("；切点 ", "; pointcut ") + Expand(p) : "";
                    if (kind == "Around")
                    {
                        var split = w.SplitAtChain(m, ["ProceedingJoinPoint"], ["proceed"]);
                        var note = L($"@Around{pc}；{orderNote}", $"@Around{pc}; {orderNote}") + (split.Conditional ? L("；有条件（有分支不调 proceed）", "; conditional (some branches don't call proceed)") : "")
                                   + (split.HasChain ? "" : L("；没找到 proceed()：目标方法不会执行", "; no proceed() found: the target method never runs"));
                        enter.Add(Node(m.Id, w.MemberLabel(m), m.QualifiedName, "call", StageAspect, note, w.CallNodes(split.Pre, m.Path, StageAspect), m.Path, m.StartLine));
                        around.Add((m, split.Post.ToArray()));
                    }
                    else enter.Add(Node(m.Id, w.MemberLabel(m), m.QualifiedName, "call", StageAspect, L($"@Before{pc}；{orderNote}", $"@Before{pc}; {orderNote}"), w.CallNodes(w.Calls(m), m.Path, StageAspect), m.Path, m.StartLine));
                }
            // 出来：AfterReturning / AfterThrowing → After → Around 后置
            foreach (var (m, ann) in advices.Where(x => x.Ann!.Name is "AfterReturning" or "AfterThrowing").Concat(advices.Where(x => x.Ann!.Name == "After")))
            {
                symbols.Add(m);
                var pc = PointcutText(w, m, ann!) is { } p ? L("；切点 ", "; pointcut ") + Expand(p) : "";
                var when = ann!.Name switch { "AfterReturning" => L("正常返回后", "after a normal return"), "AfterThrowing" => L("出错时", "on error"), _ => L("无论成败", "always") };
                aspectExit.Add(Node(m.Id, w.MemberLabel(m), m.QualifiedName, "call", StageTail, L($"{name} @{ann.Name}（{when}）{pc}", $"{name} @{ann.Name} ({when}){pc}"), w.CallNodes(w.Calls(m), m.Path, StageTail), m.Path, m.StartLine));
            }
            foreach (var (m, post) in around)
                aspectExit.AddRange(w.CallNodes(post, m.Path, StageTail, L($"{name} 后置（proceed 之后）", $"{name} after (after proceed)")));
            exitsByAspect.Add(aspectExit);
        }
        for (var i = exitsByAspect.Count - 1; i >= 0; i--) exit.AddRange(exitsByAspect[i]);

        // 框架切面（@Bean TimedAspect …）、缓存代理
        var annotated = new Dictionary<string, List<MapSymbol>>(StringComparer.Ordinal);
        foreach (var s in w.Declared)
            foreach (var a in w.Annotations(s))
                if (a.Name is "Timed" or "Counted" or "Cacheable" or "CachePut" or "CacheEvict" or "Caching")
                {
                    if (!annotated.TryGetValue(a.Name, out var list)) annotated[a.Name] = list = [];
                    list.Add(s);
                }
        string Examples(IEnumerable<MapSymbol> list) => string.Join(ListSep, list.Select(s => OutlineBuilder.IsType(s.Kind) ? World.ShortName(s) : w.MemberLabel(s)).Distinct().Take(3));
        var framework = new List<string>();
        foreach (var m in w.Declared.Where(s => s.Kind == SymbolKind.Method && w.Annotation(s, "Bean") is not null).OrderBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.StartLine))
        {
            if (w.ReturnType(m) is not { } rt || !rt.EndsWith("Aspect", StringComparison.Ordinal) || w.TypeByName.ContainsKey(rt)) continue;
            var marker = rt switch { "TimedAspect" => "Timed", "CountedAspect" => "Counted", _ => null };
            List<MapSymbol> used = marker is null ? new() : annotated.GetValueOrDefault(marker) ?? new();
            var what = marker is null ? L("框架提供的切面", "framework-provided aspect")
                : L($"Micrometer：对 @{marker} 标注的类 / 方法{(marker == "Timed" ? "计时" : "计数")}（{used.Count} 处{(used.Count > 0 ? "，如 " + Examples(used) : "")}）",
                    $"Micrometer: {(marker == "Timed" ? "times" : "counts")} classes / methods annotated with @{marker} ({used.Count} uses{(used.Count > 0 ? ", e.g. " + Examples(used) : "")})");
            enter.Add(Node(null, rt, "", "external-group", StageAspect, L($"{what}；@Bean {World.ShortName(m)}()（{World.FileName(m.Path)}:{m.StartLine}）", $"{what}; @Bean {World.ShortName(m)}() ({World.FileName(m.Path)}:{m.StartLine})"), [], m.Path, m.StartLine));
            framework.Add(rt);
        }
        var cached = CacheAnnotations.SelectMany(a => annotated.GetValueOrDefault(a) ?? []).DistinctBy(s => s.Id).ToList();
        if (cached.Count > 0)
        {
            var kinds = CacheAnnotations.Where(annotated.ContainsKey).Select(a => "@" + a).ToList();
            enter.Add(Node(null, L("缓存代理（Spring 内置）", "Cache proxy (built into Spring)"), "", "external-group", StageAspect,
                L($"{string.Join(" / ", kinds)} {cached.Count} 处，如 {Examples(cached)}：@Cacheable 命中缓存时不调方法，@CacheEvict 在方法返回后清缓存", $"{string.Join(" / ", kinds)}: {cached.Count} uses, e.g. {Examples(cached)}: @Cacheable skips the method on a cache hit, @CacheEvict clears the cache after the method returns"), [], cached[0].Path, cached[0].StartLine));
            framework.Add(L("缓存代理", "cache proxy"));
        }

        var ties = ordered.GroupBy(x => x.Order.Order ?? Lowest).Where(g => g.Count() > 1).ToList();
        var known = ties.Count == 0;
        if (aspects.Count == 0) notes.Add(L("没有发现 @Aspect 切面", "No @Aspect aspects found") + (framework.Count > 0 ? L($"（有框架切面 / 代理：{string.Join(ListSep, framework)}）", $" (framework aspects / proxies present: {string.Join(ListSep, framework)})") : ""));
        else
        {
            notes.Add(L($"切面：{aspects.Count} 个 @Aspect 类，按 @Order / getOrder 排（小的在外层），画在控制器下面——实际包在切点匹配的 Bean 方法外面（Note 里是切点）",
                        $"Aspects: {Plural(aspects.Count, "@Aspect class", "@Aspect classes")}, ordered by @Order / getOrder (smaller is outer), drawn under the controllers; they actually wrap the bean methods matched by their pointcuts (shown in the note)")
                      + (known ? "" : L("；", "; ") + string.Join(ListSep, ties.Select(g => string.Join(L(" 与 ", " and "), g.Select(x => World.ShortName(x.Aspect))) + L(" 没有区分的 @Order", " have no distinguishing @Order"))) + L("，先后不确定", ", so their order is undefined")));
            if (framework.Count > 0) notes.Add(L($"另有框架切面 / 代理：{string.Join(ListSep, framework)}", $"Also framework aspects / proxies: {string.Join(ListSep, framework)}"));
        }
        return (enter, exit, symbols, notes, known);
    }

    /// <summary>通知 / @Pointcut 注解里的切点表达式（value 或 pointcut，按同文件 String 常量展开）；没写为 null。</summary>
    private static string? PointcutText(World w, MapSymbol m, JavaSource.Annotation a)
    {
        var consts = w.Text(m.Path) is { } t ? JavaSource.StringConstants(t.Lines) : new Dictionary<string, string>();
        var v = JavaSource.Values(a.Args, k => consts.GetValueOrDefault(k), "pointcut", "value").FirstOrDefault();
        var text = v.Value ?? v.Raw;
        return string.IsNullOrWhiteSpace(text) ? null : Regex.Replace(text.Trim(), @"\s+", " ");
    }

    // ================= 事务、异常处理 =================

    private static FlowNodeDto? Transactional(World w)
    {
        var types = new List<MapSymbol>();
        var methods = new List<MapSymbol>();
        var readOnly = 0;
        foreach (var s in w.Declared)
            if (w.Annotation(s, "Transactional") is { } a)
            {
                (OutlineBuilder.IsType(s.Kind) ? types : methods).Add(s);
                if (Regex.IsMatch(a.Args ?? "", @"readOnly\s*=\s*true")) readOnly++;
            }
        if (types.Count + methods.Count == 0) return null;
        var examples = types.Select(World.ShortName).Concat(methods.Select(w.MemberLabel)).Distinct().Take(3);
        var first = types.Concat(methods).OrderBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.StartLine).First();
        var note = L($"@Transactional {types.Count + methods.Count} 处（类 {types.Count}、方法 {methods.Count}，如 {string.Join(ListSep, examples)}）：事务代理在方法正常返回后提交，抛 RuntimeException 回滚",
                     $"@Transactional: {types.Count + methods.Count} uses ({types.Count} on classes, {methods.Count} on methods, e.g. {string.Join(ListSep, examples)}): the transaction proxy commits after a normal return and rolls back on RuntimeException")
                   + (readOnly > 0 ? L($"；其中 readOnly {readOnly} 处", $"; {readOnly} of them readOnly") : "");
        return Node(null, L("@Transactional 提交 / 回滚", "@Transactional commit / rollback"), "", "external-group", StageTail, note, [], first.Path, first.StartLine);
    }

    private static (List<FlowNodeDto> Nodes, List<MapSymbol> Symbols) ExceptionHandlers(World w, List<MapSymbol> resolvers)
    {
        var local = new List<FlowNodeDto>();
        var advice = new List<(long Order, int Seq, FlowNodeDto Node)>();
        var symbols = new List<MapSymbol>();
        var seq = 0;
        foreach (var m in w.Declared.Where(s => s.Kind == SymbolKind.Method).OrderBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.StartLine))
        {
            if (w.Annotation(m, "ExceptionHandler") is not { } a || w.Owner(m) is not { } owner) continue;
            var types = ClassLiteral.Matches(a.Args ?? "").Select(x => MapBuilder.LastSegment(x.Groups[1].Value)).ToList();
            if (types.Count == 0 && w.Params(m).FirstOrDefault(p => p.Type.EndsWith("Exception", StringComparison.Ordinal) || p.Type is "Throwable" or "Error").Type is { } pt) types.Add(pt);
            var what = types.Count == 0 ? L("异常", "exception") : string.Join(ListSep, types);
            var adviceAnn = w.Annotation(owner, "ControllerAdvice", "RestControllerAdvice");
            symbols.Add(m);
            if (adviceAnn is null)
            {
                local.Add(Node(m.Id, w.MemberLabel(m), m.QualifiedName, "call", StageTail, L($"出错时：{what}（控制器内，只管 {World.ShortName(owner)}）", $"On error: {what} (inside the controller, only for {World.ShortName(owner)})"), w.CallNodes(w.Calls(m), m.Path, StageTail), m.Path, m.StartLine));
                continue;
            }
            var (order, _) = w.TypeOrder(owner);
            advice.Add((order ?? Lowest, seq++, Node(m.Id, w.MemberLabel(m), m.QualifiedName, "call", StageTail, L($"出错时：{what}（@{adviceAnn.Name}，{AdviceScope(adviceAnn)}）", $"On error: {what} (@{adviceAnn.Name}, {AdviceScope(adviceAnn)})"),
                w.CallNodes(w.Calls(m), m.Path, StageTail), m.Path, m.StartLine)));
        }
        // @ControllerAdvice 继承 ResponseEntityExceptionHandler：Spring MVC 标准异常由基类统一处理，重写的 handleXxx 挂在下面
        foreach (var t in w.Types.Where(t => t.Kind == SymbolKind.Class && w.Annotation(t, "ControllerAdvice", "RestControllerAdvice") is not null && w.Supers(t).Contains("ResponseEntityExceptionHandler")))
        {
            var ann = w.Annotation(t, "ControllerAdvice", "RestControllerAdvice")!;
            var overrides = w.Methods(t).Where(m => World.ShortName(m).StartsWith("handle", StringComparison.Ordinal) && w.Annotation(m, "ExceptionHandler") is null && w.Annotation(m, "Override") is not null).ToList();
            var kids = overrides.Select(m =>
            {
                symbols.Add(m);
                var ex = w.Params(m).FirstOrDefault(p => p.Type.EndsWith("Exception", StringComparison.Ordinal)).Type;
                return Node(m.Id, w.MemberLabel(m), m.QualifiedName, "call", StageTail, L($"出错时：{ex ?? "异常"}（重写基类方法）", $"On error: {ex ?? "exception"} (overrides a base class method)"), w.CallNodes(w.Calls(m), m.Path, StageTail), m.Path, m.StartLine);
            }).ToList();
            var (order, _) = w.TypeOrder(t);
            advice.Add((order ?? Lowest, seq++, Node(null, $"{World.ShortName(t)} : ResponseEntityExceptionHandler", "", "external-group", StageTail,
                L($"出错时：Spring MVC 标准异常（参数校验、类型转换、404 / 405 …）由基类 ResponseEntityExceptionHandler 统一处理（@{ann.Name}，{AdviceScope(ann)}）", $"On error: standard Spring MVC exceptions (validation, type conversion, 404 / 405 …) are handled by the base class ResponseEntityExceptionHandler (@{ann.Name}, {AdviceScope(ann)})")
                + (overrides.Count > 0 ? L($"；重写了 {string.Join(ListSep, overrides.Select(World.ShortName))}", $"; overrides {string.Join(ListSep, overrides.Select(World.ShortName))}") : ""), kids, t.Path, t.StartLine)));
        }
        var nodes = new List<FlowNodeDto>(local);
        nodes.AddRange(advice.OrderBy(x => x.Order).ThenBy(x => x.Seq).Select(x => x.Node));
        foreach (var r in resolvers)
        {
            var graphql = w.Supers(r).Contains("DataFetcherExceptionHandler");
            if ((graphql ? w.MethodIn(r, "handleException", "onException") : w.MethodIn(r, "doResolveException", "resolveException")) is not { } m) continue;
            symbols.Add(m);
            nodes.Add(Node(m.Id, World.ShortName(r) + "." + World.ShortName(m), m.QualifiedName, "call", StageTail,
                graphql ? L("出错时：GraphQL 数据获取的异常（DataFetcherExceptionHandler，只管 /graphql）", "On error: GraphQL data fetching exceptions (DataFetcherExceptionHandler, /graphql only)") : L("出错时（HandlerExceptionResolver）", "On error (HandlerExceptionResolver)"),
                w.CallNodes(w.Calls(m), m.Path, StageTail), m.Path, m.StartLine));
        }
        return (nodes, symbols);
    }

    private static string AdviceScope(JavaSource.Annotation a) => string.IsNullOrWhiteSpace(a.Args) ? L("全局", "global") : L("范围 ", "scope ") + Regex.Replace(a.Args.Trim(), @"\s+", " ");
}
