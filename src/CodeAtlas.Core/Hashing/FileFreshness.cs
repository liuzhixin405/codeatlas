using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Hashing;

/// <summary>
/// 磁盘上的文件和库里的文件记录是否一致。增量索引（TreeSitterIndexer）、启动对账（DiskDrift）与视图的过期检查共用这一套判定，
/// 免得三处口径不一（比如过期提示说"改过"，增量索引却认为没变而不重建）。
/// </summary>
public static class FileFreshness
{
    /// <summary>只看 stat：记录是 Ok、mtime 已知且与磁盘一致、大小一致 → 没变，不必读文件。mtime 为 0（未知）时不算一致。</summary>
    public static bool SameByStat(FileRecord record, long size, long mtimeTicks) =>
        record is { Status: FileStatus.Ok, Mtime: { } m } && mtimeTicks != 0 && m == mtimeTicks && record.Size == size;
}
