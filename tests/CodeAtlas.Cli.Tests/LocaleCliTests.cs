namespace CodeAtlas.Cli.Tests;

/// <summary>--lang en：用法、错误与命令输出走英文。Program.Run 设了全局语言不还原，所以每个用例先 Loc.Use("zh") 兜底还原。</summary>
[Collection("Locale")]
public class LocaleCliTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-cli-locale-" + Guid.NewGuid().ToString("N"));

    public LocaleCliTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src", "util"));
        File.WriteAllText(Path.Combine(_repo, "src", "util", "format.ts"), "export function helper(id: number): string { return String(id); }\n");
        File.WriteAllText(Path.Combine(_repo, "src", "svc.ts"), "import { helper } from './util/format';\nexport class Svc {\n  find(id: number): string { return helper(id); }\n}\n");
        File.WriteAllText(Path.Combine(_repo, "src", "app.ts"), "import { Svc } from './svc';\nexport function main(): void { new Svc().find(1); fetch('x'); }\n");
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Program.Run(args, stdout, stderr);
        return (code, stdout.ToString().Replace("\r", ""), stderr.ToString().Replace("\r", ""));
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void NoArgs_PrintsEnglishUsage_WithoutChinese()
    {
        using var _ = Loc.Use("zh");
        var r = Run("--lang", "en");
        Assert.Equal(2, r.Code);
        Assert.Contains("Usage:", r.Err);
        Assert.Contains("codeatlas index  <repo>", r.Err);
        Assert.Contains("[--project <external root> …]", r.Err);
        Assert.DoesNotMatch("[一-鿿]", r.Err);
    }

    [Fact]
    public void NoArgs_DefaultIsStillChineseUsage()
    {
        using var _ = Loc.Use("zh");
        var r = Run();
        Assert.Equal(2, r.Code);
        Assert.Contains("用法：", r.Err);
    }

    [Fact]
    public void UnknownCommand_PrintsEnglishError()
    {
        using var _ = Loc.Use("zh");
        var r = Run("frobnicate", "--lang", "en");
        Assert.Equal(2, r.Code);
        Assert.StartsWith("Unknown command: frobnicate\n", r.Err);
        Assert.DoesNotMatch("[一-鿿]", r.Err);
    }

    [Fact]
    public void Index_ThenQuery_PrintEnglishText()
    {
        using var _ = Loc.Use("zh");
        var index = Run("--lang", "en", "index", _repo);
        Assert.Equal(0, index.Code);
        Assert.Contains("files: 3", index.Out);
        // TS-only 仓库：没有待 Roslyn 的 C# 文件
        Assert.Contains("roslyn: no C# files to update, skipped", index.Out);

        var missing = Run("query", _repo, "doesNotExist", "--lang", "en");
        Assert.Equal(1, missing.Code);
        Assert.Equal("Symbol not found: doesNotExist\n", missing.Err);

        var flow = Run("query", _repo, "src/app.ts:main", "--flow", "--lang", "en");
        Assert.Equal(0, flow.Code);
        var first = Lines(flow.Out)[0];
        Assert.StartsWith("Start main  container (not in a container)  nodes 4", first);
        Assert.StartsWith("Participants (", Lines(flow.Out)[1]);
    }

    [Fact]
    public void Query_Impact_PrintsEnglishHeadings()
    {
        using var _ = Loc.Use("zh");
        var api = Path.Combine(_repo, "api");
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "package.json"), "{ \"name\": \"api\", \"dependencies\": { \"express\": \"4.19.0\" } }");
        File.WriteAllText(Path.Combine(api, "Dockerfile"), "FROM node:20\nEXPOSE 3000\n");
        File.WriteAllText(Path.Combine(api, "app.js"), string.Join("\n",
            "const express = require('express');",
            "const app = express();",
            "app.post('/api/orders', createOrder);",
            "function createOrder(req, res) { save(req.body); }",
            "function save(o) { audit(o); retry(o); }",
            "function retry(o) { if (o) save(o); }",
            "function audit(o) { return o; }",
            "app.listen(3000);") + "\n");
        Assert.Equal(0, Run("--lang", "en", "index", _repo).Code);

        var r = Run("--lang", "en", "query", _repo, "audit", "--impact");
        Assert.Equal(0, r.Code);
        var lines = Lines(r.Out);
        Assert.Equal("  Entry points (1)", lines[1]);
        Assert.Equal("    [api] HTTP POST /api/orders  ← depth 2", lines[2]);
        Assert.Equal("  Callers", lines[3]);

        // main 没有调用方，往上也走不到入口
        var top = Run("--lang", "en", "query", _repo, "src/app.ts:main", "--impact");
        Assert.Equal(0, top.Code);
        Assert.Contains("    (no entry point reached upwards)\n", top.Out);
        Assert.Contains("    (no callers)\n", top.Out);
        Assert.DoesNotMatch("[一-鿿]", top.Out);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
