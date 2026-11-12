import { L } from "./l10n";
import type { NarrativeKind } from "./types";

/**
 * 预设版式：AI 只能在这里面选一个，不自己画。
 * 服务端 NarrativeDto.layout 只会是这些 id 或 null；页面的「版式」下拉也只列这些。
 */
export const LayoutPresets: Record<NarrativeKind, Array<{ id: string; label: string; title: string }>> = {
  container: [
    { id: "columns", label: L("三列", "Three columns"), title: L("使用者 / 前端 | 服务容器 | 外部系统 三列固定", "Users / frontends | service containers | external systems, in three fixed columns") },
    { id: "topological", label: L("拓扑分层", "Topological layers"), title: L("按依赖方向分层，上游在左、下游在右；环内按名字", "Layered by dependency direction, upstream on the left, downstream on the right; cycles ordered by name") },
  ],
  component: [
    { id: "dependency-order", label: L("依赖序", "Dependency order"), title: L("服务端排好的拓扑序：依赖排成下三角，环合并成块", "Topological order from the server: dependencies form a lower triangle, cycles merge into blocks") },
    { id: "alphabetical", label: L("字母序", "Alphabetical"), title: L("组件按名字字母序排列", "Components sorted alphabetically by name") },
  ],
  flow: [
    { id: "first-seen", label: L("首次出现", "First seen"), title: L("参与者按在流程里首次出现的顺序排列，外部系统各占一列", "Participants in order of first appearance in the flow, one column per external system") },
    { id: "grouped-external", label: L("外部合并", "Group externals"), title: L("所有外部系统合并成一列「外部」", "Merge all external systems into one \"External\" column") },
  ],
};

export const DefaultPreset: Record<NarrativeKind, string> = {
  container: "columns",
  component: "dependency-order",
  flow: "first-seen",
};

/** 预设 id → 显示名；不认识的 id 原样返回。 */
export function presetLabel(kind: NarrativeKind, id: string): string {
  return LayoutPresets[kind].find((p) => p.id === id)?.label ?? id;
}

/** 服务端返回的 layout 是否是该粒度合法的预设 id。 */
export function isKnownPreset(kind: NarrativeKind, id: string | null): id is string {
  return id !== null && LayoutPresets[kind].some((p) => p.id === id);
}
