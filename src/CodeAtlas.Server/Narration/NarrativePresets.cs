namespace CodeAtlas.Server.Narration;

/// <summary>一个预设版式：模型只能从这里选一个 id，不自己画。</summary>
public sealed record LayoutPreset(string Id, string Description);

/// <summary>三种叙述对象各自的预设版式。页面侧按 id 切换排版。</summary>
public static class NarrativePresets
{
    public const string Container = "container";
    public const string Component = "component";
    public const string Flow = "flow";
    public static readonly string[] Kinds = [Container, Component, Flow];

    private static IReadOnlyList<LayoutPreset> ContainerPresets =>
    [
        new("columns", L("三列：左边使用者 / 前端，中间容器，右边外部系统（默认，适合容器少、外部系统多）", "Three columns: users / frontends on the left, containers in the middle, external systems on the right (default; suits few containers and many external systems)")),
        new("topological", L("按边的方向分层从左到右排（上游在左、下游在右；适合容器之间有明显的调用链）", "Layered left to right along edge direction (upstream on the left, downstream on the right; suits clear call chains between containers)")),
    ];
    private static IReadOnlyList<LayoutPreset> ComponentPresets =>
    [
        new("dependency-order", L("被依赖者在前的下三角矩阵（默认，环会露出在对角线上方）", "Lower-triangular matrix with dependencies first (default; cycles show up above the diagonal)")),
        new("alphabetical", L("按组件名字母序（适合组件多、依赖稀疏、只想查某一个）", "Alphabetical by component name (suits many components, sparse dependencies, looking up a specific one)")),
    ];
    private static IReadOnlyList<LayoutPreset> FlowPresets =>
    [
        new("first-seen", L("参与者按首次出现的顺序排列（默认，读起来和调用顺序一致）", "Actors in order of first appearance (default; reads in call order)")),
        new("grouped-external", L("外部 / 框架参与者合并到最右边的一列（适合外部调用很多、只想看自己代码的）", "External / framework actors merged into the rightmost column (suits many external calls when you only want to see your own code)")),
    ];

    public static IReadOnlyList<LayoutPreset> For(string kind) => kind switch
    {
        Container => ContainerPresets,
        Component => ComponentPresets,
        Flow => FlowPresets,
        _ => [],
    };

    public static bool IsValidKind(string? kind) => kind is not null && Kinds.Contains(kind);
}
