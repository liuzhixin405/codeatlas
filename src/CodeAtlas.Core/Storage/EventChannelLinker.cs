using System.Text.RegularExpressions;

namespace CodeAtlas.Core.Storage;

/// <summary>
/// 合成边：TS / JS 的事件频道配对（参考 CodeGraph 的 synthesizers）。适配器在原始引用里留两种标记（不直接建边，解析器跳过它们）：
/// <list type="bullet">
/// <item><c>listen:&lt;频道&gt;</c>：<c>x.on('频道', handler)</c>（once / addListener / prependListener / addEventListener 同理）里 handler 那个实参，
/// 与同一处的 via = callback 引用同一个 pos——回调边解析出的目标就是处理函数；</item>
/// <item><c>emit:&lt;频道&gt;</c>：<c>x.emit('频道', …)</c> 所在的函数。</item>
/// </list>
/// 每次 tree-sitter 解析完整趟重算（先删掉全部 via = event:… 的边再插）：从发出频道的函数连一条 calls 边到同名频道的每个处理函数，
/// via = <c>event:&lt;频道&gt;</c>，confidence = <see cref="Confidence"/>（README「怎么读 confidence」的"合成边"一档）。
/// 只按频道名配对，不看发出者与监听者是不是同一个对象，所以是推断、不是确定调用。
/// 不配对：内置 / 通用事件名（data、error、close、click……，<see cref="GenericChannels"/>）；一个频道的处理函数或发出处超过
/// <see cref="MaxListeners"/> / <see cref="MaxEmitters"/> 个（太泛，配出来全是噪声）；函数发给自己。
/// 回调 / observer 注册本身（注册函数 → 处理函数，via = callback）与 JSX 渲染（父组件 → 子组件，via = render）在解析阶段已有，不在这里。
/// </summary>
public sealed partial class EventChannelLinker(IndexDatabase db)
{
    public const string ListenPrefix = "listen:";
    public const string EmitPrefix = "emit:";
    public const string ViaPrefix = "event:";
    /// <summary>合成边的 confidence：高于多候选（0.3），刚好达到时序图 / 流程的 0.4 门槛，低于任何按名字或类型解析出的直接调用。</summary>
    public const double Confidence = 0.4;
    public const int MaxListeners = 10;
    public const int MaxEmitters = 50;
    public const int MaxChannelLength = 100;

    /// <summary>Node 流 / 进程 / 套接字与 DOM 的通用事件：到处都有同名的 on / emit，按名字配对没有意义。</summary>
    public static readonly IReadOnlySet<string> GenericChannels = new HashSet<string>(StringComparer.Ordinal)
    {
        "data", "end", "error", "close", "finish", "open", "message", "connect", "connection", "disconnect", "ready", "change", "exit",
        "drain", "readable", "response", "request", "timeout", "abort", "load", "unload", "resize", "scroll", "input", "submit",
        "click", "keydown", "keyup", "keypress", "focus", "blur", "mousedown", "mouseup", "mousemove", "visibilitychange", "beforeunload",
        "uncaughtException", "unhandledRejection", "SIGINT", "SIGTERM", "listening", "upgrade", "pipe", "unpipe", "newListener", "removeListener",
    };

    /// <summary>频道名：去掉首尾空白；空、过长或含换行的不算（返回 null）。</summary>
    public static string? NormalizeChannel(string raw)
    {
        var c = raw.Trim();
        return c.Length == 0 || c.Length > MaxChannelLength || NewLine().IsMatch(c) ? null : c;
    }

    [GeneratedRegex(@"[\r\n]")]
    private static partial Regex NewLine();

    /// <summary>重算全部频道合成边，返回插入的条数。可重复执行。</summary>
    public int Link()
    {
        db.Execute($"DELETE FROM edges WHERE via LIKE '{ViaPrefix}%'");
        // 处理函数：listen 标记 → 同一处（from_id + pos）的回调边解析出的仓库函数 / 方法
        var listeners = new List<(string Channel, long Handler, string HandlerName, int Line)>();
        using (var cmd = db.CreateCommand($"""
            SELECT DISTINCT substr(li.via, {ListenPrefix.Length + 1}), cb.to_id, h.qualified_name, li.line
            FROM source_references li
            JOIN edges cb ON cb.from_id = li.from_id AND cb.pos = li.pos AND cb.kind = 'calls' AND cb.via = 'callback' AND cb.to_id IS NOT NULL
            JOIN symbols h ON h.id = cb.to_id AND h.kind IN ('function', 'method')
            WHERE li.via LIKE '{ListenPrefix}%'
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) listeners.Add((r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3)));

        var emitters = new List<(string Channel, long From, long FileId, int Line, int Pos)>();
        using (var cmd = db.CreateCommand($"""
            SELECT substr(em.via, {EmitPrefix.Length + 1}), em.from_id, em.file_id, em.line, em.pos
            FROM source_references em JOIN symbols f ON f.id = em.from_id
            WHERE em.via LIKE '{EmitPrefix}%'
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) emitters.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3), r.GetInt32(4)));

        var byChannel = listeners.GroupBy(l => l.Channel, StringComparer.Ordinal)
            .Select(g => (Channel: g.Key, Handlers: g.DistinctBy(l => l.Handler).ToList()))
            .Where(g => !GenericChannels.Contains(g.Channel) && g.Handlers.Count <= MaxListeners)
            .ToDictionary(g => g.Channel, g => g.Handlers, StringComparer.Ordinal);
        var inserted = 0;
        foreach (var group in emitters.GroupBy(e => e.Channel, StringComparer.Ordinal))
        {
            if (!byChannel.TryGetValue(group.Key, out var handlers)) continue;
            var sites = group.DistinctBy(e => (e.From, e.Line, e.Pos)).ToList();
            if (sites.Count > MaxEmitters) continue;
            foreach (var e in sites)
                foreach (var h in handlers)
                {
                    if (h.Handler == e.From) continue;
                    inserted += db.Execute("""
                        INSERT INTO edges (from_id, to_id, to_name, kind, source, confidence, file_id, line, via, evidence_line, pos, ctx)
                        VALUES ($from, $to, $name, 'calls', 'treesitter', $conf, $file, $line, $via, $evidence, $pos, NULL)
                        """, ("$from", e.From), ("$to", h.Handler), ("$name", h.HandlerName), ("$conf", Confidence), ("$file", e.FileId),
                        ("$line", e.Line), ("$via", ViaPrefix + group.Key), ("$evidence", h.Line), ("$pos", e.Pos));
                }
        }
        return inserted;
    }
}
