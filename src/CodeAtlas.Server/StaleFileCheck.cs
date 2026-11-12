using CodeAtlas.Core.Hashing;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// 视图结果的过期检查：调用图 / 大纲 / 影响 / 模块关系涉及的文件，磁盘状态和索引记录还对不对得上。
/// 结果放进各 DTO 的可选字段 StaleFiles（没有过期文件时为 null，旧页面忽略它即可）。
/// 代价控制：只查视图里出现的文件（最多 <see cref="MaxPaths"/> 个）；先比 stat（大小 + mtime，同增量索引的 <see cref="FileFreshness.SameByStat"/>），
/// 大小相同而 mtime 不同（git checkout / touch 常见）时才读文件算哈希，每次请求最多 <see cref="MaxHashes"/> 个，超出的保守地算过期。
/// </summary>
/// <summary>磁盘文件相对索引记录的状态。除 Fresh 外都算过期。</summary>
public enum Freshness
{
    /// <summary>与索引一致（stat 一致，或只有 mtime 变了而内容哈希相同）。</summary>
    Fresh,
    /// <summary>文件已删除或移动。</summary>
    Missing,
    /// <summary>大小变了。</summary>
    SizeChanged,
    /// <summary>大小相同、mtime 不同，算哈希确认内容变了。</summary>
    ContentChanged,
    /// <summary>大小相同、mtime 不同，哈希预算用完或读不到文件，没核对内容，保守地算过期。</summary>
    Unverified,
}

public static class StaleFileCheck
{
    public const int MaxPaths = 500;
    public const int MaxHashes = 32;

    /// <summary>records 是视图涉及文件的索引记录（不在索引里的文件不查：它们不是这份结果的依据）；返回过期文件的相对路径（按序），没有为 null。</summary>
    public static IReadOnlyList<string>? Check(IEnumerable<FileRecord> records, string mainRoot)
    {
        var hashBudget = MaxHashes;
        var stale = new List<string>();
        foreach (var record in records)
        {
            string full;
            try { full = Path.GetFullPath(Path.Combine(mainRoot, record.Path)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (IsStale(record, full, ref hashBudget)) stale.Add(record.Path);
        }
        if (stale.Count == 0) return null;
        stale.Sort(StringComparer.Ordinal);
        return stale;
    }

    /// <summary>
    /// 一个文件：不存在 / 大小变了 → 过期；stat 一致 → 没过期；大小相同 mtime 不同 → Ok 的记录比哈希（预算用完算过期），
    /// 跳过（超大）与解析失败的记录没有可比的哈希，大小没变就不报。
    /// </summary>
    internal static bool IsStale(FileRecord record, string fullPath, ref int hashBudget) =>
        Classify(record, fullPath, ref hashBudget) != Freshness.Fresh;

    /// <summary>
    /// 一个文件和索引记录的关系，带原因（MCP 的过期警告要告诉 agent 为什么）。判定口径只有这一份：视图的 StaleFiles 与 MCP 共用。
    /// stat 读不到（权限等）按 Fresh 处理，不打扰；哈希读不到按 Unverified（算过期）。
    /// </summary>
    public static Freshness Classify(FileRecord record, string fullPath, ref int hashBudget)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return Freshness.Missing;
            if (info.Length != record.Size) return Freshness.SizeChanged;
            if (FileFreshness.SameByStat(record, info.Length, info.LastWriteTimeUtc.Ticks)) return Freshness.Fresh;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Freshness.Fresh; }
        if (record.Status != FileStatus.Ok) return Freshness.Fresh;
        if (hashBudget <= 0) return Freshness.Unverified;
        hashBudget--;
        try { return FileHasher.Sha256Hex(File.ReadAllBytes(fullPath)) != record.Hash ? Freshness.ContentChanged : Freshness.Fresh; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Freshness.Unverified; }
    }

    // ---- 各视图涉及哪些文件 ----

    /// <summary>时序树每个节点的调用处文件（根是定义所在文件）：调用边就记在这些文件里。</summary>
    public static IEnumerable<string?> PathsOf(FlowDto flow)
    {
        var stack = new Stack<FlowNodeDto>();
        stack.Push(flow.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n.Path;
            foreach (var c in n.Children) stack.Push(c);
        }
    }

    /// <summary>方法自己的文件（每一步都是它里面的调用）+ 各步目标所在文件。</summary>
    public static IEnumerable<string?> PathsOf(OutlineDto outline)
    {
        yield return outline.Symbol.Path;
        var stack = new Stack<OutlineStepDto>(outline.Steps);
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            yield return s.Target?.Path;
            foreach (var c in s.Children) stack.Push(c);
        }
    }

    /// <summary>符号自己的文件 + 调用方文件（调用处在那里）+ 受影响入口的文件。</summary>
    public static IEnumerable<string?> PathsOf(ImpactDto impact) =>
        new[] { impact.Symbol.Path }
            .Concat(impact.Callers.Select(c => c.Caller.Path))
            .Concat(impact.Entries?.Select(e => e.Entry.File) ?? []);

    /// <summary>中心与各单元（仓库外的除外）所在文件 + 边的示例所在文件。</summary>
    public static IEnumerable<string?> PathsOf(RelationsDto relations) =>
        new[] { relations.Focus.Path }
            .Concat(relations.Nodes.Where(n => !n.External).Select(n => n.Unit.Path))
            .Concat(relations.Edges.SelectMany(e => e.Samples.Select(s => s.File)));
}
