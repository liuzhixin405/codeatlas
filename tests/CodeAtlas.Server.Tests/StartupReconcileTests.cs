using System.Net.Http.Json;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 启动对账：服务没在跑时磁盘上的改动（扩展没开、AI 助手写盘、git pull），服务起来后和索引对一次账，有差异才排一次增量。
/// </summary>
public sealed class StartupReconcileTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-reconcile-" + Guid.NewGuid().ToString("N"));
    private string DbPath => ServerOptions.DefaultDbPath(_repo);

    public StartupReconcileTests()
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

    [Fact]
    public async Task ServerStart_ReconcilesOfflineChanges_WithOneIncrementalIndex()
    {
        await IndexOfflineAsync();
        // 服务没跑时（扩展没开 / AI 助手直接写盘 / git pull）：改一个、加一个
        Write("src/app.ts", "import { Svc } from './svc';\nexport function main(): void { new Svc().find(3); }\nexport function extra(): void {}\n");
        Write("src/added.ts", "export function added(): void {}\n");

        var status = await StartAndWaitAsync();
        Assert.Null(status.Error);
        Assert.NotNull(status.LastReport);                 // 没人发 /api/index，服务自己补了一次增量
        Assert.Equal(2, status.LastReport!.FilesParsed);    // 只解析改过的与新增的
        Assert.Equal(2, status.LastReport.FilesUnchanged);  // 没变的 svc.ts / other.ts 照增量规则跳过
    }

    [Fact]
    public async Task ServerStart_NoOfflineChanges_DoesNotIndex()
    {
        await IndexOfflineAsync();
        var status = await StartAndWaitAsync();
        Assert.Null(status.Error);
        Assert.Null(status.LastReport); // 对账没有差异：不跑索引
    }

    [Fact]
    public async Task Reconcile_ReportsDrift_AndStatusIsRunningUntilItFinishes()
    {
        await IndexOfflineAsync();
        File.Delete(Path.Combine(_repo, "src", "other.ts"));
        using var s = NewService();
        Assert.True(s.TryStartReconcile());
        await s.WaitForIndexAsync();
        Assert.Equal(["src/other.ts"], s.LastDrift!.Removed);
        Assert.Equal(1, s.Status().LastReport!.FilesRemoved);
        Assert.False(s.Status().Running);
    }

    /// <summary>真起一个服务（ServerHost），轮询到对账与可能的索引都结束。</summary>
    private async Task<IndexStatusDto> StartAndWaitAsync()
    {
        var app = ServerHost.Build(new ServerOptions(_repo, DbPath, Port: 0) { Narration = new Narration.NarrationOptions("http://127.0.0.1:1/v1", "m", Enabled: false), Roslyn = false });
        await app.StartAsync();
        try
        {
            using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            IndexStatusDto status;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            do
            {
                await Task.Delay(100);
                status = (await c.GetFromJsonAsync<IndexStatusDto>("/api/index/status"))!;
            } while (status.Running && DateTime.UtcNow < deadline);
            return status;
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_repo, recursive: true); } catch { /* 临时目录 */ }
    }
}
