using System.Text.Json.Nodes;
using CodeAtlas.Cli.Commands;
using CodeAtlas.Server.Mcp;

namespace CodeAtlas.Cli.Tests;

/// <summary>
/// codeatlas mcp：stdio 上的 JSON-RPC 握手、tools/list、tools/call，以及主工具 codeatlas_explore 在一个真实小仓库上的输出
/// （钉到符号名、行号、调用路径、多候选标注、截断与过期警告）。
/// </summary>
public class McpTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-mcp-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_repo, ".codeatlas", "index.db");

    public McpTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src", "util"));
        File.WriteAllText(Path.Combine(_repo, "src", "util", "format.ts"), "export function helper(id: number): string {\n  return String(id);\n}\n");
        File.WriteAllText(Path.Combine(_repo, "src", "svc.ts"), "import { helper } from './util/format';\n\nexport class Svc {\n  find(id: number): string {\n    return helper(id);\n  }\n}\n");
        File.WriteAllText(Path.Combine(_repo, "src", "app.ts"), "import { Svc } from './svc';\n\nexport function main(): void {\n  new Svc().find(1);\n}\n\nexport function workHours(): number {\n  return 8;\n}\n");
        // store.save 的接收者类型未知、两个同名定义：0.3 的多候选
        File.WriteAllText(Path.Combine(_repo, "src", "store.py"),
            "class FileStore:\n    def save(self, item):\n        return item\n\n\nclass DbStore:\n    def save(self, item):\n        return item\n\n\ndef persist(store, item):\n    return store.save(item)\n");
        // 两个长函数：预算放不下，截断用
        File.WriteAllText(Path.Combine(_repo, "src", "big.ts"),
            "export function bigOne(): number {\n" + string.Concat(Enumerable.Range(1, 120).Select(i => $"  const one{i:000} = {i}; // padding padding padding\n")) + "  return 1;\n}\n\n" +
            "export function bigTwo(): number {\n" + string.Concat(Enumerable.Range(1, 120).Select(i => $"  const two{i:000} = {i}; // padding padding padding\n")) + "  return 2;\n}\n");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Assert.Equal(0, Program.Run(["index", _repo, "--roslyn", "off"], stdout, stderr));
    }

    private McpServer Server() => new(new McpTools(_repo, Db));

    private static JsonObject Send(McpServer server, string json) => server.Handle(json) ?? throw new InvalidOperationException("没有响应");

    private static string Call(McpServer server, string tool, JsonObject args, out bool isError)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
        };
        var response = Send(server, request.ToJsonString());
        Assert.Equal(7, response["id"]!.GetValue<int>());
        var result = response["result"]!.AsObject();
        isError = result["isError"]!.GetValue<bool>();
        var content = result["content"]!.AsArray();
        Assert.Single(content);
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        return content[0]!["text"]!.GetValue<string>();
    }

    private string Explore(string query, int? maxChars = null)
    {
        var args = new JsonObject { ["query"] = query };
        if (maxChars is { } m) args["maxChars"] = m;
        var text = Call(Server(), "codeatlas_explore", args, out var isError);
        Assert.False(isError);
        return text;
    }

    private static List<string> Lines(string s) => s.Split('\n').ToList();

    // ---------- 协议 ----------

    [Fact]
    public void Handshake_OverStdio_OnlyProtocolMessagesOnStdout()
    {
        var input = new StringReader(string.Join("\n",
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":"two","method":"ping"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""") + "\n");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var args = ParsedArgs.Parse([_repo], new HashSet<string>());

        Assert.Equal(0, McpCommand.Run(args, input, stdout, stderr, CancellationToken.None));

        // 通知不回：三个请求三行响应，每行都是 JSON-RPC 消息；日志都在 stderr
        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        var init = JsonNode.Parse(lines[0])!.AsObject();
        Assert.Equal("2.0", init["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, init["id"]!.GetValue<int>());
        Assert.Equal("2025-06-18", init["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("codeatlas", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(init["result"]!["capabilities"]!["tools"]);
        Assert.Contains("codeatlas_explore FIRST", init["result"]!["instructions"]!.GetValue<string>());
        var ping = JsonNode.Parse(lines[1])!.AsObject();
        Assert.Equal("two", ping["id"]!.GetValue<string>());
        Assert.Empty(ping["result"]!.AsObject());
        Assert.Equal(3, JsonNode.Parse(lines[2])!["id"]!.GetValue<int>());
        Assert.Contains("[mcp] initialize from test", stderr.ToString());
        Assert.Contains("[mcp] notification notifications/initialized", stderr.ToString());
    }

    [Fact]
    public void Initialize_UnknownProtocolVersion_AnswersLatestSupported()
    {
        var r = Send(Server(), """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");
        Assert.Equal(McpServer.ProtocolVersions[0], r["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Errors_ParseError_UnknownMethod_UnknownTool()
    {
        var server = Server();
        var parse = Send(server, "{not json");
        Assert.Equal(-32700, parse["error"]!["code"]!.GetValue<int>());
        Assert.Null(parse["id"]);

        var method = Send(server, """{"jsonrpc":"2.0","id":5,"method":"resources/list"}""");
        Assert.Equal(-32601, method["error"]!["code"]!.GetValue<int>());
        Assert.Equal(5, method["id"]!.GetValue<int>());

        var tool = Send(server, """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"nope","arguments":{}}}""");
        Assert.Equal(-32602, tool["error"]!["code"]!.GetValue<int>());

        // 通知与客户端发来的响应都不回
        Assert.Null(server.Handle("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}}"""));
        Assert.Null(server.Handle("""{"jsonrpc":"2.0","id":9,"result":{}}"""));
    }

    [Fact]
    public void ToolsList_ExploreIsFirstAndSteersAgents()
    {
        var r = Send(Server(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = r["result"]!["tools"]!.AsArray();
        Assert.Equal(["codeatlas_explore", "codeatlas_search", "codeatlas_callers", "codeatlas_callees", "codeatlas_impact", "codeatlas_status"],
            tools.Select(t => t!["name"]!.GetValue<string>()));
        var explore = tools[0]!.AsObject();
        Assert.StartsWith("PRIMARY TOOL", explore["description"]!.GetValue<string>());
        Assert.Equal("object", explore["inputSchema"]!["type"]!.GetValue<string>());
        Assert.Equal(["query"], explore["inputSchema"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("integer", explore["inputSchema"]!["properties"]!["maxChars"]!["type"]!.GetValue<string>());
        // 次要工具都把人引回 explore
        foreach (var t in tools.Skip(1).Take(4)) Assert.Contains("codeatlas_explore", t!["description"]!.GetValue<string>());
    }

    // ---------- explore ----------

    [Fact]
    public void Explore_SymbolNames_SourceWithLineNumbers_PathsAndImpact()
    {
        var text = Explore("how does Svc.find use helper");
        var lines = Lines(text);

        Assert.Equal("# CodeAtlas explore: how does Svc.find use helper", lines[0]);
        Assert.DoesNotContain("警告", text);
        // 命中：how / does / use 是停用词；Svc.find 按段后缀、helper 按短名
        Assert.Contains(lines, l => l.StartsWith("1. Svc.find  [method]  src/svc.ts:4-6  得分 90（名字 Svc.find）"));
        Assert.Contains(lines, l => l.StartsWith("2. helper  [function]  src/util/format.ts:1-3  得分 90（名字 helper）"));

        // 源码：按文件分组、带行号，从磁盘读
        var svc = lines.IndexOf("### src/svc.ts");
        Assert.True(svc > 0);
        Assert.Equal("// Svc.find  [method]  L4-6", lines[svc + 1]);
        Assert.Equal("     4    find(id: number): string {", lines[svc + 2]);
        Assert.Equal("     5      return helper(id);", lines[svc + 3]);
        Assert.Equal("     6    }", lines[svc + 4]);
        Assert.Contains("     1  export function helper(id: number): string {", lines);

        // 调用路径、调出、影响范围
        Assert.Contains("- Svc.find → helper  calls  src/svc.ts:5  0.95", lines);
        Assert.Contains("  1. L5 call helper  0.95  → src/util/format.ts:1", lines);
        Assert.Contains("Svc.find  (src/svc.ts:4): 直接调用方 1，测试里的调用方 0，受影响入口 0", lines);
        Assert.Contains("  ← call main  src/app.ts:4  0.9", lines);
        Assert.DoesNotContain("## 已截断", text);
        Assert.True(text.IndexOf("## 源码", StringComparison.Ordinal) < text.IndexOf("## 调用路径", StringComparison.Ordinal));
    }

    [Fact]
    public void Explore_NaturalLanguage_UsesFullTextPrefix()
    {
        // 没有叫 format 的符号：走全文前缀 format*，对上限定名 src/util/format.ts:helper 里的路径段；how / does / work 是停用词
        var text = Explore("how does format work?");
        Assert.Contains("1. helper  [function]  src/util/format.ts:1-3  得分 40（全文 format*）", text);
        Assert.Contains("     2    return String(id);", Lines(text));
        Assert.DoesNotContain("workHours", text); // 停用词 work 不拿去做全文前缀（否则 work* 会对上 workHours）
    }

    [Fact]
    public void Explore_CaseInsensitiveNames_MemberInheritsTypeMatch_ContainedSourceOnce()
    {
        // svc / FIND 大小写都不对：按名字不分大小写找；Svc.find 继承所属类型 Svc 对上的 svc（比它自己经路径段全文对上的 40 分高），两个词相加排第一
        var text = Explore("svc FIND");
        var lines = Lines(text);
        Assert.Equal("1. Svc.find  [method]  src/svc.ts:4-6  得分 180（所属类型 Svc 对上 svc；名字 FIND（不分大小写））  #" + lines[3].Split('#')[^1], lines[3]);
        Assert.StartsWith("2. Svc  [class]  src/svc.ts:3-7  得分 90（名字 svc（不分大小写））", lines[4]);
        // Svc.find 在 Svc 的源码范围里：只贴一次
        Assert.Contains("// Svc  [class]  L3-7", lines);
        Assert.DoesNotContain("// Svc.find  [method]  L4-6", lines);
        Assert.Single(lines, l => l == "     5      return helper(id);");
    }

    [Fact]
    public void Explore_LowConfidenceCandidates_AreFlagged()
    {
        var lines = Lines(Explore("persist"));
        var step = lines.FindIndex(l => l.StartsWith("  1. L12 ambiguous "));
        Assert.True(step > 0, string.Join("\n", lines));
        Assert.EndsWith("0.3 [低置信：同名多候选之一，未确定]：2 个候选，分不清是哪个", lines[step]);
        Assert.Equal("       ? DbStore.save  src/store.py:7", lines[step + 1]);
        Assert.Equal("       ? FileStore.save  src/store.py:2", lines[step + 2]);
    }

    [Fact]
    public void Explore_OverBudget_TruncatesByRelevance()
    {
        var text = Explore("bigOne bigTwo", maxChars: 3000);
        Assert.True(text.Length <= 3000, $"长度 {text.Length}");
        var lines = Lines(text);
        // bigOne 排第一（同分时限定名短、id 小的在前）：贴了前半部分；bigTwo 的源码没贴，列在末尾
        Assert.Contains(lines, l => l.StartsWith("1. bigOne  [function]  src/big.ts:1-123"));
        Assert.Contains("// bigOne  [function]  L1-123", lines);
        Assert.Contains("     2    const one001 = 1; // padding padding padding", lines);
        Assert.DoesNotContain("     121    const one120 = 120; // padding padding padding", lines);
        Assert.DoesNotContain("// bigTwo  [function]  L125-247", lines);
        Assert.Contains("## 已截断（预算 3000 字符）", lines);
        Assert.Contains("- 最后一个贴出的符号源码只贴了前半部分。", lines);
        Assert.Contains("  - bigTwo  src/big.ts:125-247", lines);

        // 预算够时两段都完整
        var full = Explore("bigOne bigTwo", maxChars: 40000);
        Assert.Contains("   246    return 2;", Lines(full));
        Assert.DoesNotContain("## 已截断", full);
    }

    [Fact]
    public void Explore_StaleFiles_WarnAtTopAndMarkGroup()
    {
        var svc = Path.Combine(_repo, "src", "svc.ts");
        var before = new FileInfo(svc).Length;
        File.AppendAllText(svc, "// edited after indexing\n");
        var after = new FileInfo(svc).Length;

        var lines = Lines(Explore("Svc.find helper"));
        Assert.StartsWith("警告：1 个文件在建索引后改过", lines[1]);
        Assert.Equal($"  - src/svc.ts  (大小 {before} → {after} 字节)", lines[2]);
        Assert.Contains("### src/svc.ts  （已过期，见顶部警告）", lines);
        Assert.Contains("### src/util/format.ts", lines); // 没改的文件不标

        // 只动修改时间、内容没变（git checkout / touch）：与增量索引同一口径，内容没变就不算过期
        var format = Path.Combine(_repo, "src", "util", "format.ts");
        File.SetLastWriteTimeUtc(format, File.GetLastWriteTimeUtc(format).AddMinutes(-5));
        Assert.DoesNotContain("src/util/format.ts  (", Explore("helper"));

        // 大小不变、内容变了：算哈希发现
        var original = File.ReadAllText(format);
        File.WriteAllText(format, original.Replace("String(id)", "Strinq(id)"));
        Assert.Equal(original.Length, File.ReadAllText(format).Length);
        Assert.Contains("  - src/util/format.ts  (内容变了（大小相同，修改时间 索引 ", Explore("helper"));
    }

    // ---------- 次要工具 ----------

    [Fact]
    public void SecondaryTools_SearchCallersStatus()
    {
        var server = Server();
        Assert.Contains("src/svc.ts:Svc.find  [method]  src/svc.ts:4", Call(server, "codeatlas_search", new JsonObject { ["query"] = "find" }, out _));
        Assert.Contains("  ← call main  src/app.ts:4  0.9", Call(server, "codeatlas_callers", new JsonObject { ["symbol"] = "Svc.find" }, out _));
        Assert.Contains("  1. L5 call helper  0.95", Call(server, "codeatlas_callees", new JsonObject { ["symbol"] = "Svc.find" }, out _));
        var missing = Call(server, "codeatlas_impact", new JsonObject { ["symbol"] = "NoSuchThing" }, out var isError);
        Assert.True(isError);
        Assert.StartsWith("未找到符号：NoSuchThing", missing);
        var status = Call(server, "codeatlas_status", [], out _);
        Assert.Contains("文件 5，", status);
        Assert.Contains("所有已索引文件的大小 / 修改时间与索引一致。", status);
    }

    [Fact]
    public void MissingDatabase_ToolCallIsError()
    {
        var server = new McpServer(new McpTools(_repo, Path.Combine(_repo, "nope.db")));
        var text = Call(server, "codeatlas_explore", new JsonObject { ["query"] = "x" }, out var isError);
        Assert.True(isError);
        Assert.StartsWith("索引库不存在：", text);
        Assert.False(File.Exists(Path.Combine(_repo, "nope.db"))); // 不顺手建空库
    }

    [Fact]
    public void OldDatabaseMissingAColumn_ToolCallIsError_AndFileIsUntouched()
    {
        // 模拟旧库缺一列：读写方式打开（IndexDatabase.Open）会 ALTER TABLE 补列，也就是写库；MCP 必须只读，库文件一字节不变。
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Db};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "ALTER TABLE files DROP COLUMN roslyn_miss";
            cmd.ExecuteNonQuery();
        }
        var before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Db)));

        var server = Server();
        var explore = Call(server, "codeatlas_explore", new JsonObject { ["query"] = "Svc find" }, out var exploreError);
        Call(server, "codeatlas_status", [], out var statusError);

        Assert.True(exploreError);
        Assert.True(statusError);
        Assert.StartsWith("索引库是旧版本 CodeAtlas 建的，缺少列 files.roslyn_miss。运行一次 codeatlas index", explore);
        Assert.Equal(before, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Db))));
        using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Db};Mode=ReadOnly;Pooling=False");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM pragma_table_info('files') WHERE name = 'roslyn_miss'";
        Assert.Equal(0L, (long)q.ExecuteScalar()!);
    }

    [Fact]
    public void Explore_SynthesizedEventEdge_IsMarkedAsInferred()
    {
        // 事件频道合成边（emit('x') → on('x', handler)，via=event:x，0.4）：不是直接调用，输出里要说清楚
        var repo = Path.Combine(Path.GetTempPath(), "codeatlas-mcp-ev-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "src"));
            File.WriteAllText(Path.Combine(repo, "src", "bus.ts"),
                "import { EventEmitter } from \"events\";\n\nexport const bus = new EventEmitter();\n\nexport class OrderService {\n  place(id: number): void {\n    bus.emit(\"order:placed\", id);\n  }\n}\n");
            File.WriteAllText(Path.Combine(repo, "src", "listeners.ts"),
                "import { bus } from \"./bus\";\n\nexport function sendMail(id: number): void {\n  console.log(\"mail\", id);\n}\n\nexport function register(): void {\n  bus.on(\"order:placed\", sendMail);\n}\n");
            Assert.Equal(0, Program.Run(["index", repo, "--roslyn", "off"], new StringWriter(), new StringWriter()));

            var server = new McpServer(new McpTools(repo, Path.Combine(repo, ".codeatlas", "index.db")));
            var text = Call(server, "codeatlas_explore", new JsonObject { ["query"] = "OrderService.place sendMail" }, out var isError);

            Assert.False(isError);
            var line = Lines(text).Single(l => l.StartsWith("- OrderService.place → sendMail  "));
            Assert.Equal("- OrderService.place → sendMail  calls(event:order:placed)  src/bus.ts:7  0.4 [推断：事件频道 emit / on 同名配对，不是直接调用]", line);
        }
        finally
        {
            if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void NotAnIndexDatabase_ToolCallIsError()
    {
        var path = Path.Combine(_repo, "other.db");
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "CREATE TABLE x (id INTEGER)";
            cmd.ExecuteNonQuery();
        }
        var server = new McpServer(new McpTools(_repo, path));
        var text = Call(server, "codeatlas_status", [], out var isError);
        Assert.True(isError);
        Assert.StartsWith("打不开索引库：不是 CodeAtlas 索引库：", text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }
}
