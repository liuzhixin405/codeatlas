using System.Net;
using System.Net.Http.Json;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Server.Tests;

/// <summary>GET /api/affected：临时 git 仓库，提交后改一行，服务自己跑 git diff。没有 git 时跳过。</summary>
public sealed class AffectedApiTests : IAsyncLifetime
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-affected-api-" + Guid.NewGuid().ToString("N"));
    private Microsoft.AspNetCore.Builder.WebApplication? _app;
    private HttpClient _client = null!;
    private bool _git;

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_repo, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private bool Git(params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" }.Concat(args)) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public async Task InitializeAsync()
    {
        Write("src/util.ts", "export function helper(id: number): string {\n  return String(id);\n}\n");
        Write("src/svc.ts", "import { helper } from './util';\nexport class Svc {\n  find(id: number): string {\n    return helper(id);\n  }\n}\n");
        Write("tests/svc.test.ts", "import { Svc } from '../src/svc';\nexport function testFind(): void {\n  new Svc().find(1);\n}\n");
        Write(".gitignore", ".codeatlas/\n");
        _git = Git("init", "-q", "-b", "main") && Git("add", "-A") && Git("commit", "-q", "-m", "init");
        Write("src/svc.ts", "import { helper } from './util';\nexport class Svc {\n  find(id: number): string {\n    return helper(id * 2);\n  }\n}\n");

        var db = ServerOptions.DefaultDbPath(_repo);
        using (var d = IndexDatabase.Open(db))
        {
            List<ILanguageAdapter> adapters = [new TypeScriptAdapter()];
            await new TreeSitterIndexer(d, adapters, new HeuristicEdgeResolver()).IndexAsync(_repo);
            foreach (var a in adapters.OfType<IDisposable>()) a.Dispose();
        }
        _app = ServerHost.Build(new ServerOptions(_repo, db, Port: 0) { Narration = new Narration.NarrationOptions("http://127.0.0.1:1/v1", "fake-model") });
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    [Fact]
    public async Task Affected_DefaultBase_MapsChangedLineToMethod_AndReturnsItsTestFile()
    {
        if (!_git) return;
        var r = await _client.GetFromJsonAsync<AffectedDto>("/api/affected");
        Assert.Equal(("git", "HEAD"), (r!.Input, r.Base));
        Assert.NotNull(r.MergeBase);
        var s = Assert.Single(r.Symbols);
        Assert.Equal(("Svc.find", "lines"), (s.Label, s.Reason));
        Assert.Equal([4], s.Lines);
        Assert.Equal(["testFind"], r.Tests.Select(t => t.Label));
        Assert.Equal(["tests/svc.test.ts"], r.TestFiles);
    }

    [Fact]
    public async Task Affected_UnknownBase_Is400WithError()
    {
        if (!_git) return;
        var r = await _client.GetAsync("/api/affected?base=no-such-ref");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("no-such-ref", await r.Content.ReadAsStringAsync());
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        if (!Directory.Exists(_repo)) return;
        foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { }
    }
}
