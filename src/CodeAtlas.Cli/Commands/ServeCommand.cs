using CodeAtlas.Server;

namespace CodeAtlas.Cli.Commands;

internal static class ServeCommand
{
    public static int Run(ParsedArgs args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (args.Positional.Count < 1) { stderr.WriteLine(L("用法：codeatlas serve <repo> [--db <path>] [--port N] [--parent-pid N] [--token T] [--roslyn auto|off] [--project <外部根> …] [--no-nested] [--ai-provider openai|anthropic] [--ai-endpoint <url>] [--ai-model <名>] [--ai-timeout <秒>] [--ai-concurrency N] [--ai-no-source] [--no-ai]", "Usage: codeatlas serve <repo> [--db <path>] [--port N] [--parent-pid N] [--token T] [--roslyn auto|off] [--project <external root> …] [--no-nested] [--ai-provider openai|anthropic] [--ai-endpoint <url>] [--ai-model <name>] [--ai-timeout <seconds>] [--ai-concurrency N] [--ai-no-source] [--no-ai]")); return 2; }
        var repo = Path.GetFullPath(args.Positional[0]);
        if (!Directory.Exists(repo)) { stderr.WriteLine(L($"目录不存在：{repo}", $"Directory does not exist: {repo}")); return 2; }

        var port = 0;
        if (args.Value("--port") is { } p && !int.TryParse(p, out port)) { stderr.WriteLine(L($"--port 不是数字：{p}", $"--port is not a number: {p}")); return 2; }
        int? parentPid = null;
        if (args.Value("--parent-pid") is { } pp)
        {
            if (!int.TryParse(pp, out var pid)) { stderr.WriteLine(L($"--parent-pid 不是数字：{pp}", $"--parent-pid is not a number: {pp}")); return 2; }
            parentPid = pid;
        }

        var token = args.Value("--token") ?? Environment.GetEnvironmentVariable("CODEATLAS_TOKEN");
        var roslyn = args.Value("--roslyn") ?? "auto";
        if (roslyn is not ("auto" or "off")) { stderr.WriteLine(L($"serve 的 --roslyn 只能是 auto / off，不是 {roslyn}", $"--roslyn for serve must be auto / off, not {roslyn}")); return 2; }
        var ai = AiOptions.Parse(args, stderr, out var aiOk);
        if (!aiOk) return 2;
        // 外部根在这里只规范成绝对路径；与 .codeatlas/config.json 的合并在每次索引前做（IndexService），改了配置下次索引就生效
        var projects = args.Values("--project").Select(Path.GetFullPath).ToList();
        foreach (var missing in projects.Where(p => !Directory.Exists(p))) stderr.WriteLine(L($"提示：外部项目根不存在，跳过（已入库的文件保留）：{missing}", $"Note: external project root does not exist, skipped (already indexed files are kept): {missing}"));
        var options = new ServerOptions(repo, CommonOptions.DbPath(repo, args), port, parentPid)
        {
            Token = string.IsNullOrWhiteSpace(token) ? null : token, Roslyn = roslyn == "auto", Narration = ai,
            Projects = projects, IncludeNested = !args.Has("--no-nested"),
        };
        try
        {
            ServerHost.RunAsync(options, stdout, ct).GetAwaiter().GetResult();
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }
}
