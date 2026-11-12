using CodeAtlas.Core.Architecture;

namespace CodeAtlas.Server;

/// <summary>
/// 影响分析的 git 取数（复用 <see cref="GitHistory.Git"/> 起进程）：
/// <list type="number">
/// <item>base 与 HEAD 的 merge-base（PR 语义：只看这个分支自己改了什么，base 分支后来的提交不算）；base = HEAD 时就是 HEAD。</item>
/// <item><c>git diff -U0 -M --relative &lt;merge-base&gt;</c>：merge-base → <b>工作区</b>（含暂存与未暂存），与索引反映的工作区一致，新侧行号就是索引里的行号。
/// --relative：仓库参数是 git 仓库的子目录时，路径相对这个子目录、目录外的改动不出现（与索引的相对路径一致）。</item>
/// <item><c>git ls-files --others --exclude-standard</c>：未跟踪的新文件（git diff 不列它们，AI 新加的文件常常还没 add）按新增处理。</item>
/// </list>
/// </summary>
public static class AffectedGit
{
    /// <summary>
    /// 默认 base：HEAD——"工作区里还没提交的改动"。索引反映的是工作区，HEAD 是唯一不用猜分支名（main / master / origin/…）就总是存在的基准；
    /// 审 PR / CI 里显式给 --base origin/main（取 merge-base，等于这个分支相对主干的全部改动 + 未提交的改动）。
    /// </summary>
    public const string DefaultBase = "HEAD";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public sealed record Result(IReadOnlyList<FileChange> Changes, string? MergeBase, string? Error);

    public static Result Collect(string repoDir, string baseRef)
    {
        if (!Directory.Exists(repoDir)) return Fail(L($"目录不存在：{repoDir}", $"Directory does not exist: {repoDir}"));
        if (!IsSafeRef(baseRef)) return Fail(L($"base 不是合法的 git ref：{baseRef}", $"base is not a valid git ref: {baseRef}"));

        var (inside, err) = GitHistory.Git(repoDir, ["rev-parse", "--is-inside-work-tree"], Timeout);
        if (err is not null) return Fail(GitError(err, repoDir));
        if (inside.Trim() != "true") return Fail(L($"不是 git 工作区：{repoDir}", $"Not a git work tree: {repoDir}"));

        var (commit, refErr) = GitHistory.Git(repoDir, ["rev-parse", "--verify", "--quiet", "--end-of-options", baseRef + "^{commit}"], Timeout);
        if (refErr is not null || commit.Trim().Length == 0)
            return Fail(baseRef == "HEAD"
                ? L("仓库还没有任何提交（HEAD 不存在），没有可比较的基准", "The repository has no commits yet (HEAD does not exist); nothing to compare against")
                : L($"找不到 base：{baseRef}（分支 / 标签 / 提交都不是；远端分支先 git fetch）", $"base not found: {baseRef} (not a branch / tag / commit; fetch remote branches first)"));
        var target = commit.Trim();

        // merge-base 失败（无关历史、浅克隆缺祖先）时退回直接和 base 比，结果里 MergeBase 照写实际比较的提交
        var (mb, mbErr) = GitHistory.Git(repoDir, ["merge-base", target, "HEAD"], Timeout);
        if (mbErr is null && mb.Trim().Length > 0) target = mb.Trim();

        var (diff, diffErr) = GitHistory.Git(repoDir,
            ["-c", "core.quotepath=false", "diff", "-U0", "--no-color", "--no-ext-diff", "-M", "--relative", "--src-prefix=a/", "--dst-prefix=b/", target, "--"], Timeout);
        if (diffErr is not null) return Fail(GitError(diffErr, repoDir));
        var changes = AffectedDiff.ParseDiff(diff);

        var (untracked, untrackedErr) = GitHistory.Git(repoDir, ["-c", "core.quotepath=false", "ls-files", "--others", "--exclude-standard"], Timeout);
        if (untrackedErr is null)
        {
            var known = changes.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var line in untracked.Split('\n'))
            {
                var p = AffectedDiff.Normalize(line.TrimEnd('\r'));
                if (p.Length > 0 && known.Add(p)) changes.Add(new FileChange(p, null, DiffStatus.Added, null));
            }
        }
        // 工具自己的索引目录（.codeatlas/index.db 等）不算改动
        changes.RemoveAll(c => c.Path.StartsWith(".codeatlas/", StringComparison.OrdinalIgnoreCase));
        return new Result(changes, target, null);
    }

    /// <summary>
    /// --stdin 的路径修正：git diff 默认相对 git 仓库根打路径，仓库参数是它的子目录时去掉子目录前缀（git rev-parse --show-prefix）。
    /// git 不可用或不是仓库时原样返回（stdin 模式本来就不依赖 git）。
    /// </summary>
    public static IReadOnlyList<FileChange> RelativeToRepo(string repoDir, IReadOnlyList<FileChange> changes)
    {
        if (!Directory.Exists(repoDir)) return changes;
        var (prefix, err) = GitHistory.Git(repoDir, ["rev-parse", "--show-prefix"], TimeSpan.FromSeconds(30));
        var p = prefix.Trim();
        if (err is not null || p.Length == 0) return changes;
        string Strip(string path) => path.StartsWith(p, StringComparison.Ordinal) ? path[p.Length..] : path;
        return changes.Select(c => c with { Path = Strip(c.Path), OldPath = c.OldPath is null ? null : Strip(c.OldPath) }).ToList();
    }

    /// <summary>不以 - 开头（不会被当成选项）、没有空白与控制字符、不含 ..（范围写法请只给 base）。</summary>
    internal static bool IsSafeRef(string r) =>
        r.Length > 0 && r[0] != '-' && !r.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) && !r.Contains("..", StringComparison.Ordinal);

    private static string GitError(string err, string repoDir) =>
        err.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
            ? L($"不是 git 仓库：{repoDir}（没有 git 时可以用 --stdin 喂 diff 或文件名列表）", $"Not a git repository: {repoDir} (without git, feed a diff or file list with --stdin)")
            : L($"调用 git 失败：{err}（没有 git 时可以用 --stdin 喂 diff 或文件名列表）", $"git failed: {err} (without git, feed a diff or file list with --stdin)");

    private static Result Fail(string error) => new([], null, error);
}
