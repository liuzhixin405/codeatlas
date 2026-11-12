using System.Reflection;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 框架流程 · ASP.NET Core 与 Spring 的英文输出：阶段名、节点标签（"控制器" → "Controllers"，与扩展的约定）、Note、Notes 都是英文且没有中文残留；
/// 行为后置的 Note 后缀（MessagePipeline 按它认收尾列）在英文下照样认得出。夹具复用 FrameworkFlowTests / SpringFrameworkFlowTests 的。
/// </summary>
[Collection("Locale")]
public class LocaleFrameworkFlowTests
{
    private static List<FlowNodeDto> All(FlowNodeDto n) => [n, .. n.Children.SelectMany(All)];

    private static void NoChinese(FrameworkFlowDto f)
    {
        Assert.DoesNotMatch("[一-鿿]", f.Flow.EntryLabel);
        Assert.All(f.Stages, s => Assert.DoesNotMatch("[一-鿿]", s.Label));
        Assert.All(f.Flow.Actors, a => Assert.DoesNotMatch("[一-鿿]", a.Label));
        Assert.All(All(f.Flow.Root), n =>
        {
            Assert.DoesNotMatch("[一-鿿]", n.Label);
            Assert.DoesNotMatch("[一-鿿]", n.Note ?? "");
        });
        Assert.All(f.Notes, n => Assert.DoesNotMatch("[一-鿿]", n));
    }

    [Fact]
    public void AspNetCore_mediatr_flow_in_english()
    {
        using var _ = Loc.Use("en");
        var f = FrameworkFlowBuilder.Build(FrameworkFlowTests.MediatRFixture());

        Assert.Equal(["middleware", "controller", "dispatcher", "behavior", "handler", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal(["Middleware", "Controllers", "Dispatcher", "Pipeline behaviors", "Handler", "Teardown"], f.Stages.Select(s => s.Label));

        var root = f.Flow.Root;
        Assert.Equal(("HTTP request", "entry", "middleware"), (root.Label, root.Kind, root.Actor));
        Assert.Equal(["UseExceptionHandler", "RequestTimingMiddleware.InvokeAsync", "UseAuthentication", "UseAuthorization", "ApiExceptionFilter.OnException", "Controllers"],
            root.Children.Select(c => c.Label));
        var timing = root.Children[1];
        Assert.StartsWith("custom middleware (UseMiddleware, Program.cs:", timing.Note);
        Assert.Equal("after next (runs on the way back with the response)", timing.Children[1].Note);
        Assert.StartsWith("MVC global filter (", root.Children[4].Note);

        var controllers = root.Children[5];
        Assert.Equal(("controllers", "Controllers"), (controllers.Kind, controllers.Label));
        var dispatcher = Assert.Single(controllers.Children);
        Assert.StartsWith("MediatR (external library, 2 call sites", dispatcher.Note);
        Assert.Contains("conditional", dispatcher.Children[1].Note);
        var uow = Assert.Single(dispatcher.Children[3].Children);
        Assert.StartsWith("Some handlers commit themselves (1 handler calls it directly; 3 handlers in total)", uow.Note);

        // 行为后置：Note 以 "XxxBehavior" + 共用后缀开头，按洋葱倒序
        Assert.Equal(["TransactionBehavior (after next) · in a callback / lambda", "TransactionBehavior (after next) · in a callback / lambda", "AuditBehavior (after next)", "LoggingBehavior (after next)"],
            dispatcher.Children.Skip(4).Select(t => t.Note));
        Assert.Contains(f.Notes, n => n.StartsWith("Middleware in the order of the app.UseXxx calls in Program.cs", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.StartsWith("Dispatcher: MediatR (external library) ISender.Send, 2 call sites", StringComparison.Ordinal));
        NoChinese(f);
    }

    [Fact]
    public void Message_pipeline_finds_behavior_post_steps_in_both_languages()
    {
        (int Posts, IReadOnlyList<string> Skipped) Run(string lang)
        {
            using var _ = Loc.Use(lang);
            var input = FrameworkFlowTests.MediatRFixture();
            var fw = FrameworkFlowBuilder.Build(input);
            var pipe = MessagePipeline.From(fw, input.Symbols, input.Edges, input.Sources.ReadFile);
            Assert.NotNull(pipe);
            var (steps, skipped) = pipe!.For("CreateOrderCommand");
            return (steps.Sum(s => s.Post.Count), skipped);
        }

        var zh = Run("zh");
        var en = Run("en");
        Assert.True(zh.Posts > 0);
        Assert.Equal(zh.Posts, en.Posts);   // 英文下 " (after next)" 后缀照样认出行为后置
        Assert.Equal(["AuditBehavior (CreateOrderCommand does not implement IAuditableCommand)"], en.Skipped);
        Assert.Equal(["AuditBehavior（CreateOrderCommand 没实现 IAuditableCommand）"], zh.Skipped);
    }

    // Spring 夹具是 SpringFrameworkFlowTests 里的私有方法（私有嵌套类 Repo）：反射取出来，避免抄一遍几百行
    private static FrameworkInput SpringMvcInput()
    {
        var fixture = typeof(SpringFrameworkFlowTests).GetMethod("MvcFixture", BindingFlags.NonPublic | BindingFlags.Static)!;
        var repo = fixture.Invoke(null, null)!;
        var input = repo.GetType().GetMethod("Input")!;
        return (FrameworkInput)input.Invoke(repo, ["shop-api", new[] { "shop-api" }])!;
    }

    [Fact]
    public void Spring_mvc_flow_in_english()
    {
        using var _ = Loc.Use("en");
        var f = FrameworkFlowBuilder.Build(SpringMvcInput());

        Assert.Equal(["middleware", "dispatcher", "interceptor", "controller", "aspect", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal(["Filters", "Dispatcher", "Interceptors", "Controllers", "Aspects", "Teardown"], f.Stages.Select(s => s.Label));

        var root = f.Flow.Root;
        Assert.Equal(("HTTP request", "entry", "middleware"), (root.Label, root.Kind, root.Actor));
        Assert.Equal(["AuditFilter.doFilter", "Spring Security filter chain", "RequestIdFilter.doFilterInternal", "CorsFilter", "DispatcherServlet (built into Spring)",
                      "MdcSupport.clear", "SecurityAudit.record", "AuditLog.finish"], root.Children.Select(c => c.Label));
        Assert.Contains("FilterRegistrationBean (FilterConfig.java:6)", root.Children[0].Note);
        Assert.Contains("paths /api/*", root.Children[0].Note);
        Assert.Contains("configured: csrf (disabled), sessionManagement, authorizeHttpRequests", root.Children[1].Note);
        Assert.Equal(["RequestIdFilter after", "JwtAuthFilter after", "AuditFilter after"], root.Children.Skip(5).Select(c => c.Note));

        var controllers = All(root).Single(n => n.Kind == "controllers");
        Assert.Equal("Controllers", controllers.Label);
        Assert.Contains(f.Notes, n => n.StartsWith("Filters are ordered by FilterRegistrationBean.setOrder", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.StartsWith("Plus 1 non-HTTP entry point", StringComparison.Ordinal));
        NoChinese(f);
    }
}
