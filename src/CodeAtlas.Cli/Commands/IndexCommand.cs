using CodeAtlas.Core.Diagnostics;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Roslyn;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter;

namespace CodeAtlas.Cli.Commands;

internal static class IndexCommand
{
    /// <summary>
    /// 在报告的线程上同步执行、加锁串行的 IProgress。不用 System.Progress：它把回调投递到线程池，
    /// 回调之间可能并发、也可能在 Run 返回后才执行——写同一个非线程安全的 TextWriter（测试里的 StringWriter）会损坏它、让进程崩溃，
    /// 真实控制台上则是进度行乱序、跑到汇总行后面。
    /// </summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly object _gate = new();

        public void Report(T value)
        {
            lock (_gate) handler(value);
        }
    }

    public static int Run(ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Positional.Count < 1) { stderr.WriteLine(L("用法：codeatlas index <repo> [--db <path>] [--full] [--roslyn auto|on|off] [--sln <path>] [--project <外部根> …] [--no-nested]", "Usage: codeatlas index <repo> [--db <path>] [--full] [--roslyn auto|on|off] [--sln <path>] [--project <external root> …] [--no-nested]")); return 2; }
        var roslynMode = args.Value("--roslyn") ?? "auto";
        if (roslynMode is not ("auto" or "on" or "off")) { stderr.WriteLine(L($"--roslyn 只能是 auto / on / off，不是 {roslynMode}", $"--roslyn must be auto / on / off, not {roslynMode}")); return 2; }
        var repo = Path.GetFullPath(args.Positional[0]);
        if (!Directory.Exists(repo)) { stderr.WriteLine(L($"目录不存在：{repo}", $"Directory does not exist: {repo}")); return 2; }

        WarnIfNotIgnored(repo, stderr);
        var roots = CommonOptions.Roots(repo, args, stderr);

        var adapters = CommonOptions.CreateAdapters();
        try
        {
            using var db = IndexDatabase.Open(CommonOptions.DbPath(repo, args));
            var options = new IndexOptions { Full = args.Has("--full") };
            var indexer = new TreeSitterIndexer(db, adapters, new HeuristicEdgeResolver(), options);

            var lastPhase = "";
            var progress = new SyncProgress<IndexProgress>(p =>
            {
                if (p.Phase != lastPhase) { lastPhase = p.Phase; stderr.WriteLine($"[{p.Phase}]"); }
                if (p.Phase == "parse" && (p.Done % 500 == 0 || p.Done == p.Total)) stderr.WriteLine($"  {p.Done}/{p.Total}");
            });
            var report = indexer.IndexAsync(roots, progress).GetAwaiter().GetResult();

            stdout.WriteLine($"files: {report.FilesScanned}  parsed: {report.FilesParsed}  unchanged: {report.FilesUnchanged}  skipped: {report.FilesSkipped}  removed: {report.FilesRemoved}  failed: {report.FilesFailed}");
            stdout.WriteLine($"symbols: {report.TotalSymbols}  edges: {report.TotalEdges}  elapsed: {report.Elapsed.TotalSeconds:F1}s");
            stdout.WriteLine($"timings: {PhaseTimings.Format(report.Timings ?? [])}");
            foreach (var (path, error) in report.Errors) stderr.WriteLine(L($"  失败 {path}: {error}", $"  failed {path}: {error}"));
            var exit = report.FilesFailed > 0 ? 1 : 0;

            if (roslynMode != "off")
            {
                var roslynExit = RunRoslyn(db, roots, args.Value("--sln"), roslynMode == "on", options.Full, stdout, stderr);
                if (roslynExit != 0) exit = roslynExit;
            }

            // 后处理三步共用清单与源码缓存；输入没变时 arch + entries 整段跳过，git ref 没变时 git 跳过（见 PostIndexSteps）
            var postTimings = new PhaseTimings();
            var steps = new CodeAtlas.Server.PostIndexSteps(roots, db, postTimings);
            // 容器图事实：只读文件系统与源码文本；失败不影响索引结果
            try
            {
                var arch = steps.Arch();
                stdout.WriteLine(arch is null
                    ? L("arch: 输入没变，跳过", "arch: inputs unchanged, skipped")
                    : $"arch: containers: {arch.Containers}  externals: {arch.Externals}  edges: {arch.Edges}  unmatched: {arch.OutboundUnmatched}  elapsed: {arch.Elapsed.TotalSeconds:F1}s");
            }
            catch (Exception ex) { stderr.WriteLine(L($"提示：容器图扫描失败：{ex.Message}", $"Note: container graph scan failed: {ex.Message}")); }

            if (!args.Has("--no-git"))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (!steps.Git()) stdout.WriteLine(L($"git: ref 没变，跳过  elapsed: {sw.Elapsed.TotalSeconds:F1}s", $"git: ref unchanged, skipped  elapsed: {sw.Elapsed.TotalSeconds:F1}s"));
                    else
                    {
                        var git = CodeAtlas.Core.Storage.GitStorage.Read(db);
                        stdout.WriteLine(git.Error is null
                            ? $"git: commits: {git.Commits}  files: {git.Files.Count}  coupled-pairs: {git.Coupling.Count}  skipped-large: {git.CommitsSkippedForCoupling}  elapsed: {sw.Elapsed.TotalSeconds:F1}s"
                            : L($"git: 不可用（{git.Error}）", $"git: unavailable ({git.Error})"));
                    }
                }
                catch (Exception ex) { stderr.WriteLine(L($"提示：git 统计失败：{ex.Message}", $"Note: git statistics failed: {ex.Message}")); }
            }

            // 入口识别与路由匹配：依赖上面写好的符号、边和容器；失败不影响索引结果
            try
            {
                var post = steps.Entries();
                stdout.WriteLine(FormatEntries(post) + (post.Skipped ? L("  （输入没变，沿用）", "  (inputs unchanged, reused)") : ""));
                stdout.WriteLine(post.FormatRules());
            }
            catch (Exception ex) { stderr.WriteLine(L($"提示：入口识别失败：{ex.Message}", $"Note: entry point detection failed: {ex.Message}")); }
            stdout.WriteLine($"post timings: {PhaseTimings.Format(postTimings.Items)}");
            return exit;
        }
        finally
        {
            foreach (var a in adapters.OfType<IDisposable>()) a.Dispose();
        }
    }

    internal static string FormatEntries(CodeAtlas.Server.PostProcessReport post) =>
        $"entries: {post.Entries}  ({string.Join("  ", post.EntriesByKind.Select(kv => $"{kv.Key}: {kv.Value}"))})  route-matches: {post.Matches}  elapsed: {post.Elapsed.TotalSeconds:F1}s";

    /// <summary>
    /// required 为 false（auto）时缺 worker / 解决方案只提示不报错。
    /// 没给 --sln 时主根、外部根、被纳入的嵌套仓库各找一个解决方案，依次跑；部分失败打印原因、退出码 1，其余照常写回。
    /// 不带 --full 时只跑待 Roslyn 的文件（改过的、依赖它们的、新文件），没有就跳过。
    /// </summary>
    private static int RunRoslyn(IndexDatabase db, CodeAtlas.Core.Workspace.ProjectRoots roots, string? slnArg, bool required, bool full, TextWriter stdout, TextWriter stderr)
    {
        var pending = full ? null : new IndexReader(db).GetRoslynPendingFiles();
        // auto：没有待更新的就不必找 worker；on：照常检查 worker 与解决方案（缺了要报错），之后再跳过
        if (pending is { Count: 0 } && !required) return SkipNothingPending(stdout);
        var worker = RoslynRunner.FindWorker();
        if (worker is null)
        {
            stderr.WriteLine(L($"{(required ? "错误" : "提示")}：没有找到 {RoslynRunner.WorkerName}（应与 codeatlas 同目录，或用环境变量 {RoslynRunner.WorkerPathEnv} 指定），跳过 Roslyn 精确语义。", $"{(required ? "Error" : "Note")}: {RoslynRunner.WorkerName} not found (it should be in the same directory as codeatlas, or set the environment variable {RoslynRunner.WorkerPathEnv}); skipping Roslyn precise semantics."));
            return required ? 1 : 0;
        }
        IReadOnlyList<string> solutions = slnArg is not null ? [Path.GetFullPath(slnArg)] : RoslynRunner.DiscoverSolutions(roots);
        var bad = solutions.Count == 0 ? null : solutions.FirstOrDefault(s => !File.Exists(s));
        if (solutions.Count == 0 || bad is not null)
        {
            stderr.WriteLine(L($"{(required ? "错误" : "提示")}：{(bad is null ? "仓库里没有找到 .sln/.slnx" : $"解决方案不存在：{bad}")}，跳过 Roslyn 精确语义。", $"{(required ? "Error" : "Note")}: {(bad is null ? "no .sln/.slnx found in the repository" : $"solution does not exist: {bad}")}; skipping Roslyn precise semantics."));
            return required ? 1 : 0;
        }

        if (RoslynRunner.ProbeAsync(worker).GetAwaiter().GetResult() is { } unavailable)
        {
            stderr.WriteLine(L($"{(required ? "错误" : "提示")}：{unavailable}", $"{(required ? "Error" : "Note")}: {unavailable}"));
            return required ? 1 : 0;
        }
        if (pending is { Count: 0 }) return SkipNothingPending(stdout);
        if (pending is not null) stderr.WriteLine(L($"[roslyn] 增量：{pending.Count} 个待更新的 C# 文件", $"[roslyn] incremental: {Plural(pending.Count, "C# file", "C# files")} to update"));
        else foreach (var sln in solutions) stderr.WriteLine($"[roslyn] {sln}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var r = RoslynRunner.RunAndApplyAsync(db, worker, solutions, roots,
                p => { if (p.Done % 200 == 0 || p.Done == p.Total) stderr.WriteLine($"  {p.Done}/{p.Total}"); },
                line => stderr.WriteLine("  " + line), CancellationToken.None, pendingFiles: pending).GetAwaiter().GetResult();
            var failed = r.FailedSolutions ?? [];
            stdout.WriteLine($"roslyn: {(r.Incremental ? "incremental" : "full")}  solutions: {r.Solutions - failed.Count}/{r.Solutions}  files: {r.Files}  misses: {r.Misses}  skipped: {r.FilesSkipped}  merged: {r.SymbolsMerged}  added: {r.SymbolsAdded}  edges: {r.Edges}  di: {r.DiRegistrations}  errors: {r.Errors}  kept: {r.HeuristicKept}  kept-types: {r.HeuristicTypeEdgesKept}  elapsed: {sw.Elapsed.TotalSeconds:F1}s");
            stdout.WriteLine($"roslyn timings: {PhaseTimings.Format(r.Timings ?? [])}");
            foreach (var f in failed) stderr.WriteLine(L($"错误：解决方案失败 {f}", $"Error: solution failed {f}"));
            return failed.Count > 0 ? 1 : 0;
        }
        catch (RoslynWorkerException ex)
        {
            stderr.WriteLine(L($"错误：{ex.Message}", $"Error: {ex.Message}"));
            return 1;
        }
    }

    private static int SkipNothingPending(TextWriter stdout)
    {
        stdout.WriteLine(L("roslyn: 没有待更新的 C# 文件，跳过", "roslyn: no C# files to update, skipped"));
        return 0;
    }

    private static void WarnIfNotIgnored(string repo, TextWriter stderr)
    {
        var gitignore = Path.Combine(repo, ".gitignore");
        if (!Directory.Exists(Path.Combine(repo, ".git")) && !File.Exists(gitignore)) return;
        var lines = File.Exists(gitignore) ? File.ReadAllLines(gitignore) : [];
        if (lines.Any(l => l.Trim() is ".codeatlas" or ".codeatlas/" or "/.codeatlas" or "/.codeatlas/")) return;
        stderr.WriteLine(L("提示：建议把 .codeatlas/ 加入该仓库的 .gitignore（索引库不应提交）。", "Note: consider adding .codeatlas/ to this repository's .gitignore (the index database should not be committed)."));
    }
}
