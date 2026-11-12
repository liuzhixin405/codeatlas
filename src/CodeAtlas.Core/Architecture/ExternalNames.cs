namespace CodeAtlas.Core.Architecture;

/// <summary>
/// 外部系统节点的显示名。规则里登记外部系统时传的是规范名（中文词表原文，如"SMTP 邮件服务""数据库（JPA）"），
/// key 按规范名算、不随语言变；写进容器图的显示名在英文下经这里翻译。不在词表里的名字（主机名、包名、服务名）原样返回。
/// </summary>
public static class ExternalNames
{
    /// <summary>整名对照：规范名 → 英文。</summary>
    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal)
    {
        ["数据库"] = "Database",
        ["身份认证服务"] = "Identity service",
        ["HTTP 服务"] = "HTTP service",
        ["gRPC 服务"] = "gRPC service",
        ["SMTP 邮件服务"] = "SMTP mail service",
        ["钉钉开放平台"] = "DingTalk Open Platform",
        ["企业微信 API"] = "WeCom API",
        ["AI 端点（OpenAI 兼容）"] = "AI endpoint (OpenAI-compatible)",
        ["AI 端点"] = "AI endpoint",
        ["Eureka 服务注册中心"] = "Eureka service registry",
        ["Spring Cloud Config 配置中心"] = "Spring Cloud Config server",
        ["Nacos 配置中心"] = "Nacos config center",
        ["Celery 任务队列"] = "Celery task queue",
    };

    /// <summary>带括号限定的名字：前缀词 / 括号里的限定词分别对照，如"数据库（JPA）"→"Database (JPA)"、"SQLite（内嵌）"→"SQLite (embedded)"。</summary>
    private static readonly Dictionary<string, string> Qualifiers = new(StringComparer.Ordinal)
    {
        ["内嵌"] = "embedded",
        ["本进程内"] = "in-process",
        ["OpenAI 兼容"] = "OpenAI-compatible",
    };

    public static string Display(string canonical) => Loc.IsEnglish ? English(canonical) : canonical;

    /// <summary>英文名；只按词表翻，不认识的部分原样保留。</summary>
    public static string English(string canonical)
    {
        if (Exact.TryGetValue(canonical, out var en)) return en;
        var open = canonical.IndexOf('（');
        if (open > 0 && canonical.EndsWith('）'))
        {
            var head = canonical[..open].TrimEnd();
            var inner = canonical[(open + 1)..^1];
            var headEn = Exact.TryGetValue(head, out var h) ? h : head;
            var innerEn = Qualifiers.TryGetValue(inner, out var q) ? q : inner;
            if (headEn != head || innerEn != inner) return $"{headEn} ({innerEn})";
        }
        return canonical;
    }

    /// <summary>词表本身（测试用：英文名里不能再有中文）。</summary>
    public static IEnumerable<KeyValuePair<string, string>> Entries => Exact.Concat(Qualifiers);
}
