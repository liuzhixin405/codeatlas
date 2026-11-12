using CodeAtlas.Cli.Commands;

namespace CodeAtlas.Cli;

public static class Program
{
    private static string Usage => L("""
        CodeAtlas — 代码地图索引器（里程碑 1：tree-sitter + CLI）

        用法：
          codeatlas index  <repo> [--db <path>] [--full] [--roslyn auto|on|off] [--sln <path>] [--project <外部根> …] [--no-nested]
          codeatlas query  <repo> <name|qualified-name> [--in] [--out] [--depth N] [--kinds calls,implements,...] [--db <path>]
          codeatlas query  <repo> <name|qualified-name> --outline [--depth N] [--no-externals] [--tests] [--db <path>]
          codeatlas query  <repo> <name|qualified-name> --impact [--depth N] [--tests] [--db <path>]（谁调了它、一路影响到哪些入口）
          codeatlas query  <repo> <name|qualified-name> --relations [--depth 1|2] [--tests] [--db <path>]（以它所属的类为中心，类与类之间的依赖，按组件分组）
          codeatlas query  <repo> <A> --to <B> [--depth N] [--max-paths N] [--json] [--db <path>]（A 到 B 的调用路径，含分派边；默认最多 8 步、10 条）
          codeatlas query  <repo> <name|qualified-name|#符号id> --flow [--depth N] [--json] [--db <path>]（从这个方法出发的时序树；入口与 arch --flow 相同）
          codeatlas search <repo> <text> [--limit N] [--db <path>]
          codeatlas serve  <repo> [--db <path>] [--port N] [--parent-pid N] [--token T] [--roslyn auto|off] [--project <外部根> …] [--no-nested] [--ai-provider openai|anthropic] [--ai-endpoint <url>] [--ai-model <名>] [--ai-timeout <秒>] [--ai-concurrency N] [--ai-no-source] [--no-ai]
          codeatlas arch   <repo> [--db <path>] [--json] [--no-store] [--project <外部根> …] [--no-nested]
          codeatlas arch   <repo> --cycles files [--edges certain|all] [--json] [--db <path>]（文件级 import 环，CI 用：有环退出码 1；默认只算确定 import）
          codeatlas arch   <repo> --dsm <容器键|*> [--root <组件键>] [--json] [--db <path>]（* = 整个仓库，纯类库仓库用）
          codeatlas arch   <repo> --entries <容器键> [--kind http|hosted|command|job|hub|grpc|handler|page|api-route] [--q <文本>] [--json] [--db <path>]
          codeatlas arch   <repo> --flow <入口符号 id> [--depth N] [--json] [--db <path>]
          codeatlas arch   <repo> --framework <容器键> [--json] [--db <path>]（框架流程：中间件 → 控制器 → 分派器 → 行为链 → Handler → 收尾）
          codeatlas arch   <repo> --flow-coverage <容器键> [--sample N] [--levels N] [--json] [--db <path>]（任意方法时序图前几层的业务调用覆盖率，回归用）
          codeatlas arch   <repo> --narrate <container|component|flow> <键> [--force] [--facts] [--ai-provider openai|anthropic] [--ai-endpoint <url>] [--ai-model <名>] [--ai-timeout <秒>] [--ai-concurrency N] [--ai-no-source] [--no-ai] [--json] [--db <path>]
          codeatlas arch   <repo> --narrate-all [--flows N] [--force] [--ai-* …] [--db <path>]
          codeatlas mcp    <repo> [--db <path>]（stdio 上的 MCP server，给 Claude Code 等 AI 编程助手查询索引）
          codeatlas affected <repo> [--base <ref>] [--stdin] [--depth N] [--filter <glob> …] [--json] [--quiet] [--db <path>]（改动 → 改动的符号 → 受影响的入口与测试）

        affected：默认 base = HEAD（还没提交的改动）；--base origin/main 取 merge-base（这个分支相对主干的全部改动 + 未提交的）；--stdin 读 git diff -U0 或文件名列表（自动识别）；--quiet 只打印测试文件，可管道给测试运行器；--filter 自定义哪些路径算测试（可重复）；--depth 往上的层数（默认 15，与影响范围一致）。
        --roslyn auto（默认）：tree-sitter 之后，若同目录有 codeatlas-roslyn 且仓库里有 .sln/.slnx，就用 Roslyn 覆盖 C# 的精确语义；on 缺任一项即报错；off 不跑。
        多项目：带自己 .git 目录的子目录（独立克隆）与子模块默认一起索引，--no-nested 关掉；git worktree（.git 文件指向 …/worktrees/…）总是跳过。
        --project <路径> 可重复，把仓库外的目录（例如前端仓库旁边的后端仓库）一起索引；也可写在 <repo>/.codeatlas/config.json：{"projects": ["../other-repo"]}（相对 <repo>）。
        外部根的文件以相对 <repo> 的路径入库（../other-repo/Api/Foo.cs）；根互相包含时同一个文件只入库一次。Roslyn 对主根、外部根、每个嵌套仓库各找一个解决方案依次跑。
        arch：扫描容器图事实（可部署单元、外部系统、它们之间的边）并打印；写库时接着识别入口并匹配路由；index 结束时也会自动跑一次。
        --entries 列出容器的入口（控制器动作、常驻服务、分发处理器、前端页面）；--flow 打印从某入口出发的时序树。
        --narrate 用 AI（默认本地 llama-server http://127.0.0.1:8080/v1 的 qwen3.6-27b；--ai-provider openai 也能接 DeepSeek / 通义 / OpenAI 等联网的 OpenAI 兼容服务，anthropic 接 Claude）为一个容器 / 组件（<容器键>|<组件键>）/ 入口流程（入口符号 id）生成叙述并打印。
        --facts 只打印喂给模型的事实文本（不调模型）；--narrate-all 批量预生成全部容器与项目层组件的叙述（--flows N 再加每个容器前 N 个 HTTP 入口），已有且未过期的跳过，可中断续跑——本地慢模型适合夜里跑。叙述缓存在索引库旁的 narratives.db，重建索引不丢，按相关文件内容与架构事实判过期，换模型不作废；--ai-timeout 单次请求超时秒数（默认 300）；--no-ai 关闭叙述（serve 的叙述接口全部返回 unavailable）。--ai-concurrency 同时在飞的请求数（默认 1，联网可 2–4）；--ai-no-source 不发入口方法源码。联网服务的 API Key 只从环境变量 CODEATLAS_AI_API_KEY 读。端点不是回环地址时会提示数据将发往该主机。
        """, """
        CodeAtlas — code map indexer (milestone 1: tree-sitter + CLI)

        Usage:
          codeatlas index  <repo> [--db <path>] [--full] [--roslyn auto|on|off] [--sln <path>] [--project <external root> …] [--no-nested]
          codeatlas query  <repo> <name|qualified-name> [--in] [--out] [--depth N] [--kinds calls,implements,...] [--db <path>]
          codeatlas query  <repo> <name|qualified-name> --outline [--depth N] [--no-externals] [--tests] [--db <path>]
          codeatlas query  <repo> <name|qualified-name> --impact [--depth N] [--tests] [--db <path>] (who calls it, and which entry points it affects)
          codeatlas query  <repo> <name|qualified-name> --relations [--depth 1|2] [--tests] [--db <path>] (dependencies between classes, centered on its class, grouped by component)
          codeatlas query  <repo> <A> --to <B> [--depth N] [--max-paths N] [--json] [--db <path>] (call paths from A to B, including dispatch edges; default at most 8 steps, 10 paths)
          codeatlas query  <repo> <name|qualified-name|#symbol-id> --flow [--depth N] [--json] [--db <path>] (sequence tree starting from this method; same as arch --flow for entries)
          codeatlas search <repo> <text> [--limit N] [--db <path>]
          codeatlas serve  <repo> [--db <path>] [--port N] [--parent-pid N] [--token T] [--roslyn auto|off] [--project <external root> …] [--no-nested] [--ai-provider openai|anthropic] [--ai-endpoint <url>] [--ai-model <name>] [--ai-timeout <seconds>] [--ai-concurrency N] [--ai-no-source] [--no-ai]
          codeatlas arch   <repo> [--db <path>] [--json] [--no-store] [--project <external root> …] [--no-nested]
          codeatlas arch   <repo> --cycles files [--edges certain|all] [--json] [--db <path>] (file-level import cycles for CI: exit code 1 when cycles exist; certain imports only by default)
          codeatlas arch   <repo> --dsm <container key|*> [--root <component key>] [--json] [--db <path>] (* = whole repository, for pure library repos)
          codeatlas arch   <repo> --entries <container key> [--kind http|hosted|command|job|hub|grpc|handler|page|api-route] [--q <text>] [--json] [--db <path>]
          codeatlas arch   <repo> --flow <entry symbol id> [--depth N] [--json] [--db <path>]
          codeatlas arch   <repo> --framework <container key> [--json] [--db <path>] (framework flow: middleware → controllers → dispatcher → pipeline behaviors → handler → response)
          codeatlas arch   <repo> --flow-coverage <container key> [--sample N] [--levels N] [--json] [--db <path>] (business-call coverage of the first levels of arbitrary method sequence diagrams, for regression)
          codeatlas arch   <repo> --narrate <container|component|flow> <key> [--force] [--facts] [--ai-provider openai|anthropic] [--ai-endpoint <url>] [--ai-model <name>] [--ai-timeout <seconds>] [--ai-concurrency N] [--ai-no-source] [--no-ai] [--json] [--db <path>]
          codeatlas arch   <repo> --narrate-all [--flows N] [--force] [--ai-* …] [--db <path>]
          codeatlas mcp    <repo> [--db <path>] (MCP server over stdio, lets AI coding assistants such as Claude Code query the index)
          codeatlas affected <repo> [--base <ref>] [--stdin] [--depth N] [--filter <glob> …] [--json] [--quiet] [--db <path>] (changes → changed symbols → affected entry points and tests)

        affected: default base = HEAD (uncommitted changes); --base origin/main uses the merge-base (everything this branch changed relative to the mainline + uncommitted); --stdin reads git diff -U0 or a file list (auto-detected); --quiet prints only test files, for piping into a test runner; --filter customizes which paths count as tests (repeatable); --depth is the number of levels upward (default 15, same as Impact).
        --roslyn auto (default): after tree-sitter, if codeatlas-roslyn sits in the same directory and the repository has a .sln/.slnx, Roslyn overrides C# with precise semantics; on fails if either is missing; off skips it.
        Multiple projects: subdirectories with their own .git directory (independent clones) and submodules are indexed too by default; --no-nested turns this off; git worktrees (.git file pointing to …/worktrees/…) are always skipped.
        --project <path> (repeatable) indexes directories outside the repository as well (e.g. a backend repo next to the frontend repo); can also be set in <repo>/.codeatlas/config.json: {"projects": ["../other-repo"]} (relative to <repo>).
        Files from external roots are stored with paths relative to <repo> (../other-repo/Api/Foo.cs); when roots contain each other, a file is stored only once. Roslyn finds one solution each for the main root, every external root and every nested repository and runs them in turn.
        arch: scans container graph facts (deployable units, external systems, edges between them) and prints them; when writing to the index it also detects entry points and matches routes; index runs it automatically at the end too.
        --entries lists a container's entry points (controller actions, hosted services, dispatch handlers, frontend pages); --flow prints the sequence tree starting from an entry point.
        --narrate uses AI (default: qwen3.6-27b on a local llama-server at http://127.0.0.1:8080/v1; --ai-provider openai also works with online OpenAI-compatible services such as DeepSeek / Qwen / OpenAI, anthropic connects to Claude) to generate and print a narration for a container / component (<container key>|<component key>) / entry flow (entry symbol id).
        --facts only prints the facts text fed to the model (no model call); --narrate-all pre-generates narrations for all containers and project-level components in bulk (--flows N adds the first N HTTP entry points of each container); existing, up-to-date narrations are skipped, and it can be interrupted and resumed — good for running slow local models overnight. Narrations are cached in narratives.db next to the index and survive index rebuilds; staleness is judged by related file contents and architecture facts, and switching models does not invalidate them; --ai-timeout is the per-request timeout in seconds (default 300); --no-ai disables narration (serve's narration endpoints all return unavailable). --ai-concurrency is the number of in-flight requests (default 1; 2–4 for online services); --ai-no-source does not send entry method source code. API keys for online services are read only from the environment variable CODEATLAS_AI_API_KEY. A notice is shown when the endpoint is not a loopback address that data will be sent to that host.
        """);

    private static readonly HashSet<string> Flags = ["--full", "--in", "--out", "--json", "--no-store", "--no-git", "--no-ai", "--force", "--facts", "--ai-no-source", "--narrate-all", "--no-nested", "--outline", "--impact", "--relations", "--no-externals", "--tests", "--stdin", "--quiet"];

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // 输出语言：CODEATLAS_LOCALE（扩展按 VS Code 显示语言传），没有则系统 UI 语言；--lang 可再覆盖（见 Run）
        Loc.Set(Loc.Detect());
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return Run(args, Console.Out, Console.Error, cts.Token);
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr) => Run(args, stdout, stderr, CancellationToken.None);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        args = TakeLang(args);
        if (args.Length == 0) { stderr.WriteLine(Usage); return 2; }
        ParsedArgs parsed;
        // --flow 在 query 里是开关（从这个符号出发的时序树），在 arch 里带入口 id
        try { parsed = ParsedArgs.Parse(args.Skip(1).ToList(), args[0] == "query" ? [.. Flags, "--flow"] : Flags); }
        catch (ArgumentException ex) { stderr.WriteLine(ex.Message); stderr.WriteLine(Usage); return 2; }

        try
        {
            return args[0] switch
            {
                "index" => IndexCommand.Run(parsed, stdout, stderr),
                "query" => QueryCommand.Run(parsed, stdout, stderr),
                "search" => SearchCommand.Run(parsed, stdout, stderr),
                "serve" => ServeCommand.Run(parsed, stdout, stderr, ct),
                "arch" => ArchCommand.Run(parsed, stdout, stderr),
                "mcp" => McpCommand.Run(parsed, stdout, stderr, ct),
                "affected" => AffectedCommand.Run(parsed, stdout, stderr, Console.In),
                _ => Unknown(args[0], stderr),
            };
        }
        catch (Exception ex)
        {
            stderr.WriteLine(L($"错误：{ex.Message}", $"Error: {ex.Message}"));
            return 1;
        }
    }

    /// <summary>--lang zh|en（任意位置）：设定输出语言并从参数里拿掉。</summary>
    private static string[] TakeLang(string[] args)
    {
        var i = Array.IndexOf(args, "--lang");
        if (i < 0 || i + 1 >= args.Length) return args;
        Loc.Set(args[i + 1]);
        return [.. args[..i], .. args[(i + 2)..]];
    }

    private static int Unknown(string command, TextWriter stderr)
    {
        stderr.WriteLine(L($"未知命令：{command}", $"Unknown command: {command}"));
        stderr.WriteLine(Usage);
        return 2;
    }
}
