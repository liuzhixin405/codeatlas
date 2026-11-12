using CodeAtlas.Cli.Commands;

namespace CodeAtlas.Cli.Tests;

/// <summary>codeatlas affected：临时 git 仓库（tree-sitter 索引）上改一行，输出钉到改动的符号与测试文件。没有 git 时跳过。</summary>
public sealed class CliAffectedTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-affected-" + Guid.NewGuid().ToString("N"));

    public CliAffectedTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        Directory.CreateDirectory(Path.Combine(_repo, "tests"));
        Write("src/util.ts", "export function helper(id: number): string {\n  return String(id);\n}\n\nexport function unused(): number {\n  return 1;\n}\n");
        Write("src/svc.ts", "import { helper } from './util';\nexport class Svc {\n  find(id: number): string {\n    return helper(id);\n  }\n}\n");
        Write("tests/svc.test.ts", "import { Svc } from '../src/svc';\nexport function testFind(): void {\n  new Svc().find(1);\n}\n");
        Write("tests/other.test.ts", "export function testNothing(): void {\n}\n");
        Write("README.md", "# demo\n");
        Write(".gitignore", ".codeatlas/\n");
    }

    private void Write(string rel, string content) => File.WriteAllText(Path.Combine(_repo, rel.Replace('/', Path.DirectorySeparatorChar)), content);

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

    private bool InitAndCommit() => Git("init", "-q", "-b", "main") && Git("add", "-A") && Git("commit", "-q", "-m", "init");

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Program.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static (int Code, string Out, string Err) RunStdin(string input, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var parsed = ParsedArgs.Parse(args, new HashSet<string> { "--stdin", "--quiet", "--json" });
        var code = AffectedCommand.Run(parsed, stdout, stderr, new StringReader(input));
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string[] Lines(string s) => s.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Git_DefaultBaseHead_ChangedLineInHelper_QuietPrintsOnlyTheTestFileThatReachesIt()
    {
        if (!InitAndCommit()) return; // 没有 git
        Assert.Equal(0, Run("index", _repo, "--roslyn", "off").Code);
        Write("src/util.ts", "export function helper(id: number): string {\n  return String(id + 1);\n}\n\nexport function unused(): number {\n  return 1;\n}\n");
        Write("README.md", "# demo\nmore\n");

        var quiet = Run("affected", _repo, "--quiet");
        Assert.Equal(0, quiet.Code);
        Assert.Equal(["tests/svc.test.ts"], Lines(quiet.Out)); // other.test.ts 没走到；README 非源码

        var text = Run("affected", _repo);
        Assert.Equal(0, text.Code);
        Assert.Contains("base HEAD", text.Out);
        Assert.Contains("  M README.md  跳过  — 非源码文件", text.Out);
        Assert.Contains("  M src/util.ts  L2 → 1 个符号", text.Out);
        Assert.Contains("  helper  [function]  src/util.ts:1  (L2)", text.Out);
        Assert.Contains("    testFind  L2  深度 2", text.Out);
        Assert.DoesNotContain("unused", text.Out);
    }

    [Fact]
    public void Git_UntrackedNewTestFile_IsAffectedItself_AndImportChangeFallsBackToFileLevel()
    {
        if (!InitAndCommit()) return;
        Write("tests/new.test.ts", "export function testNew(): void {\n}\n");
        Write("src/svc.ts", "import { helper } from \"./util\";\nexport class Svc {\n  find(id: number): string {\n    return helper(id);\n  }\n}\n");
        Assert.Equal(0, Run("index", _repo, "--roslyn", "off").Code);

        var json = Run("affected", _repo, "--json");
        Assert.Equal(0, json.Code);
        using var doc = System.Text.Json.JsonDocument.Parse(json.Out);
        var files = doc.RootElement.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!);
        Assert.Equal(("added", "file"), (files["tests/new.test.ts"].GetProperty("status").GetString(), files["tests/new.test.ts"].GetProperty("analysis").GetString()));
        Assert.Equal("file", files["src/svc.ts"].GetProperty("analysis").GetString());
        Assert.Equal([1], files["src/svc.ts"].GetProperty("outside").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(["tests/new.test.ts", "tests/svc.test.ts"], doc.RootElement.GetProperty("testFiles").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void Stdin_UnifiedDiffAndFileList_AreAutoDetected()
    {
        Assert.Equal(0, Run("index", _repo, "--roslyn", "off").Code);
        // 只改 unused：没有调用方，也没有测试
        var diff = "diff --git a/src/util.ts b/src/util.ts\n--- a/src/util.ts\n+++ b/src/util.ts\n@@ -6 +6 @@\n-  return 0;\n+  return 1;\n";
        var r = RunStdin(diff, _repo, "--stdin", "--quiet");
        Assert.Equal(0, r.Code);
        Assert.Empty(Lines(r.Out));
        var full = RunStdin(diff, _repo, "--stdin", "--json");
        using (var doc = System.Text.Json.JsonDocument.Parse(full.Out))
        {
            Assert.Equal("diff", doc.RootElement.GetProperty("input").GetString());
            Assert.Equal(["unused"], doc.RootElement.GetProperty("symbols").EnumerateArray().Select(s => s.GetProperty("label").GetString()));
        }

        // 文件名列表：文件级，util.ts 里的 helper 也算进来
        var list = RunStdin("src/util.ts\nREADME.md\n", _repo, "--stdin", "--quiet");
        Assert.Equal(["tests/svc.test.ts"], Lines(list.Out));
    }

    [Fact]
    public void Errors_NotARepo_UnknownBase_StdinWithBase()
    {
        Assert.Equal(0, Run("index", _repo, "--roslyn", "off").Code);
        if (!Git("--version")) return;
        var notRepo = Run("affected", _repo);
        Assert.Equal(1, notRepo.Code);
        Assert.Contains("--stdin", notRepo.Err); // 提示没有 git 时怎么用

        Assert.True(InitAndCommit());
        var badBase = Run("affected", _repo, "--base", "no-such-branch");
        Assert.Equal(1, badBase.Code);
        Assert.Contains("找不到 base：no-such-branch", badBase.Err);
        var option = Run("affected", _repo, "--base", "--upload-pack=x");
        Assert.NotEqual(0, option.Code); // 以 - 开头的 base 不会被当成 git 选项

        var both = Run("affected", _repo, "--stdin", "--base", "HEAD");
        Assert.Equal(2, both.Code);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_repo)) return;
        foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); // .git 里的只读对象
        Directory.Delete(_repo, recursive: true);
    }
}
