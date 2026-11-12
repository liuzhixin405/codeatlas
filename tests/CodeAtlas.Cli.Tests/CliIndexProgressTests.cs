namespace CodeAtlas.Cli.Tests;

/// <summary>
/// index 的进度输出必须在 Run 返回前、在调用线程上同步写完。
/// System.Progress 会把回调投递到线程池：回调之间可能并发、也可能在 Run 返回后才写——
/// 写同一个非线程安全的 StringWriter 时损坏 StringBuilder，测试主机进程直接崩溃（"测试运行已中止"）。
/// </summary>
public class CliIndexProgressTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-progress-" + Guid.NewGuid().ToString("N"));

    /// <summary>每次写入都慢一点，并记录是否有并发写入、是否在 Run 返回之后还有写入。</summary>
    private sealed class SlowRecordingWriter : StringWriter
    {
        private int _inside;
        public volatile bool Returned;
        public int Concurrent;
        public int LateWrites;

        public override void WriteLine(string? value)
        {
            if (Interlocked.Increment(ref _inside) > 1) Interlocked.Increment(ref Concurrent);
            if (Returned) Interlocked.Increment(ref LateWrites);
            Thread.Sleep(30);
            lock (this) base.WriteLine(value);
            Interlocked.Decrement(ref _inside);
        }
    }

    public CliIndexProgressTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        for (var i = 0; i < 6; i++)
            File.WriteAllText(Path.Combine(_repo, "src", $"m{i}.ts"), $"export function f{i}(): number {{ return {i}; }}\n");
    }

    [Fact]
    public void ProgressLines_AreWrittenSynchronously_BeforeRunReturns()
    {
        var stdout = new StringWriter();
        var stderr = new SlowRecordingWriter();

        var code = Program.Run(["index", _repo, "--roslyn", "off"], stdout, stderr);
        stderr.Returned = true;
        Thread.Sleep(300); // 给线程池上还没执行的回调留出时间：有的话会被记成 LateWrites

        Assert.Equal(0, code);
        Assert.Equal(0, stderr.LateWrites);
        Assert.Equal(0, stderr.Concurrent);
        Assert.Contains("[parse]", stderr.ToString());
        Assert.Contains("  6/6", stderr.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }
}
