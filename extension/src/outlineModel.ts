// 调用大纲（侧边栏树视图）的纯逻辑：步骤 → 树节点 → TreeItem 字段。不 import vscode，方便单测；
// outlineView.ts 把这里算出的 OutlineItemSpec 原样搬进 vscode.TreeItem。
import { L } from "../webview/l10n";
import type { OutlineCtx, OutlineDto, OutlineStepDto, OutlineStepKind, SymbolDto } from "../webview/types";

/** 能当大纲根的符号种类：方法 / 函数（含构造函数、箭头函数——索引里都是这两种）。 */
const OutlineRootKinds = new Set(["method", "function"]);

export function isOutlineSymbol(s: SymbolDto): boolean {
  return OutlineRootKinds.has(s.kind);
}

export const CtxLabels: Record<OutlineCtx, string> = {
  loop: L("↻ 循环内", "↻ in loop"),
  cond: L("? 条件内", "? conditional"),
  try: L("try 内", "in try"),
  catch: L("catch 内", "in catch"),
  callback: L("λ 回调", "λ callback"),
};

/** 服务以后加了新的 ctx 也不至于显示 undefined。 */
export function ctxLabel(ctx: OutlineCtx | string | null | undefined): string | null {
  if (!ctx) return null;
  return (CtxLabels as Record<string, string>)[ctx] ?? ctx;
}

/** 低于这个置信度的步骤在提示里注明"推测"。 */
export const GuessConfidence = 0.7;

const KindIcons: Record<OutlineStepKind, string> = {
  call: "symbol-method",
  new: "symbol-constructor",
  dispatch: "send",
  impl: "symbol-interface",
  external: "library",
  ambiguous: "question",
};

const KindNames: Record<OutlineStepKind, string> = {
  call: L("调用", "call"),
  new: L("构造", "constructor"),
  dispatch: L("分派到 Handler", "dispatch to Handler"),
  impl: L("接口实现", "interface implementation"),
  external: L("框架 / 第三方调用", "framework / third-party call"),
  ambiguous: L("多个候选", "multiple candidates"),
};

/** 树上的一个节点。 */
export type OutlineNode = RootNode | StepNode | InfoNode;

export interface RootNode {
  type: "root";
  symbol: SymbolDto;
  repoRoot: string;
}

export interface StepNode {
  type: "step";
  step: OutlineStepDto;
  /** 调用发生在哪个文件（相对仓库根）：根方法的文件，或展开出这一层的那一步的 target 文件。单击跳到这里。 */
  containerPath: string;
  /** 展开路径上已经出现过的方法 id（含根）。target 在里面 = 递归。 */
  ancestors: ReadonlySet<number>;
}

export interface InfoNode {
  type: "info";
  text: string;
  tooltip?: string;
  icon?: string;
}

export interface OutlineItemSpec {
  label: string;
  description: string;
  tooltip: string;
  /** codicon id。 */
  icon: string;
  /** ThemeColor id；undefined 用默认颜色。 */
  iconColor?: string;
  /** 文字置灰（框架调用）。 */
  dim: boolean;
  collapsible: "none" | "collapsed" | "expanded";
  /** 菜单 when 用：含 "target" 才显示"转到定义"。 */
  contextValue: string;
  /** 单击时有动作（跳到调用处 / 定义处）。 */
  clickable: boolean;
}

// ---------- 名字 ----------

/** 按 "." 切，但不切 <> () [] 里的点（泛型参数、参数表里的限定名）。 */
export function splitQualified(name: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let cur = "";
  for (const ch of name) {
    if (ch === "<" || ch === "(" || ch === "[") depth++;
    else if ((ch === ">" || ch === ")" || ch === "]") && depth > 0) depth--;
    if (ch === "." && depth === 0) { parts.push(cur); cur = ""; continue; }
    cur += ch;
  }
  parts.push(cur);
  return parts.filter((p) => p.length > 0);
}

/** 根节点显示名："类型.方法"（限定名最后两段，去掉参数表）。 */
export function shortName(s: SymbolDto): string {
  const q = (s.qualifiedName || s.name).replace(/\(.*\)\s*$/, "");
  const parts = splitQualified(q);
  if (parts.length === 0) return s.name;
  return parts.slice(-2).join(".");
}

export function baseName(p: string): string {
  const i = Math.max(p.lastIndexOf("/"), p.lastIndexOf("\\"));
  return i >= 0 ? p.slice(i + 1) : p;
}

/** target 能"转到定义"：仓库里的符号（外部符号没有文件）。 */
export function hasDefinition(t: SymbolDto | null | undefined): t is SymbolDto {
  return !!t && t.kind !== "external" && !!t.path && t.path !== "?";
}

// ---------- 递归 / 可展开 ----------

export function isRecursive(node: StepNode): boolean {
  const t = node.step.target;
  return !!t && node.ancestors.has(t.id);
}

/**
 * 子节点从哪来：
 * - inPlace：步骤自带 children（dispatch 目标 / impl 实现 / ambiguous 候选），直接展开，不发请求；
 * - fetch：请求 target 的大纲；
 * - none：叶子（外部调用、没调用的方法、递归）。
 */
export function childSource(node: StepNode): "inPlace" | "fetch" | "none" {
  if (node.step.children.length > 0) return "inPlace";
  if (isRecursive(node)) return "none";
  if (node.step.expandable && node.step.target && node.step.kind !== "external") return "fetch";
  return "none";
}

// ---------- 子节点 ----------

/** 根方法（或某一步 target）的大纲 → 这一层的子节点。ancestors 是这一层所在路径（含本方法）。 */
export function outlineChildren(outline: OutlineDto, ancestors: ReadonlySet<number>): OutlineNode[] {
  const containerPath = outline.symbol.path;
  const nodes: OutlineNode[] = outline.steps.map((step) => ({ type: "step", step, containerPath, ancestors }));
  if (outline.steps.length === 0) nodes.push({ type: "info", text: L("（没有调用）", "(no calls)"), tooltip: L("这个方法里没有找到调用。", "No calls were found in this method."), icon: "dash" });
  if (outline.truncated) nodes.push({ type: "info", text: L("…步骤过多，后面的省略了", "…too many steps, the rest are omitted"), tooltip: L("服务按上限截断了这一层；在 CodeTrail Graph 面板里看完整调用。", "The service truncated this level at its limit; see the full calls in the CodeTrail Graph panel."), icon: "ellipsis" });
  return nodes;
}

export function rootAncestors(root: RootNode): ReadonlySet<number> {
  return new Set([root.symbol.id]);
}

/** 展开一步（fetch）后它这一层的 ancestors：父路径 + 这一步的 target。 */
export function expandedAncestors(node: StepNode): ReadonlySet<number> {
  const s = new Set(node.ancestors);
  if (node.step.target) s.add(node.step.target.id);
  return s;
}

/** 就地子项（dispatch / impl / ambiguous 候选）：与父步骤同一调用处、同一路径。 */
export function inPlaceChildren(node: StepNode): StepNode[] {
  return node.step.children.map((step) => ({ type: "step", step, containerPath: node.containerPath, ancestors: node.ancestors }));
}

// ---------- TreeItem 字段 ----------

export function stepLabel(step: OutlineStepDto): string {
  return `${step.order}  ${step.label}${step.repeat > 1 ? ` ×${step.repeat}` : ""}`;
}

export function stepDescription(step: OutlineStepDto, recursive: boolean): string {
  const parts: string[] = [];
  if (recursive) parts.push(L("↺ 递归", "↺ recursive"));
  if (step.kind === "external") parts.push(L("框架", "framework"));
  else if (step.kind === "new") parts.push("new");
  const c = ctxLabel(step.ctx);
  if (c) parts.push(c);
  parts.push(`L${step.line}`);
  return parts.join(" · ");
}

export function confidenceText(confidence: number): string {
  const v = Number.isFinite(confidence) ? confidence : 0;
  return L(`置信度 ${v.toFixed(2)}${v < GuessConfidence ? "（推测）" : ""}`, `confidence ${v.toFixed(2)}${v < GuessConfidence ? " (guess)" : ""}`);
}

export function stepTooltip(node: StepNode, recursive: boolean): string {
  const { step } = node;
  const t = step.target;
  const lines: string[] = [];
  lines.push(t ? t.qualifiedName || step.label : step.label);
  if (t?.signature) lines.push(t.signature);
  if (hasDefinition(t)) lines.push(L(`定义：${t.path}:${t.startLine}`, `Definition: ${t.path}:${t.startLine}`));
  const c = ctxLabel(step.ctx);
  lines.push(L(`调用处：${node.containerPath}:${step.line}${c ? ` · ${c}` : ""}`, `Call site: ${node.containerPath}:${step.line}${c ? ` · ${c}` : ""}`));
  lines.push(`${KindNames[step.kind] ?? step.kind} · ${confidenceText(step.confidence)}`);
  if (step.repeat > 1) lines.push(L(`相邻的同一调用合并了 ${step.repeat} 次`, `${step.repeat} adjacent identical calls merged`));
  if (step.kind === "ambiguous") lines.push(L("有多个候选，展开查看", "Multiple candidates; expand to view"));
  if (recursive) lines.push(L("↺ 递归：这个方法已经在展开路径上，不再展开", "↺ Recursive: this method is already on the expanded path; not expanding again"));
  return lines.join("\n");
}

export function stepItem(node: StepNode): OutlineItemSpec {
  const { step } = node;
  const recursive = isRecursive(node);
  const source = childSource(node);
  const external = step.kind === "external";
  return {
    label: stepLabel(step),
    description: stepDescription(step, recursive),
    tooltip: stepTooltip(node, recursive),
    icon: KindIcons[step.kind] ?? "symbol-method",
    iconColor: external ? "disabledForeground" : undefined,
    dim: external,
    // 自带候选的直接展开（就地子项不发请求）；要请求的先收起，用户展开才去取
    collapsible: source === "inPlace" ? "expanded" : source === "fetch" ? "collapsed" : "none",
    contextValue: hasDefinition(step.target) && !external ? "outline.step.target" : "outline.step",
    clickable: true,
  };
}

export function rootItem(node: RootNode): OutlineItemSpec {
  const s = node.symbol;
  return {
    label: shortName(s),
    description: `${baseName(s.path)}:${s.startLine}`,
    tooltip: [s.qualifiedName, s.signature, `${s.path}:${s.startLine}`].filter(Boolean).join("\n"),
    icon: s.kind === "function" ? "symbol-function" : "symbol-method",
    dim: false,
    collapsible: "expanded",
    contextValue: "outline.root.target",
    clickable: true,
  };
}

export function infoItem(node: InfoNode): OutlineItemSpec {
  return {
    label: node.text,
    description: "",
    tooltip: node.tooltip ?? node.text,
    icon: node.icon ?? "info",
    dim: true,
    collapsible: "none",
    contextValue: "outline.info",
    clickable: false,
  };
}

export function itemSpec(node: OutlineNode): OutlineItemSpec {
  switch (node.type) {
    case "root": return rootItem(node);
    case "step": return stepItem(node);
    case "info": return infoItem(node);
  }
}

// ---------- 跳转 ----------

export interface JumpTarget {
  /** 相对仓库根。 */
  path: string;
  /** 1 起。 */
  line: number;
  /** 1 起；null = 不指定列（行首第一个非空白）。 */
  col: number | null;
  /** 文件内 UTF-16 偏移；有就优先用（document.positionAt）。 */
  pos: number | null;
}

/** 单击：根 → 方法定义；步骤 → 调用处（当前所在方法的文件）。 */
export function clickTarget(node: OutlineNode): JumpTarget | null {
  if (node.type === "root") return { path: node.symbol.path, line: node.symbol.startLine, col: node.symbol.startCol, pos: null };
  if (node.type === "step") return { path: node.containerPath, line: node.step.line, col: null, pos: node.step.pos ?? null };
  return null;
}

/** 转到定义：target 的定义处；根就是它自己。 */
export function definitionTarget(node: OutlineNode): JumpTarget | null {
  if (node.type === "root") return clickTarget(node);
  if (node.type === "step" && hasDefinition(node.step.target)) {
    const t = node.step.target;
    return { path: t.path, line: t.startLine, col: t.startCol, pos: null };
  }
  return null;
}

/**
 * pos 换算出的行和索引时记录的行对不上（文件改过还没重新索引），说明偏移已经不准了：退回按行跳。
 * posLine / line 都是 1 起。
 */
export function trustPos(posLine: number, line: number): boolean {
  return posLine === line;
}
