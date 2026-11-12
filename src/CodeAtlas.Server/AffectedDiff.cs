using System.Text;
using System.Text.RegularExpressions;

namespace CodeAtlas.Server;

public enum DiffStatus { Modified, Added, Deleted, Renamed }

/// <param name="NewStart">新侧起始行（git 的 +c）。</param>
/// <param name="NewCount">新侧行数（git 的 ,d，省略时为 1）；0 = 纯删除，删在 NewStart 行之后。</param>
/// <param name="Blank">这一块里增删的行全是空白（只改了空行 / 缩进成空行）：不算改动。</param>
public sealed record DiffHunk(int NewStart, int NewCount, bool Blank);

/// <param name="Path">新侧路径；删除时是旧路径。'/' 分隔，去掉 a/ b/ 前缀。</param>
/// <param name="OldPath">重命名 / 复制前的路径；其余为 null。</param>
/// <param name="Hunks">文件名列表输入时为 null（只知道文件改了，不知道哪几行）。</param>
public sealed record FileChange(string Path, string? OldPath, DiffStatus Status, IReadOnlyList<DiffHunk>? Hunks, bool Binary = false);

/// <summary>
/// 改动输入的解析：统一 diff（git diff -U0，也认带上下文的 -U3 与没有 diff --git 头的普通 diff -u）或文件名列表（git diff --name-only / --name-status）。
/// 只取新侧行号——diff 是 base → 工作区，索引反映的是工作区，新侧行号才和索引里符号的起止行对得上。
/// </summary>
public static partial class AffectedDiff
{
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    [GeneratedRegex(@"^@@ -\d+(?:,(\d+))? \+\d+(?:,\d+)? @@")]
    private static partial Regex HunkOld();

    /// <summary>git diff --name-status 的一行：状态字母（R / C 带相似度）+ 制表符分隔的路径。</summary>
    [GeneratedRegex(@"^([ACDMRTUX])\d*\t(.+)$")]
    private static partial Regex NameStatus();

    /// <summary>文本像统一 diff（有 diff --git 头、@@ 块头，或紧挨着的 --- / +++ 两行）。否则按文件名列表读。</summary>
    public static bool LooksLikeDiff(string text)
    {
        var lines = SplitLines(text);
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.StartsWith("diff --git ", StringComparison.Ordinal) || HunkHeader().IsMatch(l)) return true;
            if (l.StartsWith("--- ", StringComparison.Ordinal) && i + 1 < lines.Count && lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// 文件名列表：每行一个路径（--name-only），或 "M\tpath" / "R100\told\tnew"（--name-status）。空行与 # 开头的忽略，同一路径只留一次。
    /// --name-only 不知道状态，记 Modified（文件不在磁盘上时由调用方按删除处理）。
    /// </summary>
    public static List<FileChange> ParseFileList(string text)
    {
        var result = new List<FileChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            FileChange change;
            if (NameStatus().Match(raw.TrimEnd()) is { Success: true } m)
            {
                var paths = m.Groups[2].Value.Split('\t');
                change = m.Groups[1].Value switch
                {
                    "A" => new FileChange(Normalize(paths[0]), null, DiffStatus.Added, null),
                    "D" => new FileChange(Normalize(paths[0]), null, DiffStatus.Deleted, null),
                    "R" when paths.Length >= 2 => new FileChange(Normalize(paths[1]), Normalize(paths[0]), DiffStatus.Renamed, null),
                    "C" when paths.Length >= 2 => new FileChange(Normalize(paths[1]), Normalize(paths[0]), DiffStatus.Added, null),
                    _ => new FileChange(Normalize(paths[^1]), null, DiffStatus.Modified, null),
                };
            }
            else change = new FileChange(Normalize(line), null, DiffStatus.Modified, null);
            if (seen.Add(change.Path)) result.Add(change);
        }
        return result;
    }

    /// <summary>
    /// 统一 diff → 每个文件的改动块（新侧）。块内按头里的行数计数消费行，所以内容以 "---" / "+++" 开头的增删行不会被当成文件头。
    /// 同一文件出现多次（几段 diff 拼在一起）时块合并。
    /// </summary>
    public static List<FileChange> ParseDiff(string text)
    {
        var lines = SplitLines(text);
        var files = new List<Builder>();
        Builder? cur = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                cur = new Builder();
                files.Add(cur);
                (cur.OldPath, cur.NewPath) = GitHeaderPaths(line["diff --git ".Length..]);
                continue;
            }
            if (line.StartsWith("--- ", StringComparison.Ordinal) && i + 1 < lines.Count && lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                // 普通 diff -u 没有 diff --git 头：--- / +++ 开始一个新文件；git 的头已经开过文件、还没有块时只是补路径
                if (cur is null || cur.Hunks.Count > 0 || cur.SawFileLines) { cur = new Builder(); files.Add(cur); }
                cur.SawFileLines = true;
                var oldP = HeaderPath(line[4..], "a/");
                var newP = HeaderPath(lines[i + 1][4..], "b/");
                if (oldP is null) cur.Status = DiffStatus.Added; else cur.OldPath = oldP;
                if (newP is null) cur.Status = DiffStatus.Deleted; else cur.NewPath = newP;
                i++;
                continue;
            }
            if (cur is null) continue;
            if (HunkHeader().Match(line) is { Success: true } h)
            {
                var start = int.Parse(h.Groups[1].Value);
                var count = h.Groups[2].Success ? int.Parse(h.Groups[2].Value) : 1;
                var oldMatch = HunkOld().Match(line);
                var oldCount = oldMatch.Groups[1].Success ? int.Parse(oldMatch.Groups[1].Value) : 1;
                // 块体：按行数消费（' ' 两侧都减，'-' 减旧侧，'+' 减新侧，'\' 是"文件末尾没有换行"的标记）
                var remOld = oldCount;
                var remNew = count;
                var blank = true;
                var changedLines = new List<int>(); // 带上下文（-U3）时只算 + 行与删除点，不把上下文行算作改动
                var newLine = start;
                var pendingDeletion = false;
                while ((remOld > 0 || remNew > 0) && i + 1 < lines.Count)
                {
                    var body = lines[i + 1];
                    if (body.Length == 0) { remOld--; remNew--; newLine++; i++; continue; } // 有的工具把空的上下文行写成空串
                    var c = body[0];
                    if (c == '\\') { i++; continue; }
                    if (c == ' ') { if (pendingDeletion) { changedLines.Add(-newLine); pendingDeletion = false; } remOld--; remNew--; newLine++; }
                    else if (c == '-') { remOld--; pendingDeletion = true; if (!string.IsNullOrWhiteSpace(body[1..])) blank = false; }
                    else if (c == '+') { remNew--; changedLines.Add(newLine); newLine++; pendingDeletion = false; if (!string.IsNullOrWhiteSpace(body[1..])) blank = false; }
                    else break;
                    i++;
                }
                if (pendingDeletion) changedLines.Add(-newLine);
                // 纯删除块（+c,0）：git 的 c 是删除点之前那一行，直接按 (c, 0) 记
                if (count == 0) changedLines.Clear();
                cur.AddHunk(start, count, blank, changedLines);
                continue;
            }
            if (line.StartsWith("new file mode", StringComparison.Ordinal)) cur.Status = DiffStatus.Added;
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) cur.Status = DiffStatus.Deleted;
            else if (line.StartsWith("rename from ", StringComparison.Ordinal)) { cur.OldPath = Unquote(line["rename from ".Length..]); cur.Status = DiffStatus.Renamed; }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal)) { cur.NewPath = Unquote(line["rename to ".Length..]); cur.Status = DiffStatus.Renamed; }
            else if (line.StartsWith("copy from ", StringComparison.Ordinal)) { cur.OldPath = Unquote(line["copy from ".Length..]); cur.Status = DiffStatus.Added; }
            else if (line.StartsWith("copy to ", StringComparison.Ordinal)) { cur.NewPath = Unquote(line["copy to ".Length..]); cur.Status = DiffStatus.Added; }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal)) cur.Binary = true;
        }

        // 合并同一路径（几段 diff 拼起来时）
        var merged = new Dictionary<string, FileChange>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var b in files)
        {
            var path = b.Status == DiffStatus.Deleted ? b.OldPath ?? b.NewPath : b.NewPath ?? b.OldPath;
            if (path is null) continue;
            path = Normalize(path);
            var change = new FileChange(path, b.Status is DiffStatus.Renamed or DiffStatus.Added && b.OldPath is { } o && Normalize(o) != path ? Normalize(o) : null,
                b.Status, b.Hunks, b.Binary);
            if (merged.TryGetValue(path, out var prev))
                merged[path] = prev with { Hunks = [.. prev.Hunks ?? [], .. change.Hunks ?? []], Binary = prev.Binary || change.Binary };
            else { merged[path] = change; order.Add(path); }
        }
        return order.Select(p => merged[p]).ToList();
    }

    private sealed class Builder
    {
        public string? OldPath;
        public string? NewPath;
        public DiffStatus Status = DiffStatus.Modified;
        public bool Binary;
        public bool SawFileLines;
        public List<DiffHunk> Hunks { get; } = [];

        /// <summary>
        /// -U0 时一块就是一个区间；带上下文时把块拆成真正改动的行：+ 行各自一段，删除点（负数，记删除点之后那一行的新侧行号）记一段 count = 0。
        /// </summary>
        public void AddHunk(int start, int count, bool blank, List<int> changed)
        {
            if (changed.Count == 0) { Hunks.Add(new DiffHunk(start, count, blank)); return; }
            var runStart = -1;
            var prev = -1;
            foreach (var c in changed)
            {
                if (c < 0)
                {
                    if (runStart > 0) { Hunks.Add(new DiffHunk(runStart, prev - runStart + 1, blank)); runStart = -1; }
                    // 删在 (-c - 1) 行之后；如果删除点紧接在刚加的行后面（替换），那段 + 已经覆盖，不再单记
                    if (prev != -c - 1 || prev < 0) Hunks.Add(new DiffHunk(-c - 1, 0, blank));
                    prev = -c - 1;
                    continue;
                }
                if (runStart > 0 && c == prev + 1) { prev = c; continue; }
                if (runStart > 0) Hunks.Add(new DiffHunk(runStart, prev - runStart + 1, blank));
                runStart = c;
                prev = c;
            }
            if (runStart > 0) Hunks.Add(new DiffHunk(runStart, prev - runStart + 1, blank));
        }
    }

    /// <summary>"a/x b/y"（可能带引号）→ (x, y)。路径里有空格又没引号时按 " b/" 切。</summary>
    private static (string? Old, string? New) GitHeaderPaths(string rest)
    {
        if (rest.StartsWith('"'))
        {
            var end = QuotedEnd(rest, 0);
            var a = Unquote(rest[..(end + 1)]);
            var b = Unquote(rest[(end + 1)..].Trim());
            return (StripPrefix(a, "a/"), StripPrefix(b, "b/"));
        }
        var idx = rest.IndexOf(" b/", StringComparison.Ordinal);
        if (idx < 0) idx = rest.IndexOf(" \"b/", StringComparison.Ordinal);
        if (idx < 0) return (null, null);
        return (StripPrefix(rest[..idx], "a/"), StripPrefix(Unquote(rest[(idx + 1)..]), "b/"));
    }

    /// <summary>--- / +++ 后面的路径：/dev/null → null；去掉前缀与 diff -u 加在后面的 \t时间戳。</summary>
    private static string? HeaderPath(string raw, string prefix)
    {
        var p = raw;
        if (!p.StartsWith('"')) { var tab = p.IndexOf('\t'); if (tab >= 0) p = p[..tab]; }
        p = Unquote(p.TrimEnd());
        if (p == "/dev/null") return null;
        return StripPrefix(p, prefix);
    }

    private static string StripPrefix(string p, string prefix) => p.StartsWith(prefix, StringComparison.Ordinal) ? p[prefix.Length..] : p;

    private static int QuotedEnd(string s, int start)
    {
        for (var i = start + 1; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '"') return i;
        }
        return s.Length - 1;
    }

    /// <summary>git 的 C 风格引号路径（core.quotepath 打开时中文是 \346\226\207 这种八进制字节）→ 原文。不带引号的原样返回。</summary>
    public static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return s;
        var bytes = new List<byte>();
        for (var i = 1; i < s.Length - 1; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length - 1) { bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString())); continue; }
            var n = s[++i];
            if (n is >= '0' and <= '7' && i + 2 < s.Length - 1 && s[i + 1] is >= '0' and <= '7' && s[i + 2] is >= '0' and <= '7')
            {
                bytes.Add((byte)Convert.ToInt32(s.Substring(i, 3), 8));
                i += 2;
                continue;
            }
            bytes.Add(n switch { 'n' => (byte)'\n', 't' => (byte)'\t', 'r' => (byte)'\r', 'a' => 7, 'b' => 8, 'f' => 12, 'v' => 11, _ => (byte)n });
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>反斜杠换成 /，去掉开头的 ./。</summary>
    public static string Normalize(string path)
    {
        var p = Unquote(path).Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p;
    }

    private static List<string> SplitLines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
}
