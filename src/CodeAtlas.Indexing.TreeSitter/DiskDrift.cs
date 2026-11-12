using CodeAtlas.Core.Hashing;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Indexing.TreeSitter;

/// <summary>
/// 磁盘与索引的差异（启动对账用）：只扫目录、比 stat，不读文件、不算哈希。
/// 判定与增量索引一致（<see cref="FileFreshness.SameByStat"/>、同一个 <see cref="RepositoryScanner"/> 与跳过目录），
/// 所以这里报"有差异"时，接着跑的增量索引一定会去看这些文件；内容其实没变的（只是 mtime 变了）由增量索引算哈希后补写 mtime。
/// </summary>
/// <param name="Added">磁盘上有、库里没有。</param>
/// <param name="Changed">大小或 mtime 和库里不一致。</param>
/// <param name="Removed">库里有、磁盘上没有（不存在的外部根下的不算，和增量索引的删除检测一致）。</param>
public sealed record DiskDrift(IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed)
{
    public int Count => Added.Count + Changed.Count + Removed.Count;

    /// <param name="existing">库里的文件记录（IndexReader.GetAllFiles）；调用方在读锁里取好，扫盘时不占锁。</param>
    public static DiskDrift Detect(ProjectRoots roots, IReadOnlyList<FileRecord> existing, IReadOnlyList<ILanguageAdapter> adapters, IndexOptions? options = null)
    {
        options ??= new IndexOptions();
        var extensionToLanguage = TreeSitterIndexer.ExtensionMap(adapters);
        var byPath = existing.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<string> added = [], changed = [];
        foreach (var file in new RepositoryScanner(extensionToLanguage, options.ExcludedDirectories).Scan(roots))
        {
            seen.Add(file.RelativePath);
            if (!byPath.TryGetValue(file.RelativePath, out var old)) { added.Add(file.RelativePath); continue; }
            if (!SameAsRecord(old, file, options.MaxFileBytes)) changed.Add(file.RelativePath);
        }
        var removed = existing.Where(f => !seen.Contains(f.Path) && !roots.IsUnderMissingRoot(f.Path)).Select(f => f.Path).ToList();
        return new DiskDrift(added, changed, removed);
    }

    /// <summary>
    /// 超过大小上限的：上次也是 Skipped 且大小没变才算一致（同增量索引）；
    /// 上次解析失败的：大小没变就不算差异——增量索引每次都会重试它们，没必要为它们在每次启动时专门跑一次。
    /// </summary>
    private static bool SameAsRecord(FileRecord old, ScannedFile file, long maxFileBytes)
    {
        if (file.Size > maxFileBytes) return old.Status == FileStatus.Skipped && old.Size == file.Size;
        if (old.Status == FileStatus.ParseFailed) return old.Size == file.Size;
        return FileFreshness.SameByStat(old, file.Size, file.Mtime);
    }
}
