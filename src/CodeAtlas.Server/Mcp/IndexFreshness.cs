using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Mcp;

/// <summary>一个与索引记录对不上的文件。Reason 是给人 / agent 看的原因（已删除、大小变了、内容变了、未核对）。</summary>
public sealed record StaleFile(string Path, string Reason);

/// <summary>
/// MCP 的过期警告：判定交给 <see cref="StaleFileCheck.Classify"/>（与视图的 StaleFiles、增量索引同一口径——
/// 只有 mtime 变了而内容哈希相同的不算过期），这里只把结果翻成给 agent 看的原因。
/// 每次 <see cref="CheckAll"/> 最多算 <see cref="StaleFileCheck.MaxHashes"/> 个哈希，超出的报"未核对"。
/// </summary>
public static class IndexFreshness
{
    /// <summary>
    /// 一个索引文件是否过期；没过期返回 null。<paramref name="repoRoot"/> 是主根（库里的路径相对它，外部根的是 ../other/...）。
    /// </summary>
    public static StaleFile? Check(string repoRoot, FileRecord file, ref int hashBudget)
    {
        if (file.Path == Core.Storage.IndexWriter.ExternalFilePath) return null;
        var full = FullPath(repoRoot, file.Path);
        return StaleFileCheck.Classify(file, full, ref hashBudget) switch
        {
            Freshness.Fresh => null,
            Freshness.Missing => new StaleFile(file.Path, L("文件已删除或移动", "file deleted or moved")),
            Freshness.SizeChanged => SizeChanged(file, full),
            Freshness.ContentChanged => new StaleFile(file.Path, L($"内容变了（大小相同，修改时间 {Times(file, full)}）", $"content changed (same size, modified time {Times(file, full)})")),
            _ => new StaleFile(file.Path, L($"修改时间变了，内容未核对（{Times(file, full)}）", $"modified time changed, content not verified ({Times(file, full)})")),
        };
    }

    /// <summary>一组文件里过期的那些（按路径排）。</summary>
    public static List<StaleFile> CheckAll(string repoRoot, IEnumerable<FileRecord> files)
    {
        var budget = StaleFileCheck.MaxHashes;
        var stale = new List<StaleFile>();
        foreach (var f in files)
            if (Check(repoRoot, f, ref budget) is { } s) stale.Add(s);
        stale.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        return stale;
    }

    /// <summary>库里的相对路径（'/' 分隔，可能以 ../ 开头）→ 磁盘绝对路径。</summary>
    public static string FullPath(string repoRoot, string relativePath) =>
        Path.GetFullPath(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static StaleFile SizeChanged(FileRecord file, string full)
    {
        long length;
        try { length = new FileInfo(full).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { length = -1; }
        return new StaleFile(file.Path, L($"大小 {file.Size} → {length} 字节", $"size {file.Size} → {length} bytes"));
    }

    private static string Times(FileRecord file, string full)
    {
        string disk;
        try { disk = Format(new FileInfo(full).LastWriteTimeUtc.Ticks); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { disk = "?"; }
        var index = file.Mtime is { } m and not 0 ? Format(m) : "?";
        return L($"索引 {index}，磁盘 {disk}", $"index {index}, disk {disk}");
    }

    private static string Format(long ticks) => new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss") + "Z";
}
