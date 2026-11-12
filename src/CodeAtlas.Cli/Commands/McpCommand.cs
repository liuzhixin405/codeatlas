using System.Text;
using CodeAtlas.Server.Mcp;

namespace CodeAtlas.Cli.Commands;

/// <summary>
/// codeatlas mcp &lt;repo&gt; [--db &lt;path&gt;]：以 stdio 传输跑 MCP server，给 Claude Code 等 AI 编程助手查询索引。
/// stdout 只写协议消息；日志与提示写 stderr。库不存在时照样启动，工具调用返回"先运行 codeatlas index"的错误结果。
/// </summary>
internal static class McpCommand
{
    public const string UsageText = "codeatlas mcp <repo> [--db <path>]";

    public static int Run(ParsedArgs args, TextWriter stdout, TextWriter stderr, CancellationToken ct) =>
        Run(args, null, stdout, stderr, ct);

    /// <param name="input">测试传 StringReader；null = 进程的 stdin（按 UTF-8 读）。</param>
    public static int Run(ParsedArgs args, TextReader? input, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (args.Positional.Count < 1) { stderr.WriteLine(L("用法：", "Usage: ") + UsageText); return 2; }
        var repo = Path.GetFullPath(args.Positional[0]);
        if (!Directory.Exists(repo)) { stderr.WriteLine(L($"目录不存在：{repo}", $"Directory does not exist: {repo}")); return 2; }
        var dbPath = CommonOptions.DbPath(repo, args);
        if (!File.Exists(dbPath)) stderr.WriteLine(L($"提示：索引库不存在：{dbPath}。先运行 codeatlas index {repo}；在那之前工具调用会返回错误。",
            $"Note: index database does not exist: {dbPath}. Run codeatlas index {repo} first; until then tool calls return an error."));

        var protocolOut = stdout;
        if (input is null)
        {
            // 真实进程：自己开 stdin / stdout 的 UTF-8 流（不带 BOM、\n 换行），再把 Console.Out 指到 stderr，
            // 免得哪里顺手 Console.WriteLine 把协议流弄坏
            input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            if (ReferenceEquals(stdout, Console.Out))
            {
                protocolOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
                Console.SetOut(stderr);
            }
        }
        stderr.WriteLine($"[mcp] CodeAtlas MCP server (stdio) — repo {repo}, db {dbPath}");
        try
        {
            new McpServer(new McpTools(repo, dbPath), stderr).RunAsync(input, protocolOut, ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}
