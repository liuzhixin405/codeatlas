using System.Text;
using System.Text.Json.Nodes;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Server.Mcp;

/// <summary>工具调用的结果：Text 原样放进 MCP 的 text content；IsError 时 agent 看到的是工具执行错误（不是协议错误）。</summary>
public sealed record McpToolResult(string Text, bool IsError = false);

/// <summary>
/// CodeAtlas 提供给 AI 编程助手的工具。主推 <c>codeatlas_explore</c>（一次拿到源码 + 调用路径 + 影响范围），其余几个是补充：
/// search（按前缀找符号）、callers / callees（单个符号的一层调用方 / 调出）、impact（完整影响范围）、status（索引状态与过期文件）。
/// 每次调用重新打开索引库：扩展或 CLI 重建索引后不用重启 MCP server 就能看到新数据。
/// </summary>
public sealed class McpTools(string repoRoot, string dbPath)
{
    public string RepoRoot { get; } = Path.GetFullPath(repoRoot);
    public string DbPath { get; } = Path.GetFullPath(dbPath);

    private const string SymbolArgHelp = "Symbol: qualified name (Acme.Orders.OrderService.Find), short name (Find), Type.Method suffix, or #id from other tool output.";

    /// <summary>tools/list 的 tools 数组。</summary>
    public static JsonArray Definitions() =>
    [
        Tool("codeatlas_explore",
            "PRIMARY TOOL — call this FIRST for almost any code question about this repository (how does X work, where is X, what calls X, what breaks if I change X) " +
            "and before editing a symbol. Input can be a natural-language question OR a list of symbol / file names. One call returns: the verbatim, line-numbered source " +
            "of the most relevant symbols grouped by file (read live from disk — treat it as already Read), the call paths between them (including dispatch / handler edges), " +
            "each symbol's outgoing calls, and an impact summary (direct callers, test callers, affected entry points). Every edge carries a confidence; 0.3 edges are " +
            "flagged as low-confidence same-name candidates. Output is capped by maxChars and truncates by relevance (truncated symbols are listed with file:line). " +
            "A stale-index warning at the top lists files changed since indexing — read those files directly. Prefer this over search/callers/callees.",
            new JsonObject
            {
                ["query"] = Prop("string", "Natural-language question or space-separated symbol / file names, e.g. \"how does ImpactQuery build callers\" or \"OrderService.Approve src/orders/handler.ts\"."),
                ["maxChars"] = Prop("integer", $"Output budget in characters (default {ExploreBuilder.DefaultMaxChars}, range {ExploreBuilder.MinMaxChars}–{ExploreBuilder.MaxMaxChars}; ~4 chars per token)."),
                ["maxSymbols"] = Prop("integer", $"Max matched symbols to include (default {ExploreBuilder.DefaultMaxSymbols})."),
                ["includeTests"] = Prop("boolean", "Do not down-rank test code, and list test callers (default false)."),
            }, ["query"]),
        Tool("codeatlas_search",
            "Find symbols by name prefix (case-insensitive; several words must all match). Returns qualified name, kind, file:line and #id. " +
            "Use only to locate a name; prefer codeatlas_explore, which also returns source and relations.",
            new JsonObject
            {
                ["query"] = Prop("string", "Name or name prefix, e.g. \"OrderApproval\"."),
                ["limit"] = Prop("integer", "Max results (default 30)."),
            }, ["query"]),
        Tool("codeatlas_callers",
            "Direct callers of one symbol (calls, interface/base-method calls, dispatches, constructions, cross-service hops), with call-site lines and confidence. " +
            "codeatlas_explore already includes a caller summary; use this for the full list.",
            new JsonObject
            {
                ["symbol"] = Prop("string", SymbolArgHelp),
                ["includeTests"] = Prop("boolean", "List callers in test code (default false; they are only counted)."),
            }, ["symbol"]),
        Tool("codeatlas_callees",
            "What one method calls, in source order (calls, constructions, dispatch targets, interface implementations, ambiguous candidates). " +
            "codeatlas_explore already includes this for its top matches; use this for one specific symbol.",
            new JsonObject
            {
                ["symbol"] = Prop("string", SymbolArgHelp),
                ["externals"] = Prop("boolean", "Include framework / third-party calls (default false)."),
            }, ["symbol"]),
        Tool("codeatlas_impact",
            "Full impact of changing one symbol: direct callers plus every entry point (HTTP endpoint, handler, job, page…) reachable upwards. " +
            "codeatlas_explore includes a short version; use this before a risky change.",
            new JsonObject
            {
                ["symbol"] = Prop("string", SymbolArgHelp),
                ["includeTests"] = Prop("boolean", "List callers in test code (default false)."),
            }, ["symbol"]),
        Tool("codeatlas_status",
            "Index status: database path, file / symbol / edge counts, whether the index format is outdated, and files changed on disk since indexing (stale).",
            new JsonObject(), []),
    ];

    private static JsonObject Tool(string name, string description, JsonObject properties, string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    public static bool Exists(string name) => name is "codeatlas_explore" or "codeatlas_search" or "codeatlas_callers" or "codeatlas_callees" or "codeatlas_impact" or "codeatlas_status";

    /// <summary>执行一个工具。参数不对、符号找不到、库不存在都是 IsError 的结果（agent 能读到原因），不抛异常。</summary>
    public McpToolResult Call(string name, JsonObject? args)
    {
        args ??= [];
        if (!File.Exists(DbPath))
            return new McpToolResult(L($"索引库不存在：{DbPath}。先运行 codeatlas index \"{RepoRoot}\"（用了 --db 就带同一个 --db）。",
                $"Index database does not exist: {DbPath}. Run codeatlas index \"{RepoRoot}\" first (with the same --db if one was used)."), true);
        // 只读打开：MCP 只查询，不建表、不迁移、不写版本号，也就不会和扩展的索引写入抢写锁。
        IndexDatabase db;
        try { db = IndexDatabase.OpenReadOnly(DbPath); }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new McpToolResult(L($"打不开索引库：{e.Message}。可重新运行 codeatlas index \"{RepoRoot}\"。",
                $"Cannot open the index database: {e.Message}. Try running codeatlas index \"{RepoRoot}\" again."), true);
        }
        using var _ = db;
        // 旧版本工具建的库缺列：读写方式打开会补，MCP 只读不补，查询会报 no such column，这里先给出能照做的提示
        if (db.MissingMigratedColumns() is { Count: > 0 } missing)
            return new McpToolResult(L($"索引库是旧版本 CodeAtlas 建的，缺少列 {string.Join("、", missing.Select(c => c.Table + "." + c.Column))}。运行一次 codeatlas index \"{RepoRoot}\" 即可补齐（MCP 只读，不会自己改库）。",
                $"The index was built by an older CodeAtlas and lacks column(s) {string.Join(", ", missing.Select(c => c.Table + "." + c.Column))}. Run codeatlas index \"{RepoRoot}\" once to add them (MCP is read-only and never modifies the database)."), true);
        return name switch
        {
            "codeatlas_explore" => Explore(db, args),
            "codeatlas_search" => Search(db, args),
            "codeatlas_callers" => WithSymbol(db, args, (s, sb) => Callers(db, s, Bool(args, "includeTests"), sb)),
            "codeatlas_callees" => WithSymbol(db, args, (s, sb) => Callees(db, s, Bool(args, "externals"), sb)),
            "codeatlas_impact" => WithSymbol(db, args, (s, sb) => Impact(db, s, Bool(args, "includeTests"), sb)),
            "codeatlas_status" => Status(db),
            _ => new McpToolResult(L($"未知工具：{name}", $"Unknown tool: {name}"), true),
        };
    }

    private McpToolResult Explore(IndexDatabase db, JsonObject args)
    {
        if (Str(args, "query") is not { Length: > 0 } query) return Missing("query");
        var options = new ExploreOptions(
            MaxChars: Int(args, "maxChars") ?? ExploreBuilder.DefaultMaxChars,
            MaxSymbols: Int(args, "maxSymbols") ?? ExploreBuilder.DefaultMaxSymbols,
            IncludeTests: Bool(args, "includeTests"));
        return new McpToolResult(ExploreBuilder.Build(db, RepoRoot, query, options));
    }

    private static McpToolResult Search(IndexDatabase db, JsonObject args)
    {
        if (Str(args, "query") is not { Length: > 0 } query) return Missing("query");
        var limit = Math.Clamp(Int(args, "limit") ?? 30, 1, 500);
        var hits = new IndexReader(db).SearchSymbols(query, limit);
        if (hits.Count == 0) return new McpToolResult(L("没有匹配。", "No matches."));
        var paths = SymbolMatcher.Paths(db, hits.Select(h => h.FileId).Distinct().ToList());
        var sb = new StringBuilder();
        foreach (var h in hits) sb.AppendLine($"{h.QualifiedName}  [{DbNames.ToDb(h.Kind)}]  {paths.GetValueOrDefault(h.FileId, "?")}:{h.StartLine}  #{h.Id}");
        return new McpToolResult(sb.ToString());
    }

    /// <summary>symbol 参数 → 符号（#id、限定名、短名、后缀）；多于 3 个候选时列出来让 agent 挑。</summary>
    private static McpToolResult WithSymbol(IndexDatabase db, JsonObject args, Action<SymbolRecord, StringBuilder> run)
    {
        if (Str(args, "symbol") is not { Length: > 0 } target) return Missing("symbol");
        var reader = new IndexReader(db);
        var matches = target.StartsWith('#') && long.TryParse(target[1..], out var id)
            ? (reader.GetSymbol(id) is { } one ? [one] : [])
            : OutlineQuery.FindSymbols(db, target);
        var sb = new StringBuilder();
        if (matches.Count == 0)
        {
            sb.AppendLine(L($"未找到符号：{target}", $"Symbol not found: {target}"));
            var suggestions = reader.SearchSymbols(target, 5);
            if (suggestions.Count > 0)
            {
                sb.AppendLine(L("前缀相近的：", "Similar by prefix:"));
                var paths = SymbolMatcher.Paths(db, suggestions.Select(s => s.FileId).Distinct().ToList());
                foreach (var s in suggestions) sb.AppendLine($"  {s.QualifiedName}  [{DbNames.ToDb(s.Kind)}]  {paths.GetValueOrDefault(s.FileId, "?")}:{s.StartLine}  #{s.Id}");
            }
            return new McpToolResult(sb.ToString(), true);
        }
        if (matches.Count > 3)
        {
            var paths = SymbolMatcher.Paths(db, matches.Select(s => s.FileId).Distinct().ToList());
            sb.AppendLine(L($"有 {matches.Count} 个同名符号，请用限定名或 #id：", $"{matches.Count} symbols share this name; use a qualified name or #id:"));
            foreach (var m in matches.Take(50)) sb.AppendLine($"  {m.QualifiedName}  [{DbNames.ToDb(m.Kind)}]  {paths.GetValueOrDefault(m.FileId, "?")}:{m.StartLine}  #{m.Id}");
            return new McpToolResult(sb.ToString());
        }
        foreach (var m in matches)
        {
            run(m, sb);
            sb.AppendLine();
        }
        return new McpToolResult(sb.ToString());
    }

    private static void Callers(IndexDatabase db, SymbolRecord s, bool tests, StringBuilder sb)
    {
        if (ImpactQuery.Build(db, s.Id, new ImpactOptions(Entries: false, Tests: tests)) is not { } impact) return;
        ExploreBuilder.AppendImpactSummary(sb, impact, s.QualifiedName, ImpactBuilder.MaxCallers, 0);
    }

    private static void Impact(IndexDatabase db, SymbolRecord s, bool tests, StringBuilder sb)
    {
        if (ImpactQuery.Build(db, s.Id, new ImpactOptions(Entries: true, Tests: tests)) is not { } impact) return;
        ExploreBuilder.AppendImpactSummary(sb, impact, s.QualifiedName, 50, 200);
    }

    private static void Callees(IndexDatabase db, SymbolRecord s, bool externals, StringBuilder sb)
    {
        if (OutlineQuery.Build(db, s.Id, new OutlineOptions(Externals: externals)) is not { } outline) return;
        sb.AppendLine($"{outline.Symbol.QualifiedName}  [{outline.Symbol.Kind}]  {outline.Symbol.Path}:{outline.Symbol.StartLine}");
        if (outline.Steps.Count == 0) sb.AppendLine(L("  （没有调用）", "  (no calls)"));
        foreach (var step in outline.Steps) ExploreBuilder.AppendStep(sb, step, "  ");
        if (outline.Truncated) sb.AppendLine(L($"  ……超过 {OutlineBuilder.MaxSteps} 步，已截断", $"  … over {OutlineBuilder.MaxSteps} steps, truncated"));
    }

    private McpToolResult Status(IndexDatabase db)
    {
        var reader = new IndexReader(db);
        var files = reader.GetAllFiles();
        var stale = IndexFreshness.CheckAll(RepoRoot, files);
        var sb = new StringBuilder();
        sb.AppendLine(L($"仓库：{RepoRoot}", $"Repository: {RepoRoot}"));
        sb.AppendLine(L($"索引库：{DbPath}", $"Index database: {DbPath}"));
        sb.AppendLine(L($"文件 {files.Count}，符号 {db.Scalar<long>("SELECT COUNT(*) FROM symbols")}，边 {db.Scalar<long>("SELECT COUNT(*) FROM edges")}，待 Roslyn 的 C# 文件 {reader.CountRoslynPendingFiles()}",
            $"{files.Count} files, {db.Scalar<long>("SELECT COUNT(*) FROM symbols")} symbols, {db.Scalar<long>("SELECT COUNT(*) FROM edges")} edges, {reader.CountRoslynPendingFiles()} C# files pending Roslyn"));
        if (db.NeedsRebuild) sb.AppendLine(L("索引格式过旧：请运行 codeatlas index <repo> --full。", "Index format is outdated: run codeatlas index <repo> --full."));
        if (stale.Count == 0) sb.AppendLine(L("所有已索引文件的大小 / 修改时间与索引一致。", "All indexed files match the index (size / modified time)."));
        else
        {
            sb.AppendLine(L($"{stale.Count} 个文件在建索引后改过（重新运行 codeatlas index 更新）：", $"{stale.Count} file(s) changed after indexing (re-run codeatlas index to update):"));
            foreach (var s in stale.Take(50)) sb.AppendLine($"  - {s.Path}  ({s.Reason})");
            if (stale.Count > 50) sb.AppendLine($"  - … +{stale.Count - 50}");
        }
        return new McpToolResult(sb.ToString());
    }

    private static McpToolResult Missing(string name) => new(L($"缺少参数：{name}", $"Missing argument: {name}"), true);

    private static string? Str(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

    private static int? Int(JsonObject args, string name)
    {
        if (args[name] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null;
    }

    private static bool Bool(JsonObject args, string name) =>
        args[name] is JsonValue v && ((v.TryGetValue<bool>(out var b) && b) || (v.TryGetValue<string>(out var s) && s.Equals("true", StringComparison.OrdinalIgnoreCase)));
}
