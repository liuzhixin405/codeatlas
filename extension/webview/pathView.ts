import { L } from "./l10n";
import type { CallPathsDto, SymbolDto } from "./types";

/**
 * 两点间调用路径（调用图工具栏「⇢ 路径到…」、网状图右键「从焦点到这里的路径」）的纯逻辑，不碰 DOM：
 * - PathPicker：点「路径到…」时记下起点 A，下一次在搜索框里选中的符号就是终点 B；Esc / 再点一次取消。
 * - pathStats / pathLegend：标题区的统计与图例文字。
 * 画图复用时序图渲染器（SequenceDiagram，collapseDepth = Infinity 整条路径摊开），数据是服务端合并好的 CallPathsDto.flow。
 */
export type PathPick = { from: SymbolDto; to: SymbolDto } | { error: string };

export class PathPicker {
  private start: SymbolDto | null = null;

  /** 正在等终点。 */
  get armed(): boolean {
    return this.start !== null;
  }

  get from(): SymbolDto | null {
    return this.start;
  }

  /** 以 from 为起点开始选终点；from 为空（还没有焦点）返回提示文字。 */
  arm(from: SymbolDto | null): string | null {
    if (!from) return L("先在调用图里选一个起点（焦点），再点「路径到…」", "Pick a start (focus) in the Call Graph first, then click \"Path to…\"");
    this.start = from;
    return null;
  }

  cancel(): void {
    this.start = null;
  }

  /** 选中终点：返回起止两端并解除等待；终点与起点相同时报错（仍在等）。 */
  take(to: SymbolDto): PathPick {
    const from = this.start;
    if (!from) return { error: L("没有起点", "No start") };
    if (from.id === to.id) return { error: L("终点和起点是同一个符号，换一个", "End is the same symbol as the start; pick another") };
    this.start = null;
    return { from, to };
  }
}

/** 搜索框的占位文字：等终点时写明起点。 */
export function pickPlaceholder(label: string): string {
  return L(`选终点：从 ${label} 出发的调用路径（Esc 取消）`, `Pick the end: call paths from ${label} (Esc to cancel)`);
}

/** 标题区统计："3 条路径 · 最多 8 步 · 2 条含分派 / 低置信度边 · 还有更多，已截断"。 */
export function pathStats(dto: CallPathsDto): string {
  const n = dto.paths.length;
  const inferred = dto.paths.filter((p) => p.inferred).length;
  const parts = [
    L(`${n} 条路径`, `${n} ${n === 1 ? "path" : "paths"}`),
    L(`最多 ${dto.maxDepth} 步`, `at most ${dto.maxDepth} steps`),
  ];
  if (inferred > 0) parts.push(L(`${inferred} 条含分派 / 低置信度边`, `${inferred} with dispatch / low-confidence edges`));
  if (dto.truncated) parts.push(L(`超过 ${dto.maxPaths} 条，已截断`, `more than ${dto.maxPaths}, truncated`));
  return parts.join(" · ");
}

/** 图例：实线 / 橙色虚线（分派）/ 灰色虚线（低置信度）。 */
export function pathLegend(): string {
  return L("实线 = 直接调用（confidence ≥ 0.7）；橙色虚线 = 分派到 Handler；半透明虚线 = 低置信度（< 0.7，多候选或外部），不是确定调用",
    "Solid = direct call (confidence ≥ 0.7); orange dashed = dispatch to a handler; faded dashed = low confidence (< 0.7, candidate or external), not a certain call");
}
