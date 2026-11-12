namespace CodeAtlas.Cli.Tests;

/// <summary>arch &lt;repo&gt; --cycles files：真实 TS 小仓库，有环退出码 1、没环 0、用法错误 2。</summary>
public class CliImportCyclesTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-cli-cycles-" + Guid.NewGuid().ToString("N"));

    public CliImportCyclesTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "a.ts"), "import { b } from './b';\nexport function a(): number { return b(); }\n");
        File.WriteAllText(Path.Combine(_repo, "src", "b.ts"), "import { c } from './c';\nexport function b(): number { return c(); }\n");
        File.WriteAllText(Path.Combine(_repo, "src", "c.ts"), "import { x } from 'lodash';\nimport { a } from './a';\nexport function c(): number { return a() + x; }\n");
        File.WriteAllText(Path.Combine(_repo, "src", "main.ts"), "import { a } from './a';\na();\n");
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Program.Run(args, stdout, stderr);
        return (code, stdout.ToString().Replace("\r", ""), stderr.ToString());
    }

    [Fact]
    public void Cycle_ExitsWith1_ListsTheFilesAndImportLines()
    {
        Assert.Equal(2, Run("arch", _repo, "--cycles", "files").Code); // 还没有索引：2，不是 0 也不是 1
        Assert.Equal(0, Run("index", _repo).Code);
        var r = Run("arch", _repo, "--cycles", "files");
        Assert.Equal(1, r.Code);
        Assert.Contains("文件级 import：4 个文件、4 条边（只算确定边）", r.Out);
        Assert.Contains(string.Join("\n",
            "#1  3 个文件：src/a.ts、src/b.ts、src/c.ts",
            "    src/a.ts:1  →  src/b.ts",
            "    src/b.ts:1  →  src/c.ts",
            "    src/c.ts:2  →  src/a.ts"), r.Out);

        var json = Run("arch", _repo, "--cycles", "files", "--json");
        Assert.Equal(1, json.Code);
        Assert.Contains("\"path\": [\n        \"src/a.ts\",\n        \"src/b.ts\",\n        \"src/c.ts\",\n        \"src/a.ts\"\n      ]", json.Out);

        Assert.Equal(2, Run("arch", _repo, "--cycles", "modules").Code);
        Assert.Equal(2, Run("arch", _repo, "--cycles", "files", "--edges", "some").Code);
    }

    [Fact]
    public void NoCycle_ExitsWith0()
    {
        File.WriteAllText(Path.Combine(_repo, "src", "c.ts"), "export function c(): number { return 1; }\n");
        Assert.Equal(0, Run("index", _repo).Code);
        var r = Run("arch", _repo, "--cycles", "files", "--edges", "all");
        Assert.Equal(0, r.Code);
        Assert.Contains("没有环", r.Out);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { }
    }
}
