namespace CodeAtlas.Cli.Tests;

/// <summary>query &lt;A&gt; --to &lt;B&gt;：两点间调用路径。</summary>
public class CliPathTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-cli-path-" + Guid.NewGuid().ToString("N"));

    public CliPathTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src", "util"));
        File.WriteAllText(Path.Combine(_repo, "src", "util", "format.ts"), "export function helper(id: number): string { return String(id); }\n");
        File.WriteAllText(Path.Combine(_repo, "src", "svc.ts"), "import { helper } from './util/format';\nexport class Svc {\n  find(id: number): string { return helper(id); }\n}\n");
        File.WriteAllText(Path.Combine(_repo, "src", "app.ts"), "import { Svc } from './svc';\nexport function main(): void { new Svc().find(1); fetch('x'); }\n");
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Program.Run(args, stdout, stderr);
        return (code, stdout.ToString().Replace("\r", ""), stderr.ToString());
    }

    [Fact]
    public void QueryTo_PrintsEachPathStepByStep_AndJson()
    {
        Assert.Equal(0, Run("index", _repo).Code);
        var r = Run("query", _repo, "main", "--to", "helper");
        Assert.Equal(0, r.Code);
        Assert.Contains("路径 1 条（最多 8 步、10 条）", r.Out);
        Assert.Contains(string.Join("\n",
            "#1  2 步  最低 confidence 0.90",
            "    main",
            "    → calls  Svc.find  (0.90)  @ src/app.ts:2",
            "    → calls  helper  (0.95)  @ src/svc.ts:3"), r.Out);

        var json = Run("query", _repo, "main", "--to", "helper", "--json", "--depth", "3");
        Assert.Equal(0, json.Code);
        Assert.Contains("\"maxDepth\": 3,", json.Out);
        Assert.Contains("\"label\": \"Svc.find\",", json.Out);

        var none = Run("query", _repo, "helper", "--to", "main");
        Assert.Contains("8 步之内没有从 helper 到 main 的调用路径", none.Out);
        Assert.Equal(1, Run("query", _repo, "main", "--to", "nope").Code);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { }
    }
}
