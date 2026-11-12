using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// 合成边：TS 事件频道配对。夹具 Fixtures/events：bus.ts 里 OrderService.place 发 order:placed / order:audited / error / 动态频道，
/// listeners.ts 里 register 用 on / addListener / off 注册，Auditor.start 用 once + this.onAudited。
/// </summary>
public class EventChannelTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-events-" + Guid.NewGuid().ToString("N"));
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();
    private readonly List<ILanguageAdapter> _adapters = [new TypeScriptAdapter()];

    public EventChannelTests()
    {
        var from = TypeScriptAdapterTests.Fixture("events");
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_repo, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private async Task<IndexReader> IndexAsync()
    {
        await new TreeSitterIndexer(_db, _adapters, new HeuristicEdgeResolver()).IndexAsync(_repo);
        return new IndexReader(_db);
    }

    private static List<(string To, string? Via, double Confidence, int Line)> Out(IndexReader reader, string qualifiedName) =>
        reader.GetNeighbors(reader.FindByQualifiedName(qualifiedName).Single().Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls })
            .Select(n => (n.Edge.ToName, n.Edge.Via, n.Edge.Confidence, n.Edge.Line))
            .OrderBy(x => x.Line).ThenBy(x => x.ToName, StringComparer.Ordinal).ToList();

    [Fact]
    public void Adapter_RecordsListenAndEmitMarkers_ForLiteralChannelsOnly()
    {
        using var ts = new TypeScriptAdapter();
        var bus = ts.Parse("src/bus.ts", File.ReadAllText(Path.Combine(TypeScriptAdapterTests.Fixture("events"), "src", "bus.ts")));
        Assert.Equal(new (string, string?, int)[] { ("place", "emit:order:placed", 8), ("place", "emit:order:audited", 9), ("place", "emit:error", 10) },
            bus.References.Where(r => r.Via?.StartsWith(EventChannelLinker.EmitPrefix) == true).Select(r => (bus.Symbols[r.FromIndex].Name, r.Via, r.Line)));

        var listeners = ts.Parse("src/listeners.ts", File.ReadAllText(Path.Combine(TypeScriptAdapterTests.Fixture("events"), "src", "listeners.ts")));
        Assert.Equal(new (string, string, string?, int)[]
        {
            ("start", "onAudited", "listen:order:audited", 17),     // bus.once(..., this.onAudited)
            ("register", "sendMail", "listen:order:placed", 26),
            ("register", "updateStats", "listen:order:placed", 27), // addListener
            ("register", "logError", "listen:error", 28),
        }, listeners.References.Where(r => r.Via?.StartsWith(EventChannelLinker.ListenPrefix) == true).Select(r => (listeners.Symbols[r.FromIndex].Name, r.Name, r.Via, r.Line)));
        // 回调引用照旧（off 摘监听不算；箭头函数不是函数引用）
        Assert.Equal(new[] { "onAudited", "sendMail", "updateStats", "logError" },
            listeners.References.Where(r => r.Via == HeuristicEdgeResolver.ScriptCallbackVia).Select(r => r.Name));
    }

    [Fact]
    public async Task Index_SynthesizesLowConfidenceEventEdges_FromEmitterToHandlers_SkippingGenericChannels()
    {
        var reader = await IndexAsync();
        Assert.Equal(new (string, string?, double, int)[]
        {
            ("src/bus.ts:save", null, 0.9, 7),
            ("src/listeners.ts:sendMail", "event:order:placed", EventChannelLinker.Confidence, 8),
            ("src/listeners.ts:updateStats", "event:order:placed", EventChannelLinker.Confidence, 8),
            ("src/listeners.ts:Auditor.onAudited", "event:order:audited", EventChannelLinker.Confidence, 9),
            // emit("error") 不配：通用事件名；emit(channelOf(id)) 不是字面量
            ("src/bus.ts:channelOf", null, 0.9, 11),
        }, Out(reader, "src/bus.ts:OrderService.place").Where(c => !c.To.StartsWith("external:", StringComparison.Ordinal)));
        // 注册本身仍是 callback 边（注册函数 → 处理函数），标记不产生任何边
        Assert.Equal(new (string, string?, double, int)[] { ("src/listeners.ts:sendMail", "callback", 0.85, 26), ("src/listeners.ts:updateStats", "callback", 0.85, 27), ("src/listeners.ts:logError", "callback", 0.85, 28) },
            Out(reader, "src/listeners.ts:register").Where(c => !c.To.StartsWith("external:", StringComparison.Ordinal)));
        Assert.Equal(3L, _db.Scalar<long>("SELECT COUNT(*) FROM edges WHERE via LIKE 'event:%'"));
    }

    [Fact]
    public async Task Reindex_RecomputesEventEdges_WhenOnlyTheListenerFileChanges()
    {
        await IndexAsync();
        var path = Path.Combine(_repo, "src", "listeners.ts");
        File.WriteAllText(path, File.ReadAllText(path).Replace("  bus.addListener(\"order:placed\", updateStats);\n", "").Replace("  bus.addListener(\"order:placed\", updateStats);\r\n", ""));
        var reader = await IndexAsync(); // 增量：只重解析 listeners.ts，bus.ts 的 emit 标记从库里读
        Assert.Equal(new (string, string?)[] { ("src/listeners.ts:sendMail", "event:order:placed"), ("src/listeners.ts:Auditor.onAudited", "event:order:audited") },
            Out(reader, "src/bus.ts:OrderService.place").Where(c => c.Via?.StartsWith("event:") == true).Select(c => (c.To, c.Via)));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { }
    }
}
