using System.Text.Json;
using CodeAtlas.Core.Storage;
using CodeAtlas.Server;

namespace CodeAtlas.Cli.Commands;

/// <summary>
/// codeatlas affected：git diff 的改动行 → 改动的符号 → 受影响的入口与测试（规则见 AffectedQuery）。
/// 默认 base = HEAD（未提交的改动）；--base 给分支时取 merge-base（PR 语义）；--stdin 读统一 diff（git diff -U0）或文件名列表（自动识别）。
/// --quiet 只打印测试文件路径（相对仓库根，一行一个），管道给测试运行器；--json 打印完整结果。
/// </summary>
internal static class AffectedCommand
{
    public const string UsageLine = "codeatlas affected <repo> [--base <ref>] [--stdin] [--depth N] [--filter <glob> …] [--json] [--quiet] [--db <path>]";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Run(ParsedArgs args, TextWriter stdout, TextWriter stderr, TextReader stdin)
    {
        if (args.Positional.Count < 1) { stderr.WriteLine(L("用法：", "Usage: ") + UsageLine); return 2; }
        var repo = args.Positional[0];
        var depth = AffectedQuery.DefaultDepth;
        if (args.Value("--depth") is { } ds)
        {
            if (!int.TryParse(ds, out depth) || depth < 1) { stderr.WriteLine(L($"--depth 要是正整数：{ds}", $"--depth must be a positive integer: {ds}")); return 2; }
            depth = Math.Min(depth, ImpactBuilder.MaxDepth);
        }
        if (args.Has("--stdin") && args.Has("--base"))
        {
            stderr.WriteLine(L("--stdin 与 --base 只能选一个：--stdin 时比较基准由喂进来的 diff 决定", "Use either --stdin or --base: with --stdin the diff you feed decides the base"));
            return 2;
        }
        var options = new AffectedOptions(depth, args.Values("--filter"));

        IndexDatabase db;
        try { db = CommonOptions.OpenExisting(repo, args, stderr); }
        catch (FileNotFoundException) { return 1; }

        AffectedDto result;
        using (db)
        {
            var repoDir = Path.GetFullPath(repo);
            if (args.Has("--stdin")) result = AffectedQuery.FromText(db, repoDir, stdin.ReadToEnd(), options);
            else
            {
                var (r, error) = AffectedQuery.FromGit(db, repoDir, args.Value("--base"), options);
                if (r is null) { stderr.WriteLine(L($"错误：{error}", $"Error: {error}")); return 1; }
                result = r;
            }
        }

        if (args.Has("--json")) { stdout.WriteLine(JsonSerializer.Serialize(result, Json)); return 0; }
        if (args.Has("--quiet"))
        {
            foreach (var w in result.Warnings) stderr.WriteLine(L("提示：", "Note: ") + w);
            foreach (var f in result.TestFiles) stdout.WriteLine(f);
            return 0;
        }
        Print(result, stdout);
        return 0;
    }

    private static void Print(AffectedDto r, TextWriter o)
    {
        var source = r.Input switch
        {
            "git" => L($"base {r.Base}（merge-base {Short(r.MergeBase)}）→ 工作区", $"base {r.Base} (merge-base {Short(r.MergeBase)}) → work tree"),
            "diff" => L("stdin 里的 diff", "diff from stdin"),
            _ => L("stdin 里的文件名列表（只能文件级）", "file list from stdin (file level only)"),
        };
        o.WriteLine(L($"改动：{source}；{r.Files.Count} 个文件，{r.Symbols.Count} 个改动的符号", $"Changes: {source}; {Plural(r.Files.Count, "file", "files")}, {Plural(r.Symbols.Count, "changed symbol", "changed symbols")}"));
        if (r.Files.Count == 0) o.WriteLine(L("  （没有改动）", "  (no changes)"));
        foreach (var f in r.Files)
        {
            var status = f.Status switch { "added" => "A", "deleted" => "D", "renamed" => "R", _ => "M" };
            var what = f.Analysis switch
            {
                "symbols" => L($"{AffectedQuery.LineList(f.Ranges.SelectMany(Lines))} → {f.Symbols} 个符号", $"{AffectedQuery.LineList(f.Ranges.SelectMany(Lines))} → {Plural(f.Symbols, "symbol", "symbols")}"),
                "file" => L($"文件级 → {f.Symbols} 个符号", $"file level → {Plural(f.Symbols, "symbol", "symbols")}"),
                _ => L("跳过", "skipped"),
            };
            var tags = (f.IsTest ? L(" [测试]", " [test]") : "") + (f.Stale ? L(" [索引过期]", " [stale index]") : "");
            o.WriteLine($"  {status} {f.Path}{(f.OldPath is null ? "" : $"  ({L("原", "from")} {f.OldPath})")}  {what}{tags}{(f.Note is null ? "" : "  — " + f.Note)}");
        }

        o.WriteLine();
        o.WriteLine(L($"改动的符号（{r.Symbols.Count}）", $"Changed symbols ({r.Symbols.Count})"));
        foreach (var s in r.Symbols)
        {
            var why = s.Reason == "lines" ? AffectedQuery.LineList(s.Lines) : L("文件级", "file level");
            o.WriteLine($"  {s.Label}  [{s.Symbol.Kind}]  {s.Symbol.Path}:{s.Symbol.StartLine}  ({why}){(s.IsTest ? L(" [测试]", " [test]") : "")}");
            o.WriteLine(L($"    往上 {s.Callers} 个方法 · 入口 {s.Entries.Count} · 测试 {s.Tests.Count}{(s.Truncated ? "（可能不全）" : "")}",
                $"    {Plural(s.Callers, "method", "methods")} upward · {Plural(s.Entries.Count, "entry", "entries")} · {Plural(s.Tests.Count, "test", "tests")}{(s.Truncated ? " (may be incomplete)" : "")}"));
        }

        o.WriteLine();
        o.WriteLine(L($"受影响的入口（{r.Entries.Count}）", $"Affected entries ({r.Entries.Count})"));
        if (r.Entries.Count == 0) o.WriteLine(L("  （往上没有走到入口）", "  (no entry point reached upwards)"));
        foreach (var e in r.Entries)
            o.WriteLine(L($"  [{e.ContainerName}] {EntryText(e.Entry)}  深度 {e.Depth}{Conf(e.Confidence)}  ← {From(r, e.From)}",
                $"  [{e.ContainerName}] {EntryText(e.Entry)}  depth {e.Depth}{Conf(e.Confidence)}  ← {From(r, e.From)}"));

        o.WriteLine();
        o.WriteLine(L($"受影响的测试（{r.Tests.Count} 个方法，{r.TestFiles.Count} 个文件）", $"Affected tests ({Plural(r.Tests.Count, "method", "methods")}, {Plural(r.TestFiles.Count, "file", "files")})"));
        if (r.TestFiles.Count == 0) o.WriteLine(L("  （没有走到测试）", "  (no tests reached)"));
        var byFile = r.Tests.ToLookup(t => t.Symbol.Path);
        foreach (var file in r.TestFiles)
        {
            o.WriteLine($"  {file}");
            if (!byFile.Contains(file)) { o.WriteLine(L("    （测试文件本身改了）", "    (the test file itself changed)")); continue; }
            foreach (var t in byFile[file])
                o.WriteLine(L($"    {t.Label}  L{t.Symbol.StartLine}  深度 {t.Depth}{Conf(t.Confidence)}  ← {From(r, t.From)}",
                    $"    {t.Label}  L{t.Symbol.StartLine}  depth {t.Depth}{Conf(t.Confidence)}  ← {From(r, t.From)}"));
        }

        foreach (var w in r.Warnings) { o.WriteLine(); o.WriteLine(L("提示：", "Note: ") + w); }
    }

    private static string? Short(string? hash) => hash is { Length: > 10 } ? hash[..10] : hash;

    private static IEnumerable<int> Lines(AffectedRangeDto r) => r.Count == 0 ? [r.Start + 1] : Enumerable.Range(r.Start, r.Count);

    /// <summary>链路置信度：1 不写；低于 0.7 标"推测"（与扩展、README 的置信度档一致）。</summary>
    private static string Conf(double c) => c >= 1 ? "" : c < 0.7 ? L($"  置信度 {c:0.##}（推测）", $"  confidence {c:0.##} (guess)") : L($"  置信度 {c:0.##}", $"  confidence {c:0.##}");

    private static string From(AffectedDto r, IReadOnlyList<long> ids)
    {
        var labels = ids.Select(id => r.Symbols.FirstOrDefault(s => s.Symbol.Id == id)?.Label ?? $"#{id}").ToList();
        return labels.Count <= 3 ? string.Join(L("、", ", "), labels) : string.Join(L("、", ", "), labels.Take(3)) + L($" 等 {labels.Count} 个", $" and {labels.Count - 3} more");
    }

    private static string EntryText(EntryDto e) => e.Kind == "http"
        ? $"HTTP {(e.HttpMethod is null ? "" : e.HttpMethod + " ")}{e.Route ?? e.Name}"
        : $"{e.Kind} {e.Route ?? e.Name}";
}
