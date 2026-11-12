using System.Globalization;

namespace CodeAtlas.Core.Localization;

/// <summary>
/// 界面 / 输出文案的语言：zh*（zh-CN、zh-TW …）中文，其他一律英文。进程级设置，启动时定一次：
/// · serve：扩展按 VS Code 显示语言传 --lang；
/// · 命令行：Program.Main 按 CODEATLAS_LOCALE，没有则按系统 UI 语言；--lang 可覆盖；
/// · 测试：默认中文（不经 Main），英文用例在不并行的集合里 Set("en") 再还原。
/// 节点 key、slug、缓存键等标识一律用中文规范名算，不随语言变——换语言只换显示文字。
/// </summary>
public static class Loc
{
    /// <summary>环境变量：显式指定语言（也传给子进程 codeatlas-roslyn）。</summary>
    public const string EnvVar = "CODEATLAS_LOCALE";

    private static volatile bool _english;

    public static bool IsEnglish => _english;

    /// <summary>"zh" 或 "en"：参与后处理指纹、叙述缓存键。</summary>
    public static string Current => _english ? "en" : "zh";

    public static bool IsChinese(string? lang) => lang is not null && lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static void Set(string? lang) => _english = !IsChinese(lang);

    /// <summary>临时切换语言，Dispose 时还原（测试用：using var _ = Loc.Use("en")；所在测试类须进不并行的 "Locale" 集合）。</summary>
    public static IDisposable Use(string lang)
    {
        var previous = _english;
        Set(lang);
        return new Restore(() => _english = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>CODEATLAS_LOCALE，没有则系统 UI 语言。</summary>
    public static string Detect() => Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } env ? env : CultureInfo.CurrentUICulture.Name;

    /// <summary>按当前语言取一条文案：L("中文", "English")。</summary>
    public static string L(string zh, string en) => _english ? en : zh;

    /// <summary>列表连接符：中文顿号，英文逗号。</summary>
    public static string ListSep => _english ? ", " : "、";

    /// <summary>英文单复数：Plural(3, "caller", "callers") → "3 callers"。中文文案不用它。</summary>
    public static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
