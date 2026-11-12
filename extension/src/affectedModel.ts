// 影响范围视图里的「改动的影响」（GET /api/affected）：AffectedDto → 树节点 → TreeItem 字段。不 import vscode，方便单测；
// impactView.ts 把这里算出的 OutlineItemSpec 原样搬进 vscode.TreeItem。入口文案 / 图标与影响范围共用 impactModel.ts，置信度文案与调用大纲共用。
import { L } from "../webview/l10n";
import type { AffectedDto, AffectedEntryDto, AffectedFileDto, AffectedSymbolDto, AffectedTestDto } from "../webview/types";
import { entryIcon, entryKindName, entryLabel } from "./impactModel";
import { baseName, confidenceText, GuessConfidence, type JumpTarget, type OutlineItemSpec } from "./outlineModel";

export type AffectedNode =
  | AffectedRootNode
  | AffectedGroupNode
  | AffectedSymbolNode
  | AffectedEntryNode
  | AffectedTestFileNode
  | AffectedTestNode
  | AffectedFileNode
  | AffectedInfoNode;

export interface AffectedRootNode {
  type: "aRoot";
  data: AffectedDto;
  repoRoot: string;
}

export interface AffectedGroupNode {
  type: "aGroup";
  group: "symbols" | "entries" | "tests" | "files";
  data: AffectedDto;
}

export interface AffectedSymbolNode {
  type: "aSymbol";
  item: AffectedSymbolDto;
}

export interface AffectedEntryNode {
  type: "aEntry";
  item: AffectedEntryDto;
  /** 能走到它的改动符号的显示名。 */
  from: string[];
}

export interface AffectedTestFileNode {
  type: "aTestFile";
  path: string;
  tests: AffectedTestDto[];
  labels: ReadonlyMap<number, string>;
}

export interface AffectedTestNode {
  type: "aTest";
  item: AffectedTestDto;
  from: string[];
}

export interface AffectedFileNode {
  type: "aFile";
  file: AffectedFileDto;
}

export interface AffectedInfoNode {
  type: "aInfo";
  text: string;
  tooltip?: string;
  icon?: string;
}

const AffectedTypes = new Set(["aRoot", "aGroup", "aSymbol", "aEntry", "aTestFile", "aTest", "aFile", "aInfo"]);

export function isAffectedNode(node: { type: string }): node is AffectedNode {
  return AffectedTypes.has(node.type);
}

/** 低置信度（推测）：链路上有一条边低于 0.7（多实现、接口 / 基类补充、全仓同名）。 */
export function isGuess(confidence: number): boolean {
  return confidence < GuessConfidence;
}

function labelsOf(data: AffectedDto): Map<number, string> {
  return new Map(data.symbols.map((s) => [s.symbol.id, s.label]));
}

function fromLabels(ids: number[], labels: ReadonlyMap<number, string>): string[] {
  return ids.map((id) => labels.get(id) ?? `#${id}`);
}

// ---------- 子节点 ----------

/** 根下面：提示（索引过期等）、改动的符号、受影响的入口、受影响的测试、改动的文件。 */
export function affectedRootChildren(root: AffectedRootNode): AffectedNode[] {
  const d = root.data;
  const nodes: AffectedNode[] = d.warnings.map((w) => ({ type: "aInfo", text: w, tooltip: w, icon: "warning" }));
  if (d.files.length === 0) {
    nodes.push({ type: "aInfo", text: L("没有改动", "No changes"), tooltip: L("相对这个 base 工作区没有改动（未跟踪的新文件也算在内）。", "The work tree has no changes relative to this base (untracked new files included)."), icon: "check" });
    return nodes;
  }
  nodes.push({ type: "aGroup", group: "symbols", data: d }, { type: "aGroup", group: "entries", data: d }, { type: "aGroup", group: "tests", data: d }, { type: "aGroup", group: "files", data: d });
  return nodes;
}

export function affectedGroupChildren(node: AffectedGroupNode): AffectedNode[] {
  const d = node.data;
  const labels = labelsOf(d);
  switch (node.group) {
    case "symbols":
      return d.symbols.length
        ? d.symbols.map((item) => ({ type: "aSymbol", item }))
        : [{ type: "aInfo", text: L("（没有映射到符号的改动）", "(no changes mapped to symbols)"), tooltip: L("改动的文件都被跳过了：见「改动的文件」。", "All changed files were skipped: see \"Changed files\"."), icon: "dash" }];
    case "entries":
      return d.entries.map((item) => ({ type: "aEntry", item, from: fromLabels(item.from, labels) }));
    case "tests":
      return testFileNodes(d.testFiles, d.tests, labels);
    case "files":
      return d.files.map((file) => ({ type: "aFile", file }));
  }
}

/** 测试按文件分组；本身就改了、但没有走到具体测试方法的测试文件也列出来（没有子项）。 */
export function testFileNodes(testFiles: string[], tests: AffectedTestDto[], labels: ReadonlyMap<number, string>): AffectedTestFileNode[] {
  return testFiles.map((path) => ({ type: "aTestFile", path, tests: tests.filter((t) => t.symbol.path === path), labels }));
}

/** 一个改动符号下面：它走到的入口，再是测试。 */
export function affectedSymbolChildren(node: AffectedSymbolNode): AffectedNode[] {
  const own = new Map([[node.item.symbol.id, node.item.label]]);
  const nodes: AffectedNode[] = [
    ...node.item.entries.map((item): AffectedNode => ({ type: "aEntry", item, from: fromLabels(item.from, own) })),
    ...node.item.tests.map((item): AffectedNode => ({ type: "aTest", item, from: fromLabels(item.from, own) })),
  ];
  if (nodes.length === 0) {
    nodes.push({ type: "aInfo", text: L("没有走到入口或测试", "Reaches no entries or tests"), tooltip: L("往上没有碰到入口，也没有测试调用它。可能经反射 / 框架回调调用，或者还没有测试。", "Walking up reached no entry and no test calls it. It may be called via reflection / framework callbacks, or it has no tests yet."), icon: "dash" });
  }
  return nodes;
}

export function affectedChildren(node: AffectedNode): AffectedNode[] {
  switch (node.type) {
    case "aRoot": return affectedRootChildren(node);
    case "aGroup": return affectedGroupChildren(node);
    case "aSymbol": return affectedSymbolChildren(node);
    case "aTestFile": return node.tests.map((item) => ({ type: "aTest", item, from: fromLabels(item.from, node.labels) }));
    default: return [];
  }
}

// ---------- TreeItem 字段 ----------

/** "L3, L7-9" 这样的行号列表（连续的合并）。 */
export function lineList(lines: number[]): string {
  const sorted = [...new Set(lines)].sort((a, b) => a - b);
  const parts: string[] = [];
  for (let i = 0; i < sorted.length;) {
    let j = i;
    while (j + 1 < sorted.length && sorted[j + 1] === sorted[j]! + 1) j++;
    parts.push(i === j ? `L${sorted[i]}` : `L${sorted[i]}-${sorted[j]}`);
    i = j + 1;
  }
  return parts.join(", ");
}

function sourceText(d: AffectedDto): string {
  if (d.input === "git") return L(`相对 ${d.base ?? "HEAD"}${d.mergeBase ? `（${d.mergeBase.slice(0, 8)}）` : ""} → 工作区`, `vs ${d.base ?? "HEAD"}${d.mergeBase ? ` (${d.mergeBase.slice(0, 8)})` : ""} → work tree`);
  return d.input === "diff" ? L("来自 diff", "from diff") : L("来自文件名列表", "from file list");
}

function guessTag(confidence: number): string {
  return isGuess(confidence) ? L(`推测 ${confidence.toFixed(2)}`, `guess ${confidence.toFixed(2)}`) : "";
}

export function affectedRootItem(node: AffectedRootNode): OutlineItemSpec {
  const d = node.data;
  return {
    label: L("改动的影响", "Impact of changes"),
    description: `${sourceText(d)} · ${L(`${d.symbols.length} 个符号`, `${d.symbols.length} ${d.symbols.length === 1 ? "symbol" : "symbols"}`)}`,
    tooltip: [
      L(`比较：${sourceText(d)}`, `Compare: ${sourceText(d)}`),
      L(`改动的文件 ${d.files.length} · 改动的符号 ${d.symbols.length} · 受影响的入口 ${d.entries.length} · 测试文件 ${d.testFiles.length}`, `Changed files ${d.files.length} · changed symbols ${d.symbols.length} · affected entries ${d.entries.length} · test files ${d.testFiles.length}`),
      L(`往上最多 ${d.depth} 层；刷新按钮重新分析，图钉取消后回到跟随光标。`, `Up to ${d.depth} levels upward; Refresh re-runs the analysis, unpin to follow the cursor again.`),
    ].join("\n"),
    icon: "git-compare",
    dim: false,
    collapsible: "expanded",
    contextValue: "impact.affected",
    clickable: false,
  };
}

export function affectedGroupItem(node: AffectedGroupNode): OutlineItemSpec {
  const d = node.data;
  const base = { dim: false, contextValue: "impact.group", clickable: false } as const;
  switch (node.group) {
    case "symbols":
      return {
        ...base, label: L(`改动的符号（${d.symbols.length}）`, `Changed symbols (${d.symbols.length})`), description: "", icon: "diff",
        tooltip: L("diff 的改动行落在哪些方法 / 类型里（新侧行号，对应索引里的起止行）。改动在符号之外（import / using、文件顶部）时整个文件的符号都算，标「文件级」。", "Which methods / types the changed lines fall into (new-side line numbers, matched to the indexed ranges). Changes outside any symbol (import / using, top of file) count every symbol in the file, marked \"file level\"."),
        collapsible: "expanded",
      };
    case "entries": {
      const guess = d.entries.filter((e) => isGuess(e.confidence)).length;
      return {
        ...base, label: L(`受影响的入口（${d.entries.length}）`, `Affected entries (${d.entries.length})`),
        description: [d.truncated ? L("可能不全", "may be incomplete") : "", guess ? L(`${guess} 个推测`, `${guess} guessed`) : ""].filter(Boolean).join(" · "),
        icon: "debug-start",
        tooltip: L("改动的符号沿调用方一路往上走到的入口（与「受影响的入口」同一套规则）。「推测」= 链路上有低于 0.7 的边。", "Entries reached by walking up the callers of the changed symbols (same rules as \"Affected entries\"). \"guess\" = the chain has an edge below 0.7."),
        collapsible: d.entries.length ? "collapsed" : "none",
      };
    }
    case "tests":
      return {
        ...base, label: L(`受影响的测试（${d.testFiles.length} 个文件）`, `Affected tests (${d.testFiles.length} ${d.testFiles.length === 1 ? "file" : "files"})`),
        description: L(`${d.tests.length} 个方法`, `${d.tests.length} ${d.tests.length === 1 ? "method" : "methods"}`), icon: "beaker",
        tooltip: L("往上走到的测试方法所在的文件，加上本身就改了的测试文件。命令行 codeatlas affected --quiet 输出的就是这些文件。", "Files of the test methods reached upward, plus test files that changed themselves. These are what codeatlas affected --quiet prints."),
        collapsible: d.testFiles.length ? "expanded" : "none",
      };
    case "files": {
      const skipped = d.files.filter((f) => f.analysis === "skipped").length;
      return {
        ...base, label: L(`改动的文件（${d.files.length}）`, `Changed files (${d.files.length})`),
        description: skipped ? L(`${skipped} 个跳过`, `${skipped} skipped`) : "", icon: "files",
        tooltip: L("每个改动文件怎么处理的：按符号 / 文件级 / 跳过（原因见悬停）。", "How each changed file was handled: by symbol / file level / skipped (hover for the reason)."),
        collapsible: "collapsed",
      };
    }
  }
}

export function affectedSymbolItem(node: AffectedSymbolNode): OutlineItemSpec {
  const s = node.item;
  const where = s.reason === "lines" ? lineList(s.lines) : L("文件级", "file level");
  const counts = L(`入口 ${s.entries.length} · 测试 ${s.tests.length}`, `${s.entries.length} ${s.entries.length === 1 ? "entry" : "entries"} · ${s.tests.length} ${s.tests.length === 1 ? "test" : "tests"}`);
  return {
    label: s.label,
    description: [`${baseName(s.symbol.path)}:${s.symbol.startLine}`, where, counts, s.truncated ? L("可能不全", "may be incomplete") : ""].filter(Boolean).join(" · "),
    tooltip: [
      s.symbol.qualifiedName,
      `${s.symbol.path}:${s.symbol.startLine}`,
      s.reason === "lines" ? L(`改动行：${where}`, `Changed lines: ${where}`) : L("文件级：同一文件里有落在符号之外的改动（或新文件 / 文件名列表），文件里的符号都算", "File level: the same file has changes outside any symbol (or it is a new file / file list); every symbol in the file counts"),
      L(`往上 ${s.callers} 个方法`, `${s.callers} ${s.callers === 1 ? "method" : "methods"} upward`),
      s.isTest ? L("本身是测试代码", "Is test code itself") : "",
    ].filter(Boolean).join("\n"),
    icon: s.symbol.kind === "class" || s.symbol.kind === "struct" ? "symbol-class" : s.symbol.kind === "function" ? "symbol-function" : "symbol-method",
    iconColor: s.reason === "file" ? "disabledForeground" : undefined,
    dim: s.reason === "file",
    collapsible: "collapsed",
    contextValue: "impact.affected.symbol.target",
    clickable: true,
  };
}

export function affectedEntryItem(node: AffectedEntryNode): OutlineItemSpec {
  const e = node.item;
  const guess = isGuess(e.confidence);
  return {
    label: entryLabel(e.entry),
    description: [e.containerName, L(`深度 ${e.depth}`, `depth ${e.depth}`), guessTag(e.confidence)].filter(Boolean).join(" · "),
    tooltip: [
      e.entry.qualifiedName,
      `${entryKindName(e.entry.kind)} · ${e.containerName}`,
      L(`定义：${e.entry.file}:${e.entry.line}`, `Definition: ${e.entry.file}:${e.entry.line}`),
      L(`链路${confidenceText(e.confidence)}`, `Chain ${confidenceText(e.confidence)}`),
      guess ? L("链路上有低置信度的边（多实现 / 经接口补充 / 全仓同名），可能并不会真的走到", "The chain has a low-confidence edge (multiple implementations / via interface / repository-wide name match); it may not actually be reached") : "",
      L(`来自：${node.from.join("、")}`, `From: ${node.from.join(", ")}`),
    ].filter(Boolean).join("\n"),
    icon: guess ? "question" : entryIcon(e.entry.kind),
    iconColor: guess ? "editorWarning.foreground" : undefined,
    dim: false,
    collapsible: "none",
    contextValue: "impact.affected.entry",
    clickable: true,
  };
}

export function affectedTestFileItem(node: AffectedTestFileNode): OutlineItemSpec {
  const dir = node.path.includes("/") ? node.path.slice(0, node.path.lastIndexOf("/")) : "";
  return {
    label: baseName(node.path),
    description: [dir, node.tests.length ? L(`${node.tests.length} 个`, `${node.tests.length}`) : L("文件本身改了", "changed itself")].filter(Boolean).join(" · "),
    tooltip: node.path,
    icon: "beaker",
    dim: false,
    collapsible: node.tests.length ? "collapsed" : "none",
    contextValue: "impact.affected.testFile",
    clickable: true,
  };
}

export function affectedTestItem(node: AffectedTestNode): OutlineItemSpec {
  const t = node.item;
  const guess = isGuess(t.confidence);
  return {
    label: t.label,
    description: [`L${t.symbol.startLine}`, t.depth === 0 ? L("本身改了", "changed") : L(`深度 ${t.depth}`, `depth ${t.depth}`), guessTag(t.confidence)].filter(Boolean).join(" · "),
    tooltip: [
      t.symbol.qualifiedName,
      `${t.symbol.path}:${t.symbol.startLine}`,
      L(`链路${confidenceText(t.confidence)}`, `Chain ${confidenceText(t.confidence)}`),
      L(`来自：${node.from.join("、")}`, `From: ${node.from.join(", ")}`),
    ].join("\n"),
    icon: guess ? "question" : "beaker",
    iconColor: guess ? "editorWarning.foreground" : undefined,
    dim: false,
    collapsible: "none",
    contextValue: "impact.affected.test.target",
    clickable: true,
  };
}

const StatusLetters: Record<AffectedFileDto["status"], string> = { added: "A", deleted: "D", renamed: "R", modified: "M" };

export function affectedFileItem(node: AffectedFileNode): OutlineItemSpec {
  const f = node.file;
  const how = f.analysis === "symbols"
    ? L(`${f.symbols} 个符号`, `${f.symbols} ${f.symbols === 1 ? "symbol" : "symbols"}`)
    : f.analysis === "file" ? L(`文件级 · ${f.symbols} 个符号`, `file level · ${f.symbols} ${f.symbols === 1 ? "symbol" : "symbols"}`) : L("跳过", "skipped");
  return {
    label: f.path,
    description: [StatusLetters[f.status], how, f.stale ? L("索引过期", "stale index") : "", f.isTest ? L("测试", "test") : ""].filter(Boolean).join(" · "),
    tooltip: [f.oldPath ? `${f.oldPath} → ${f.path}` : f.path, f.note ?? "", f.outside.length ? L(`符号之外的改动：${lineList(f.outside)}`, `Changes outside symbols: ${lineList(f.outside)}`) : ""].filter(Boolean).join("\n"),
    icon: f.analysis === "skipped" ? "circle-slash" : f.stale ? "warning" : "file",
    dim: f.analysis === "skipped",
    collapsible: "none",
    contextValue: "impact.affected.file",
    clickable: f.status !== "deleted",
  };
}

export function affectedInfoItem(node: AffectedInfoNode): OutlineItemSpec {
  return { label: node.text, description: "", tooltip: node.tooltip ?? node.text, icon: node.icon ?? "info", dim: true, collapsible: "none", contextValue: "impact.info", clickable: false };
}

export function affectedItemSpec(node: AffectedNode): OutlineItemSpec {
  switch (node.type) {
    case "aRoot": return affectedRootItem(node);
    case "aGroup": return affectedGroupItem(node);
    case "aSymbol": return affectedSymbolItem(node);
    case "aEntry": return affectedEntryItem(node);
    case "aTestFile": return affectedTestFileItem(node);
    case "aTest": return affectedTestItem(node);
    case "aFile": return affectedFileItem(node);
    case "aInfo": return affectedInfoItem(node);
  }
}

// ---------- 单击 / 转到定义 ----------

/** 单击：符号 / 测试 → 定义处；入口 → 入口方法；测试文件 → 文件开头；改动的文件 → 第一处改动。 */
export function affectedClick(node: AffectedNode): JumpTarget | null {
  switch (node.type) {
    case "aSymbol": return { path: node.item.symbol.path, line: node.item.lines[0] ?? node.item.symbol.startLine, col: null, pos: null };
    case "aEntry": return { path: node.item.entry.file, line: node.item.entry.line, col: null, pos: null };
    case "aTest": return { path: node.item.symbol.path, line: node.item.symbol.startLine, col: node.item.symbol.startCol, pos: null };
    case "aTestFile": return { path: node.path, line: 1, col: null, pos: null };
    case "aFile": {
      if (node.file.status === "deleted") return null;
      const r = node.file.ranges[0];
      return { path: node.file.path, line: r ? Math.max(1, r.count === 0 ? r.start + 1 : r.start) : 1, col: null, pos: null };
    }
    default: return null;
  }
}

/** 转到定义（行尾按钮）：改动的符号 / 测试的定义处。 */
export function affectedDefinition(node: AffectedNode): JumpTarget | null {
  if (node.type === "aSymbol") return { path: node.item.symbol.path, line: node.item.symbol.startLine, col: node.item.symbol.startCol, pos: null };
  if (node.type === "aTest") return { path: node.item.symbol.path, line: node.item.symbol.startLine, col: node.item.symbol.startCol, pos: null };
  return null;
}
