using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Tests.Architecture;

namespace CodeAtlas.Core.Tests;

/// <summary>容器图扫描的英文输出：节点 key 不随语言变，外部系统显示名与证据说明翻成英文。</summary>
[Collection("Locale")]
public class LocaleArchitectureTests
{
    [Fact]
    public void Scan_NodeKeysAreLanguageIndependent_ExternalNamesFollowLanguage()
    {
        using var fx = new JavaFixture();
        var zh = ArchitectureScanner.Scan(fx.Root);
        ArchModel en;
        using (Loc.Use("en")) en = ArchitectureScanner.Scan(fx.Root);

        Assert.Equal(zh.Nodes.Select(n => n.Key).Order(StringComparer.Ordinal), en.Nodes.Select(n => n.Key).Order(StringComparer.Ordinal));
        Assert.Equal(zh.Edges.Select(e => $"{e.FromKey} → {e.ToKey} {e.Protocol}"), en.Edges.Select(e => $"{e.FromKey} → {e.ToKey} {e.Protocol}"));

        string Name(ArchModel m, string key) => m.Nodes.Single(n => n.Key == key).Name;
        Assert.Equal("数据库（JPA）", Name(zh, "external:database:数据库-jpa"));
        Assert.Equal("Database (JPA)", Name(en, "external:database:数据库-jpa"));
        Assert.Equal("SMTP 邮件服务", Name(zh, "external:email:smtp-邮件服务"));
        Assert.Equal("SMTP mail service", Name(en, "external:email:smtp-邮件服务"));
        Assert.Equal("H2（内嵌）", Name(zh, "external:database:h2-内嵌"));
        Assert.Equal("H2 (embedded)", Name(en, "external:database:h2-内嵌"));
        Assert.Equal("PostgreSQL", Name(en, "external:database:postgresql"));
        Assert.All(en.Nodes.Where(n => n.Kind == ArchNodeKind.External), n => Assert.DoesNotMatch("[一-鿿]", n.Name));
    }

    [Fact]
    public void ExternalNames_EnglishVocabulary()
    {
        Assert.All(ExternalNames.Entries, p => Assert.DoesNotMatch("[一-鿿]", p.Value));

        Assert.Equal("数据库（JPA）", ExternalNames.Display("数据库（JPA）"));
        Assert.Equal("SQLite（内嵌）", ExternalNames.Display(PackageReferenceRule.SQLite));
        using var _ = Loc.Use("en");
        Assert.Equal("Database (JPA)", ExternalNames.Display("数据库（JPA）"));
        Assert.Equal("SQLite (embedded)", ExternalNames.Display(PackageReferenceRule.SQLite));
        Assert.Equal("IdentityServer (in-process)", ExternalNames.Display("IdentityServer（本进程内）"));
        Assert.Equal("SMTP mail service", ExternalNames.Display("SMTP 邮件服务"));
        Assert.Equal("Eureka service registry", ExternalNames.Display("Eureka 服务注册中心"));
        Assert.Equal("AI endpoint (OpenAI-compatible)", ExternalNames.Display("AI 端点（OpenAI 兼容）"));
        Assert.Equal("api.example.com", ExternalNames.Display("api.example.com"));
        Assert.Equal("Billing", ExternalNames.Display("Billing"));
    }

    [Fact]
    public void Descriptions_And_EvidenceNotes_InEnglish()
    {
        using var fx = new JavaFixture();
        using var _ = Loc.Use("en");
        Assert.Equal("Spring Cloud Gateway routes, Eureka registration, config server clients", new SpringCloudRule().Description);
        foreach (var rule in ArchitectureScanner.DefaultRules) Assert.DoesNotMatch("[一-鿿]", rule.Description);

        var clinic = ProjectInventory.Load(fx.Root).JavaModules.Single(m => m.Dir == "clinic");
        Assert.Contains(clinic.DeployEvidence, e => e.Note == "Spring Boot default port 8080");
    }
}
