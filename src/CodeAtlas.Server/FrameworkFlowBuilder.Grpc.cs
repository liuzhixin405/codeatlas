using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

public static partial class FrameworkFlowBuilder
{
    /// <summary>
    /// 纯 gRPC 服务（没有 HTTP 框架）的框架流程，Go / Python / Java 共用（Node 的在 NodeFrameworkFlow.BuildGrpc，.NET 的 gRPC 走同一条 ASP.NET Core 管道、画在中间件下面）：
    /// 根"gRPC 调用" → 服务端拦截器（有就按注册顺序画，没有在说明里写）→ 占位"服务方法 ×N"（N = kind = grpc 的入口数）。
    /// 拦截器从源码文本认：Go 的 grpc.NewServer(grpc.UnaryInterceptor / ChainUnaryInterceptor / StreamInterceptor / StatsHandler …)，
    /// Python 的 grpc.server(…, interceptors=[…])，Java 的 ServerBuilder….intercept(x) / ServerInterceptors.intercept(svc, x…)。
    /// 没有 gRPC 入口返回 null（调用方照旧走自己的非 Web 画法）。
    /// </summary>
    internal static FrameworkFlowDto? GrpcOnly(FrameworkInput input, string language, IReadOnlyList<string> preface)
    {
        var container = input.Container;
        var entries = input.Entries.Where(e => e.ContainerKey == container.Key && !PipelineBuilder.IsTest(e.File, "")).ToList();
        var grpc = entries.Where(e => e.Kind == "grpc").ToList();
        if (grpc.Count == 0) return null;
        var ext = language switch { "go" => ".go", "python" => ".py", _ => ".java" };
        var files = input.Symbols.Where(s => s.Kind != SymbolKind.External && s.Path.EndsWith(ext, StringComparison.Ordinal) && container.Members.Any(m => Under(s.Path, m))
                                             && !PipelineBuilder.IsTest(s.Path, s.QualifiedName) && !s.Path.Contains("genproto", StringComparison.Ordinal) && !s.Path.EndsWith("_pb2_grpc.py", StringComparison.Ordinal))
            .Select(s => s.Path).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();

        var interceptors = new List<FlowNodeDto>();
        string? server = null;
        foreach (var path in files)
        {
            string? raw;
            try { raw = input.Sources.ReadFile(path); } catch { raw = null; }
            if (raw is null) continue;
            var text = language == "python" ? Regex.Replace(raw, @"#[^\n]*", m => new string(' ', m.Length)) : StripCommentsAndStrings(raw).Replace('\0', ' ');
            int LineOf(int pos) { var n = 1; for (var i = 0; i < pos && i < raw.Length; i++) if (raw[i] == '\n') n++; return n; }
            string Where(int pos) => $"{MapSymbol.FileName(path)}:{LineOf(pos)}";
            switch (language)
            {
                case "go":
                    foreach (Match m in Regex.Matches(text, @"\bgrpc\s*\.\s*NewServer\s*\("))
                    {
                        server ??= L($"grpc.NewServer，{Where(m.Index)}", $"grpc.NewServer, {Where(m.Index)}");
                        var close = MatchBracket(text, m.Index + m.Length - 1, '(', ')');
                        if (close < 0) continue;
                        var args = raw[(m.Index + m.Length)..close];
                        foreach (Match o in Regex.Matches(args, @"grpc\s*\.\s*(?<k>ChainUnaryInterceptor|UnaryInterceptor|ChainStreamInterceptor|StreamInterceptor|StatsHandler)\s*\("))
                        {
                            var oc = MatchBracket(args, o.Index + o.Length - 1, '(', ')');
                            var inner = oc < 0 ? "" : Regex.Replace(args[(o.Index + o.Length)..oc], @"\s+", "");
                            var kind = o.Groups["k"].Value;
                            var pos = m.Index + m.Length + o.Index;
                            var note = kind == "StatsHandler"
                                ? L($"StatsHandler（{Where(pos)}）：每个 RPC 开始 / 结束时回调（链路追踪、指标），包在服务方法外层", $"StatsHandler ({Where(pos)}): called at the start / end of every RPC (tracing, metrics), wraps the service methods")
                                : L($"{(kind.Contains("Stream", StringComparison.Ordinal) ? "流式" : "一元")}拦截器（grpc.{kind}，{Where(pos)}）：包在服务方法外层，按注册顺序由外到内", $"{(kind.Contains("Stream", StringComparison.Ordinal) ? "Stream" : "Unary")} interceptor (grpc.{kind}, {Where(pos)}): wraps the service methods, outermost first in registration order");
                            foreach (var part in SplitTopLevel(inner).Where(p => p.Length > 0))
                                interceptors.Add(Node(null, part, part, "external-group", StageInterceptor, note, [], path, LineOf(pos)));
                        }
                    }
                    break;
                case "python":
                    foreach (Match m in Regex.Matches(text, @"\bgrpc\s*\.\s*(?:aio\s*\.\s*)?server\s*\("))
                    {
                        server ??= L($"grpc.server，{Where(m.Index)}", $"grpc.server, {Where(m.Index)}");
                        var close = MatchBracket(text, m.Index + m.Length - 1, '(', ')');
                        if (close < 0) continue;
                        var args = raw[(m.Index + m.Length)..close];
                        if (Regex.Match(args, @"interceptors\s*=\s*(?<o>[\[(])") is { Success: true } im
                            && MatchBracket(args, im.Groups["o"].Index, im.Groups["o"].Value[0], im.Groups["o"].Value == "[" ? ']' : ')') is var ic and > 0)
                            foreach (var part in SplitTopLevel(args[(im.Groups["o"].Index + 1)..ic]).Select(p => Regex.Replace(p, @"\s+", "")).Where(p => p.Length > 0))
                                interceptors.Add(Node(null, part, part, "external-group", StageInterceptor, L($"服务端拦截器（interceptors=，{Where(m.Index)}）：按列表顺序由外到内", $"Server interceptor (interceptors=, {Where(m.Index)}): outermost first in list order"), [], path, LineOf(m.Index)));
                    }
                    break;
                default:
                    foreach (Match m in Regex.Matches(text, @"\b(?:Netty)?ServerBuilder\s*\.\s*forPort\s*\("))
                        server ??= L($"ServerBuilder.forPort，{Where(m.Index)}", $"ServerBuilder.forPort, {Where(m.Index)}");
                    foreach (Match m in Regex.Matches(text, @"\.\s*intercept\s*\(|\bServerInterceptors\s*\.\s*intercept(?:Forward)?\s*\("))
                    {
                        var close = MatchBracket(text, m.Index + m.Length - 1, '(', ')');
                        if (close < 0) continue;
                        var parts = SplitTopLevel(raw[(m.Index + m.Length)..close]).Select(p => Regex.Replace(p, @"\s+", "")).Where(p => p.Length > 0).ToList();
                        if (m.Value.Contains("ServerInterceptors", StringComparison.Ordinal) && parts.Count > 0) parts.RemoveAt(0); // 第一个参数是服务
                        foreach (var part in parts)
                            interceptors.Add(Node(null, part, part, "external-group", StageInterceptor, L($"服务端拦截器（{Where(m.Index)}）：包在服务方法外层", $"Server interceptor ({Where(m.Index)}): wraps the service methods"), [], path, LineOf(m.Index)));
                    }
                    break;
            }
        }

        var services = grpc.Where(e => e.Route is not null).GroupBy(e => e.Route!.Contains('/') ? e.Route![..e.Route!.IndexOf('/')] : "?", StringComparer.Ordinal)
            .Select(g => L($"{g.Key} {g.Count()} 个方法", $"{g.Key} {Plural(g.Count(), "method", "methods")}")).ToList();
        var notes = new List<string>(preface)
        {
            L($"认出 gRPC 服务（{server ?? "没找到创建服务器的地方"}）：{string.Join(ListSep, services)}；没有 HTTP 框架，框架流程画 gRPC 这条链", $"Recognized gRPC services ({server ?? "server creation not found"}): {string.Join(ListSep, services)}; no HTTP framework, so the framework flow shows the gRPC pipeline"),
            interceptors.Count == 0
                ? language switch
                {
                    "go" => L("没有注册服务端拦截器（grpc.NewServer 的参数里没有 UnaryInterceptor / StreamInterceptor / StatsHandler）：请求直接进服务方法", "No server interceptors registered (no UnaryInterceptor / StreamInterceptor / StatsHandler among the grpc.NewServer arguments): requests go straight to the service methods"),
                    "python" => L("没有注册服务端拦截器（grpc.server 没传 interceptors=）：请求直接进服务方法", "No server interceptors registered (grpc.server has no interceptors=): requests go straight to the service methods"),
                    _ => L("没有注册服务端拦截器（ServerBuilder 上没有 .intercept(…)，也没有 ServerInterceptors.intercept）：请求直接进服务方法", "No server interceptors registered (no .intercept(…) on ServerBuilder and no ServerInterceptors.intercept): requests go straight to the service methods"),
                }
                : L($"服务端拦截器 {interceptors.Count} 个，按注册顺序包在服务方法外层（先注册的在外层）", $"{Plural(interceptors.Count, "server interceptor", "server interceptors")}, wrapping the service methods in registration order (first registered is outermost)"),
        };
        var others = entries.Count - grpc.Count;
        if (others > 0) notes.Add(L($"另有 {others} 个非 gRPC 入口（", $"Plus {Plural(others, "non-gRPC entry point", "non-gRPC entry points")} (")
                                 + string.Join(ListSep, entries.Where(e => e.Kind != "grpc").GroupBy(e => e.Kind).Select(g => $"{g.Key} {g.Count()}")) + L("），没画在这条链上", "), not drawn in this pipeline"));

        var placeholder = Node(null, L("服务方法", "Service methods"), "", "controllers", StageController, L("具体做了什么看各方法的入口流程", "See each method's entry flow for what it does"), [], null, null, grpc.Count);
        var root = Node(null, L("gRPC 调用", "gRPC call"), "", "entry", interceptors.Count > 0 ? StageInterceptor : StageController, null, [.. interceptors, placeholder], null, null);
        var byId = input.Symbols.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        SymbolDto? Dto(long id) => input.Sources.Symbol?.Invoke(id)
            ?? (byId.TryGetValue(id, out var s) ? new SymbolDto(s.Id, DbNames.ToDb(s.Kind), s.Name, s.QualifiedName, null, s.Path, s.StartLine, 0, s.EndLine, 0, s.Visibility) : null);
        var stages = new List<FrameworkStageDto>();
        if (interceptors.Count > 0) stages.Add(new FrameworkStageDto(StageInterceptor, L("拦截器", "Interceptors"), true, []));
        stages.Add(new FrameworkStageDto(StageController, L("服务方法", "Service methods"), true, grpc.Select(e => Dto(e.SymbolId)).Where(d => d is not null).Select(d => d!).ToList()));
        return Assemble(container, L("gRPC 调用", "gRPC call"), root, stages, [], notes, interceptors.Count > 0 ? L("拦截器", "Interceptors") : L("服务方法", "Service methods"));
    }
}
