using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAtlas.Server.Mcp;

/// <summary>
/// 最小的 MCP server（stdio 传输）：一行一条 JSON-RPC 2.0 消息（UTF-8，换行分隔，消息里不含换行），
/// 只实现 initialize、notifications/initialized、ping、tools/list、tools/call。
/// stdout 只写协议消息；日志写 <c>log</c>（命令行接 stderr）。请求按到达顺序串行处理。
/// <para>
/// 为什么不用官方 C# SDK（NuGet ModelContextProtocol 1.4.1，有 net10.0 目标、发布方式也兼容）：
/// 这里只需要 tools 这一种能力、三个方法，手写不到两百行；SDK 会把 Microsoft.Extensions.AI.Abstractions / Hosting / Caching / Logging
/// 一串依赖带进自带运行时的发布目录，并让 CLI 换成 Host + DI 的启动方式。手写版本可以直接在测试里用 StringReader / StringWriter 跑完整握手。
/// </para>
/// </summary>
public sealed class McpServer(McpTools tools, TextWriter? log = null)
{
    /// <summary>支持的协议版本（新的在前）。客户端请求的版本在表里就照回，否则回第一个。</summary>
    public static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    public const string ServerName = "codeatlas";

    /// <summary>initialize 结果里的 instructions：告诉 agent 先用哪个工具。</summary>
    public const string Instructions =
        "CodeAtlas indexes this repository's symbols and call graph (calls, dispatches, implements, inherits — each edge with a confidence). " +
        "Call codeatlas_explore FIRST for almost any question about the code, or before editing a symbol: one call returns the line-numbered source of the relevant symbols, " +
        "the call paths between them and an impact summary — usually the only call you need. Use codeatlas_search / codeatlas_callers / codeatlas_callees / codeatlas_impact " +
        "only for follow-ups. Edges with confidence 0.3 are same-name guesses (one of several candidates). If the output warns that files are stale, read those files directly.";

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>读到输入结束（客户端关掉 stdin）或取消为止。</summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = Handle(line);
            if (response is null) continue;
            await output.WriteAsync(response.ToJsonString(Compact) + "\n").ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>处理一条消息；通知（没有 id）返回 null。</summary>
    public JsonObject? Handle(string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException ex) { return Error(null, -32700, "Parse error: " + ex.Message); }
        if (node is not JsonObject msg) return Error(null, -32600, "Invalid Request: expected a JSON object (batches are not supported)");

        var id = msg["id"]?.DeepClone();
        var isNotification = !msg.ContainsKey("id");
        if (msg["method"] is not JsonValue mv || !mv.TryGetValue<string>(out var method))
        {
            // 没有 method 的是客户端发来的响应（本 server 不发请求），忽略
            return isNotification || msg.ContainsKey("result") || msg.ContainsKey("error") ? null : Error(id, -32600, "Invalid Request: missing method");
        }
        if (isNotification)
        {
            log?.WriteLine($"[mcp] notification {method}");
            return null; // notifications/initialized、notifications/cancelled 等：不回
        }

        try
        {
            var p = msg["params"] as JsonObject;
            return method switch
            {
                "initialize" => Result(id, Initialize(p)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = McpTools.Definitions() }),
                "tools/call" => CallTool(id, p),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception ex)
        {
            log?.WriteLine($"[mcp] {method} failed: {ex}");
            return Error(id, -32603, "Internal error: " + ex.Message);
        }
    }

    private JsonObject Initialize(JsonObject? p)
    {
        var requested = p?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var version = requested is not null && ProtocolVersions.Contains(requested) ? requested : ProtocolVersions[0];
        var client = p?["clientInfo"]?["name"]?.ToString();
        log?.WriteLine($"[mcp] initialize from {client ?? "?"} (protocol {requested ?? "?"} → {version}); repo {tools.RepoRoot}, db {tools.DbPath}");
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = Version() },
            ["instructions"] = Instructions,
        };
    }

    private JsonObject CallTool(JsonNode? id, JsonObject? p)
    {
        var name = p?["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : null;
        if (name is null) return Error(id, -32602, "Invalid params: missing tool name");
        if (!McpTools.Exists(name)) return Error(id, -32602, $"Unknown tool: {name}");
        var args = p?["arguments"] as JsonObject;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        McpToolResult result;
        try { result = tools.Call(name, args); }
        catch (Exception ex)
        {
            // 工具执行失败（库被锁、文件读不到……）：按 MCP 约定放进结果、isError=true，agent 能读到原因
            log?.WriteLine($"[mcp] {name} failed: {ex}");
            result = new McpToolResult(L($"工具执行失败：{ex.Message}", $"Tool failed: {ex.Message}"), true);
        }
        log?.WriteLine($"[mcp] {name} {args?.ToJsonString(Compact) ?? "{}"} → {result.Text.Length} chars{(result.IsError ? " (error)" : "")} in {sw.ElapsedMilliseconds} ms");
        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = ExploreBuilder.Lf(result.Text) }),
            ["isError"] = result.IsError,
        });
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static string Version() =>
        typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
