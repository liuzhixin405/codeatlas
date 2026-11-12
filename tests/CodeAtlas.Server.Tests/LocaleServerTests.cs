using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using CodeAtlas.Server.Narration;

namespace CodeAtlas.Server.Tests;

/// <summary>英文模式下 Server 侧的叙述提示词、叙述缓存按语言分行、DSM / 预设版式等给人看的文字。</summary>
[Collection("Locale")]
public class LocaleServerTests
{
    private const string Cjk = "[一-鿿]";

    [Fact]
    public void Prompts_follow_language()
    {
        var facts = FakeChat.ContainerFacts();
        using (Loc.Use("en"))
        {
            var (system, user) = Prompts.Build(facts);
            Assert.Contains("Write a short narration in English", system);
            Assert.Contains("summary is at most 60 words", system);
            Assert.Contains("\"columns\": Three columns", system); // 预设说明也是英文
            Assert.Contains("{\"summary\": \"…\", \"keyPoints\": [{\"text\": \"…\", \"evidence\": [\"id\", \"…\"]}]", system);
            Assert.DoesNotMatch(Cjk, system);
            Assert.StartsWith("Facts (one per line, ids in square brackets):\n\n", user);
            Assert.EndsWith("\nOutput the JSON according to the rules.", user);
            Assert.Contains(facts.Text, user);
        }
        using (Loc.Use("zh"))
        {
            var (system, user) = Prompts.Build(facts);
            Assert.Contains("用简体中文", system);
            Assert.Contains("summary 不超过 120 个汉字", system);
            Assert.StartsWith("事实（每行一条，方括号里是 id）：", user);
            Assert.EndsWith("\n请按规则输出 JSON。", user);
        }
    }

    [Fact]
    public async Task Narration_cache_rows_are_per_language()
    {
        var handler = new FakeChatHandler()
            .Reply(FakeChat.ValidJson(["node:k"]))
            .Reply(FakeChat.ValidJson(["node:k"], summary: "K is a web container."));
        using var svc = FakeChat.Service(handler);

        using (Loc.Use("zh"))
        {
            var zh = await svc.GenerateAsync("container", "k", force: false, CancellationToken.None);
            Assert.Equal(("ready", "K 是一个 Web 容器。"), (zh.Dto.Status, zh.Dto.Summary));
        }
        using (Loc.Use("en"))
        {
            // 中文那行不能当英文的缓存命中：要另外生成一次，提示词是英文
            var en = await svc.GenerateAsync("container", "k", force: false, CancellationToken.None);
            Assert.Equal(("ready", "K is a web container.", 1), (en.Dto.Status, en.Dto.Summary, en.Attempts));
            Assert.Equal(2, handler.Calls);
            Assert.Contains("in English", handler.RequestBodies[1]);
            Assert.Equal("K is a web container.", svc.Get("container", "k").Summary);
        }
        using (Loc.Use("zh"))
        {
            // 英文生成没有盖掉中文那行；两边再取都命中各自的缓存，不再调端点
            var again = await svc.GenerateAsync("container", "k", force: false, CancellationToken.None);
            Assert.Equal((0, "K 是一个 Web 容器。"), (again.Attempts, again.Dto.Summary));
            Assert.Equal("K 是一个 Web 容器。", svc.Get("container", "k").Summary);
        }
        using (Loc.Use("en"))
            Assert.Equal("K is a web container.", svc.Get("container", "k").Summary);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void Dsm_notes_presets_and_flow_categories_in_english()
    {
        using var _ = Loc.Use("en");

        var ghost = new ArchNode("ghost", ArchNodeKind.Container, "dotnet-web", "ghost", "ghost", ArchSource.Discovered, [], ["ghost/dir"], []);
        var empty = DsmBuilder.Build(new List<MapSymbol>(), new List<MapEdge>(), ghost, "", null);
        Assert.Equal("No symbols indexed under the container's member directories (submodule not indexed?)", empty.Note);
        var nope = DsmBuilder.Build(new List<MapSymbol>(), new List<MapEdge>(), ghost, "Acme.Nope", null);
        Assert.Equal("No symbols under Acme.Nope", nope.Note);
        Assert.Equal(("(this level)", "(global namespace)"), (DsmBuilder.SelfLabel, DsmBuilder.GlobalLabel));

        var component = NarrativePresets.For(NarrativePresets.Component);
        Assert.Equal(new[] { "dependency-order", "alphabetical" }, component.Select(p => p.Id)); // id 不随语言变
        Assert.Equal("Lower-triangular matrix with dependencies first (default; cycles show up above the diagonal)", component[0].Description);
        Assert.All(NarrativePresets.Kinds.SelectMany(NarrativePresets.For), p => Assert.DoesNotMatch(Cjk, p.Description));

        Assert.Equal("Database", FlowBuilder.ExternalCategory("external:JpaRepository.save"));
        Assert.Equal("File", FlowBuilder.ExternalCategory("external:System.IO.File.ReadAllText(string)"));
    }
}
