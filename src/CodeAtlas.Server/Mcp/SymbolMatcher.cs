using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Server.Mcp;

/// <summary>一个命中的符号：得分与命中原因（"名字 find"、"全文 impact*"……，输出里原样给 agent 看）。</summary>
public sealed record SymbolHit(SymbolRecord Symbol, string Path, double Score, IReadOnlyList<string> Reasons);

/// <summary>
/// explore 的输入（自然语言问题或一串符号名 / 文件名）→ 按相关度排好的符号。打分简单、可解释，每一分都能在 Reasons 里看到来源：
/// <list type="number">
/// <item>切词：按空白与标点（中英文）切开；保留 . : / \ # &lt; &gt; 等限定名里会出现的字符；全小写的停用词（how / does / the / work……）整个跳过，写成 Find 之类的照常当符号名。</item>
/// <item>#123：按符号 id，100 分。</item>
/// <item>像符号名的词：<see cref="OutlineQuery.FindSymbols"/>（完整限定名 → 短名 → 按段对齐的限定名后缀，与 CLI query 同一套）。
/// 限定名完全一致 100 分；名字 / 后缀命中 90 分，同名候选多于 3 个时每多一个减 5 分，最低 30（短名太常见，说明不了什么）。</item>
/// <item>像文件的词（带 / \ 或常见源码扩展名）：路径相等或以 /词 结尾的文件里的顶层定义（类型、函数、顶层方法），60 分。</item>
/// <item>全文前缀（symbols_fts，与 search 命令同一个 <see cref="IndexReader.SearchSymbols"/>）：不在停用词表里、≥3 个字符的词，
/// 按 bm25 排名 40 分起、每名减 1，最低 20；相邻两个词拼起来（impact query → impactquery*，对上驼峰名 ImpactQuery）70 分起。</item>
/// <item>单个词还按名字不分大小写找一次（build 对上 Build），分数同上。</item>
/// <item>同一个符号被几个词命中就把各词的最高分相加（每个词只算一次）；成员还继承所属类型对上的词，
/// （取所属类型与自己对上同一个词的较高分）所以"ImpactQuery build"会把 ImpactQuery.Build（build 30 + 所属类型 ImpactQuery 90）顶到 ImpactQuery 类本身（90）前面。</item>
/// <item>调整：测试代码 ×0.5（includeTests 时不降）；属性 / 字段 / 变量 ×0.7；模块、命名空间、外部符号不要。</item>
/// <item>排序：得分降序，同分按种类（方法 / 函数 &gt; 类型 &gt; 其他）、限定名长度、id。</item>
/// </list>
/// </summary>
public static class SymbolMatcher
{
    public const double IdScore = 100;
    public const double ExactScore = 100;
    public const double NameScore = 90;
    public const double FileScore = 60;
    public const double PairScore = 70;
    public const double FtsScore = 40;
    /// <summary>一个词按名字最多取几个符号（再多也只是 30 分的弱命中）。</summary>
    private const int MaxNamed = 500;

    private static readonly Regex Splitter = new(@"[\s,;!?""'`()\[\]{}=+*|，。？！、；：（）【】「」《》“”‘’]+", RegexOptions.Compiled);
    private static readonly Regex FileLike = new(@"\.(cs|ts|tsx|js|jsx|mjs|cjs|go|py|java|vue)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>自然语言里的虚词与提问用语：不拿去做全文检索（"how does X work" 里只有 X 有用）。</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "not", "of", "to", "in", "on", "for", "from", "with", "by", "at", "as", "into", "about",
        "is", "are", "was", "were", "be", "been", "do", "does", "did", "how", "what", "where", "when", "why", "which", "who",
        "this", "that", "these", "those", "it", "its", "there", "here", "can", "could", "should", "would", "will", "get",
        "work", "works", "working", "used", "use", "uses", "using", "call", "calls", "called", "code", "function", "method",
        "class", "file", "files", "find", "show", "explain", "between", "flow", "me", "my", "we", "our", "you", "your", "all", "any",
    };

    /// <summary>query → 命中（已排序，没有截断）。</summary>
    public static List<SymbolHit> Match(IndexDatabase db, string query, bool includeTests)
    {
        var reader = new IndexReader(db);
        var tokens = Tokenize(query);
        // 符号 id → (各词的最高分, 原因)
        var perToken = new Dictionary<long, Dictionary<string, (double Score, string Reason)>>();
        var symbols = new Dictionary<long, SymbolRecord>();

        void Add(string token, SymbolRecord s, double score, string reason)
        {
            if (s.Kind is SymbolKind.Module or SymbolKind.Namespace or SymbolKind.External) return;
            symbols[s.Id] = s;
            if (!perToken.TryGetValue(s.Id, out var byToken)) perToken[s.Id] = byToken = new(StringComparer.Ordinal);
            if (!byToken.TryGetValue(token, out var old) || old.Score < score) byToken[token] = (score, reason);
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            // 全小写的停用词是自然语言（"how does it work"）；写成 Find / FIND 的当作符号名照常找
            if (IsStopWord(token)) continue;
            if (token.StartsWith('#') && long.TryParse(token[1..], out var id))
            {
                if (reader.GetSymbol(id) is { } byId) Add(token, byId, IdScore, $"id {token}");
                continue;
            }

            var named = OutlineQuery.FindSymbols(db, token).ToList();
            // 自然语言里的词大小写随意（build 对 Build）：单个词再按名字不分大小写找一次
            var caseInsensitive = new HashSet<long>();
            if (IsPlainWord(token))
            {
                var known = named.Select(n => n.Id).ToHashSet();
                foreach (var s in OutlineQuery.ReadSymbols(db, $"SELECT {OutlineQuery.SymbolColumns} FROM symbols s WHERE s.name = $n COLLATE NOCASE AND s.kind NOT IN ('module', 'namespace', 'external') LIMIT {MaxNamed}", 1, ("$n", token)))
                    if (known.Add(s.Id)) { named.Add(s); caseInsensitive.Add(s.Id); }
            }
            if (named.Count > 0)
            {
                var exact = named.Count == 1 && named[0].QualifiedName == token;
                var score = exact ? ExactScore : Math.Max(30, NameScore - 5 * Math.Max(0, named.Count - 3));
                foreach (var s in named.Take(MaxNamed))
                    Add(token, s, score, exact ? L($"限定名 {token}", $"qualified name {token}")
                        : caseInsensitive.Contains(s.Id) ? L($"名字 {token}（不分大小写）", $"name {token} (case-insensitive)") : L($"名字 {token}", $"name {token}"));
            }

            if (FileLike.IsMatch(token) || token.Contains('/') || token.Contains('\\'))
                foreach (var s in FileSymbols(db, token)) Add(token, s, FileScore, L($"文件 {token}", $"file {token}"));

            if (IsSearchWord(token))
            {
                var rank = 0;
                foreach (var s in reader.SearchSymbols(token, 20))
                    Add(token, s, Math.Max(20, FtsScore - rank++), L($"全文 {token}*", $"full-text {token}*"));
            }

            // 相邻两个词拼成一个前缀：impact query → impactquery*（FTS 把驼峰名当一个词，拆开的两个词对不上 ImpactQuery）
            if (i + 1 < tokens.Count && IsSearchWord(token) && IsSearchWord(tokens[i + 1]) && IsPlainWord(token) && IsPlainWord(tokens[i + 1]))
            {
                var pair = token + tokens[i + 1];
                var rank = 0;
                foreach (var s in reader.SearchSymbols(pair, 10))
                    Add(pair, s, Math.Max(40, PairScore - 2 * rank++), L($"全文 {pair}*", $"full-text {pair}*"));
            }
        }

        var hits = new List<SymbolHit>();
        if (symbols.Count == 0) return hits;

        // 成员继承所属类型命中的词："ImpactQuery build" 里 build 对上很多方法，其中 ImpactQuery.Build 的类型又对上了 ImpactQuery
        var snapshot = perToken.ToDictionary(p => p.Key, p => p.Value.ToList());
        foreach (var (sid, byToken) in perToken)
        {
            if (symbols[sid].ParentId is not { } parent || !snapshot.TryGetValue(parent, out var parentTokens) || !OutlineBuilder.IsType(symbols[parent].Kind)) continue;
            foreach (var (token, v) in parentTokens)
                if (!byToken.TryGetValue(token, out var own) || own.Score < v.Score)
                    byToken[token] = (v.Score, L($"所属类型 {symbols[parent].Name} 对上 {token}", $"declaring type {symbols[parent].Name} matches {token}"));
        }
        var paths = Paths(db, symbols.Values.Select(s => s.FileId).Distinct().ToList());
        foreach (var (sid, byToken) in perToken)
        {
            var s = symbols[sid];
            var path = paths.GetValueOrDefault(s.FileId, IndexWriter.ExternalFilePath);
            var score = byToken.Values.Sum(v => v.Score);
            var reasons = byToken.Values.OrderByDescending(v => v.Score).Select(v => v.Reason).ToList();
            if (!includeTests && PipelineBuilder.IsTest(path, s.QualifiedName)) { score *= 0.5; reasons.Add(L("测试代码 ×0.5", "test code ×0.5")); }
            if (s.Kind is SymbolKind.Property or SymbolKind.Field or SymbolKind.Variable) { score *= 0.7; reasons.Add(L("成员变量 ×0.7", "member/variable ×0.7")); }
            hits.Add(new SymbolHit(s, path, Math.Round(score, 1), reasons));
        }
        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => KindRank(h.Symbol.Kind))
            .ThenBy(h => h.Symbol.QualifiedName.Length)
            .ThenBy(h => h.Symbol.Id)
            .ToList();
    }

    /// <summary>按空白与标点切词；去掉词尾的 . : 和英文所有格。</summary>
    public static List<string> Tokenize(string query) =>
        Splitter.Split(query)
            .Select(t => t.Trim('.', ':', '-'))
            .Select(t => t.EndsWith("'s", StringComparison.Ordinal) ? t[..^2] : t)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool IsStopWord(string token) => StopWords.Contains(token) && token.All(c => !char.IsUpper(c));

    private static bool IsSearchWord(string token) => token.Length >= 3 && !IsStopWord(token) && token.Any(char.IsLetterOrDigit);

    private static bool IsPlainWord(string token) => token.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static int KindRank(SymbolKind k) => k switch
    {
        SymbolKind.Method or SymbolKind.Function => 0,
        SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum => 1,
        _ => 2,
    };

    /// <summary>路径等于 token 或以 /token 结尾的文件（最多 3 个）里的顶层定义：类型、函数，以及父符号不是类型的方法。</summary>
    private static List<SymbolRecord> FileSymbols(IndexDatabase db, string token)
    {
        var want = token.Replace('\\', '/').TrimStart('.', '/');
        if (want.Length == 0) return [];
        var fileIds = db.Query<long>("SELECT id FROM files WHERE path = $p OR path LIKE $suffix ESCAPE '!' ORDER BY length(path) LIMIT 3",
            ("$p", want), ("$suffix", "%/" + want.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_")));
        if (fileIds.Count == 0) return [];
        var all = OutlineQuery.ReadSymbols(db, $"SELECT {OutlineQuery.SymbolColumns} FROM symbols s WHERE s.file_id IN ({OutlineQuery.Ids(fileIds)}) ORDER BY s.start_line", fileIds.Count);
        var byId = all.ToDictionary(s => s.Id);
        return all.Where(s =>
                s.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum or SymbolKind.Function or SymbolKind.Method
                && (s.ParentId is not { } p || !byId.TryGetValue(p, out var parent) || parent.Kind is SymbolKind.Module or SymbolKind.Namespace))
            .Take(20)
            .ToList();
    }

    internal static Dictionary<long, string> Paths(IndexDatabase db, IReadOnlyCollection<long> fileIds)
    {
        var paths = new Dictionary<long, string>();
        if (fileIds.Count == 0) return paths;
        using var cmd = db.CreateCommand($"SELECT id, path FROM files WHERE id IN ({OutlineQuery.Ids(fileIds)})");
        using var r = cmd.ExecuteReader();
        while (r.Read()) paths[r.GetInt64(0)] = r.GetString(1);
        return paths;
    }
}
