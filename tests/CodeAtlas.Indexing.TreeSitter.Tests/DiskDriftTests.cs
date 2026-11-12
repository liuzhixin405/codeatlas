using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>启动对账：索引后磁盘上的增 / 改 / 删能被 stat 比对认出来，跳过目录里的不算；判定与增量索引一致。</summary>
public class DiskDriftTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-drift-" + Guid.NewGuid().ToString("N"));
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();
    private readonly List<ILanguageAdapter> _adapters = [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter()];

    public DiskDriftTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        Write("src/a.ts", "export function a(): number { return 1; }\n");
        Write("src/b.ts", "export function b(): number { return 2; }\n");
        Write("src/c.ts", "export function c(): number { return 3; }\n");
    }

    private void Write(string rel, string text)
    {
        var full = Path.Combine(_repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private Task IndexAsync() => new TreeSitterIndexer(_db, _adapters, new HeuristicEdgeResolver()).IndexAsync(_repo);

    private DiskDrift Detect() => DiskDrift.Detect(ProjectRoots.Single(_repo), new IndexReader(_db).GetAllFiles(), _adapters);

    [Fact]
    public async Task NothingChanged_NoDrift()
    {
        await IndexAsync();
        var drift = Detect();
        Assert.Equal(0, drift.Count);
    }

    [Fact]
    public async Task AddedChangedRemoved_AreReported_ExcludedDirectoriesIgnored()
    {
        await IndexAsync();
        Write("src/a.ts", "export function a(): number { return 100; }\n"); // 大小变了
        Write("src/new.ts", "export function n(): void {}\n");
        File.Delete(Path.Combine(_repo, "src", "c.ts"));
        Write("node_modules/pkg/index.js", "module.exports = 1;\n");   // 跳过目录
        Write("dist/out.js", "export const x = 1;\n");
        Write("README.md", "不是源码\n");                                // 不受支持的扩展名

        var drift = Detect();

        Assert.Equal(["src/new.ts"], drift.Added);
        Assert.Equal(["src/a.ts"], drift.Changed);
        Assert.Equal(["src/c.ts"], drift.Removed);
        Assert.Equal(3, drift.Count);
    }

    [Fact]
    public async Task OnlyMtimeChanged_IsDrift_ThenIncrementalIndexBackfillsAndDriftClears()
    {
        await IndexAsync();
        var b = Path.Combine(_repo, "src", "b.ts");
        File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(b).AddMinutes(5)); // git checkout 常见：内容没变、mtime 变了

        Assert.Equal(["src/b.ts"], Detect().Changed);
        var report = await new TreeSitterIndexer(_db, _adapters, new HeuristicEdgeResolver()).IndexAsync(_repo);
        Assert.Equal(0, report.FilesParsed);     // 增量索引算哈希发现没变，不重解析……
        Assert.Equal(0, Detect().Count);         // ……但补写了 mtime，下次对账不再报
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var a in _adapters.OfType<IDisposable>()) a.Dispose();
        try { Directory.Delete(_repo, recursive: true); } catch { /* 临时目录 */ }
    }
}
