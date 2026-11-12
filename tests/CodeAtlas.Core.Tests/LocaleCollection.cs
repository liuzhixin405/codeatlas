namespace CodeAtlas.Core.Tests;

/// <summary>切换输出语言（Loc.Use）的测试放进这个集合：不与其他测试并行，免得全局语言串到中文断言里。</summary>
[CollectionDefinition("Locale", DisableParallelization = true)]
public sealed class LocaleCollection;
