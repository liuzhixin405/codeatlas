using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 视图结果带过期文件列表（StaleFiles）：涉及的文件在上次索引后改过才报，只报涉及的；stat 不一致而内容没变的不报。
/// 每个用例自己建临时仓库（要改文件，不能用共享的 ServerFixture）。
/// </summary>
public sealed class StaleFilesTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-stale-files-" + Guid.NewGuid().ToString("N"));
    private string DbPath => ServerOptions.DefaultDbPath(_repo);

    public StaleFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        Write("src/svc.ts", "export class Svc {\n  find(id: number): string { return String(id); }\n}\n");
        Write("src/app.ts", "import { Svc } from './svc';\nexport function main(): void { new Svc().find(1); }\n");
        Write("src/other.ts", "export function other(): number { return 1; }\n");
    }

    private void Write(string rel, string text) => File.WriteAllText(Path.Combine(_repo, rel), text);

    private async Task IndexOfflineAsync()
    {
        using var db = IndexDatabase.Open(DbPath);
        List<ILanguageAdapter> adapters = [.. LanguageAdapters.Create()]; // 与服务同一套语言，免得对账把别的语言当新增
        try { await new TreeSitterIndexer(db, adapters, new HeuristicEdgeResolver()).IndexAsync(_repo); }
        finally { foreach (var a in adapters.OfType<IDisposable>()) a.Dispose(); }
    }

    private IndexService NewService() => new(new ServerOptions(_repo, DbPath) { Roslyn = false });

    private static long IdOf(IndexService s, string qualifiedName) => s.Search(qualifiedName.Split(':', '.')[^1], 50).Single(x => x.QualifiedName == qualifiedName).Id;

    [Fact]
    public async Task Views_HaveNoStaleFiles_WhenDiskMatchesIndex()
    {
        await IndexOfflineAsync();
        using var s = NewService();
        var main = IdOf(s, "src/app.ts:main");
        Assert.Null(s.Outline(main, new OutlineOptions(true, false))!.StaleFiles);
        Assert.Null(s.SymbolFlow(main, null)!.StaleFiles);
        Assert.Null(s.Impact(IdOf(s, "src/svc.ts:Svc.find"), new ImpactOptions(true, false))!.StaleFiles);
    }

    [Fact]
    public async Task Views_ListOnlyTheirOwnChangedFiles()
    {
        await IndexOfflineAsync();
        using var s = NewService();
        var main = IdOf(s, "src/app.ts:main");
        var find = IdOf(s, "src/svc.ts:Svc.find");
        Write("src/app.ts", "import { Svc } from './svc';\nexport function main(): void { new Svc().find(2); /* 改了 */ }\n");
        Write("src/other.ts", "export function other(): number { return 2222; }\n"); // 和这些视图无关

        Assert.Equal(["src/app.ts"], s.Outline(main, new OutlineOptions(true, false))!.StaleFiles);
        Assert.Equal(["src/app.ts"], s.SymbolFlow(main, null)!.StaleFiles);
        // 影响范围：find 的调用方 main 在 app.ts 里
        Assert.Equal(["src/app.ts"], s.Impact(find, new ImpactOptions(true, false))!.StaleFiles);
        // 调用图有缓存：缓存的是索引结果，过期与否每次现查——改回去以后同一个请求不再报
        Write("src/app.ts", "import { Svc } from './svc';\nexport function main(): void { new Svc().find(1); }\n");
        Assert.Null(s.SymbolFlow(main, null)!.StaleFiles);
    }

    [Fact]
    public async Task CallPaths_FlowCarriesStaleFiles()
    {
        await IndexOfflineAsync();
        using var s = NewService();
        var main = IdOf(s, "src/app.ts:main");
        var find = IdOf(s, "src/svc.ts:Svc.find");
        Assert.Null(s.CallPaths(main, find, null, null)!.Flow.StaleFiles);

        Write("src/app.ts", "import { Svc } from './svc';\nexport function main(): void { new Svc().find(2); /* 改了 */ }\n");
        Assert.Equal(["src/app.ts"], s.CallPaths(main, find, null, null)!.Flow.StaleFiles);
    }

    [Fact]
    public async Task DeletedFile_IsStale()
    {
        await IndexOfflineAsync();
        using var s = NewService();
        var find = IdOf(s, "src/svc.ts:Svc.find");
        File.Delete(Path.Combine(_repo, "src", "svc.ts"));
        Assert.Equal(["src/svc.ts"], s.Outline(find, new OutlineOptions(true, false))!.StaleFiles);
        Assert.Equal(["src/svc.ts"], s.Relations(find, new RelationsOptions(1, false))!.StaleFiles); // 中心 Svc 所在文件没了；app.ts 没动不报
    }

    [Fact]
    public async Task OnlyMtimeChanged_SameContent_IsNotStale()
    {
        await IndexOfflineAsync();
        using var s = NewService();
        var main = IdOf(s, "src/app.ts:main");
        var app = Path.Combine(_repo, "src", "app.ts");
        File.SetLastWriteTimeUtc(app, File.GetLastWriteTimeUtc(app).AddMinutes(3)); // 切分支再切回来：内容一样
        Assert.Null(s.Outline(main, new OutlineOptions(true, false))!.StaleFiles); // stat 不一致时比哈希，哈希一致不报
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_repo, recursive: true); } catch { /* 临时目录 */ }
    }
}
