// 编辑器 CodeLens（方法上方"被 N 处调用 · 调用 M 个 · 影响范围"）的纯逻辑：文案、行号漂移时隐藏、按文件缓存。
// 不 import vscode，方便单测；codeLens.ts 把这里算出的 LensSpec 原样搬进 vscode.CodeLens。
import { L } from "../webview/l10n";
import type { FileLensDto, SymbolDto, SymbolLensDto } from "../webview/types";

export type LensKind = "entry" | "callers" | "tests" | "callees" | "impact";

export interface LensSpec {
  kind: LensKind;
  title: string;
  tooltip: string;
  /** 点了有动作；false 时只是一行字（入口、没有调用方、调用 0 个）。 */
  clickable: boolean;
}

/** 一个方法上方的一行 CodeLens。 */
export interface MethodLenses {
  symbol: SymbolDto;
  /** 放在哪一行（1 起，= symbol.startLine）。 */
  line: number;
  lenses: LensSpec[];
}

export const StaleSuffix = L("（索引已过期）", " (index out of date)");
/** 每行第一条前面的标识：和语言插件自己的 CodeLens（"2 个引用"）区分开。ctg = Codetrail Graph 的简写，比全名短。 */
export const BrandPrefix = "ctg: ";
const StaleTooltip = L("文件在索引之后改过，数字和行号可能对不上；保存后会增量索引。", "The file changed after indexing, so counts and line numbers may be off; it will be re-indexed incrementally on save.");

/** "被 N 处调用" 那一条的标题：调用方个数和处数不同时两个都写；都是 0 写"没有调用方"（找死代码用）。 */
export function callersTitle(callers: number, callSites: number): string {
  const sites = Math.max(callers, callSites);
  if (sites === 0) return L("没有调用方", "no callers");
  if (callers > 0 && callers !== sites) return L(`被 ${callers} 个方法调用 ${sites} 处`, `${plural(callers, "caller")} · ${plural(sites, "call site")}`);
  return L(`被 ${sites} 处调用`, plural(sites, "caller"));
}

/** 英文计数：1 caller / 3 callers。 */
function plural(n: number, word: string): string {
  return `${n} ${n === 1 ? word : `${word}s`}`;
}

/**
 * 一个方法的 CodeLens，从左到右：入口 · 被 N 处调用 · 测试 K · 调用 M 个 · 影响范围。
 * 过期（stale）时只在调用计数那一条后面加"（索引已过期）"，一行只加一次。
 */
export function lensSpecs(item: SymbolLensDto, stale: boolean): LensSpec[] {
  const out: LensSpec[] = [];
  if (item.isEntry) out.push({ kind: "entry", title: L("入口", "entry"), tooltip: L("这个方法本身是入口（HTTP / 任务 / 命令 / 消息处理…），由框架调用。", "This method is itself an entry (HTTP / job / command / message handler…), called by the framework."), clickable: false });

  const sites = Math.max(item.callers, item.callSites);
  const callersTip = sites === 0
    ? L(
      `索引里没有找到调用方（不含测试）${item.isEntry ? "；它是入口，由框架调用" : "：可能是死代码，也可能经反射 / 依赖注入 / 动态调用"}。`,
      `No callers found in the index (excluding tests)${item.isEntry ? "; it is an entry, called by the framework" : ": possibly dead code, or called via reflection / dependency injection / dynamic invocation"}.`,
    )
    : L(
      `直接调用方 ${item.callers} 个，共 ${sites} 处（不含测试）。点开列出调用方，选一个跳到调用处。`,
      `${plural(item.callers, "direct caller")}, ${plural(sites, "call site")} in total (excluding tests). Click to list callers and pick one to jump to its call site.`,
    );
  out.push({
    kind: "callers",
    title: callersTitle(item.callers, item.callSites) + (stale ? StaleSuffix : ""),
    tooltip: stale ? `${callersTip}\n${StaleTooltip}` : callersTip,
    clickable: sites > 0,
  });

  if (item.testCallers > 0) {
    out.push({ kind: "tests", title: L(`测试 ${item.testCallers}`, plural(item.testCallers, "test")), tooltip: L(`测试代码里有 ${item.testCallers} 个调用方。点开列出全部调用方（含测试）。`, `${plural(item.testCallers, "caller")} in test code. Click to list all callers (including tests).`), clickable: true });
  }

  const dispatches = item.dispatches ?? 0;
  out.push({
    kind: "callees",
    title: L(`调用 ${item.callees} 个`, `calls ${item.callees}`) + (dispatches > 0 ? L(` · 分派 ${dispatches}`, ` · dispatch ${dispatches}`) : ""),
    tooltip: item.callees > 0
      ? L(
        `按源码顺序调用了 ${item.callees} 个（调用大纲第一层，含框架调用）。点开选一个跳到调用处。`,
        `Calls ${plural(item.callees, "callee")} in source order (first level of the Call Outline, including framework calls). Click to pick one and jump to its call site.`,
      ) +
        (dispatches > 0
          ? L(
            `\n经分派（Send / Publish 一类，不是直接引用）到 ${dispatches} 个 Handler：列表里的「↳ … · 经分派」一项直接跳到 Handler。`,
            `\nDispatches (Send / Publish style, not a direct reference) to ${plural(dispatches, "Handler")}: the "↳ … · via dispatch" items in the list jump straight to the Handler.`,
          )
          : "")
      : L("这个方法里没有找到调用。", "No calls were found in this method."),
    clickable: item.callees > 0,
  });

  out.push({ kind: "impact", title: L("影响范围", "Impact"), tooltip: L("在侧边栏「影响范围」里以这个方法为根：谁调用了它、一路影响到哪些入口。", "Open this method as the root of the Impact sidebar: who calls it, and which entries it ultimately affects."), clickable: true });
  return out;
}

/** 声明行往下最多看几行找名字（C# 特性 / Java 注解 / TS、Python 装饰器让 startLine 落在声明上面几行）。 */
export const DeclarationLookahead = 3;

/** 特性 / 注解 / 装饰器行：`[HttpPost]`、`@Override`、`@Injectable()`。 */
function isDecoration(text: string): boolean {
  const t = text.trimStart();
  return t.startsWith("[") || t.startsWith("@");
}

/**
 * 声明还在原处吗：startLine 那一行包含方法名；或者 startLine 起是特性 / 注解 / 装饰器行，
 * 紧接着（最多往下看 DeclarationLookahead 行、不超过 endLine）有一行包含方法名。
 * 文件改过没保存 / 索引过期时用：行号已漂走的那条就不显示，免得挂在别的地方。lineText 取不到（超出文档）返回 undefined。
 */
export function declarationIntact(symbol: SymbolDto, lineText: (line: number) => string | undefined): boolean {
  if (!symbol.name) return true;
  const last = Math.max(symbol.startLine, Math.min(symbol.endLine || symbol.startLine, symbol.startLine + DeclarationLookahead));
  for (let line = symbol.startLine; line <= last; line++) {
    const text = lineText(line);
    if (text === undefined) return false;
    if (text.includes(symbol.name)) return true;
    if (!isDecoration(text)) return false;
  }
  return false;
}

/**
 * 整个文件的 CodeLens。checkLines：文件在编辑没保存或索引过期时为 true，逐条核对声明行，对不上的不显示；
 * 文件干净且索引新鲜时行号可信，不核对（构造函数名、装饰器等不会被误杀）。
 */
export function fileLenses(dto: FileLensDto, opts: { checkLines: boolean; lineText: (line: number) => string | undefined }): MethodLenses[] {
  const out: MethodLenses[] = [];
  for (const item of dto.symbols) {
    const s = item.symbol;
    if (!(s.startLine >= 1)) continue;
    if (opts.checkLines && !declarationIntact(s, opts.lineText)) continue;
    out.push({ symbol: s, line: s.startLine, lenses: withBrand(lensSpecs(item, dto.stale)) });
  }
  return out;
}

/** 第一条加 ctg: 标识（一行只加一次）。 */
export function withBrand(specs: LensSpec[]): LensSpec[] {
  if (specs.length === 0) return specs;
  return [{ ...specs[0]!, title: BrandPrefix + specs[0]!.title }, ...specs.slice(1)];
}

// ---------- 缓存 ----------

/**
 * 按文件缓存 lens 接口的结果（一次请求整个文件），再按文档版本缓存算好的 CodeLens：
 * - 数据只在保存这个文件 / 索引完成时作废（invalidate），编辑中不重新请求——服务端的数字只在索引后才会变；
 * - 同一文档版本重复调 provideCodeLenses 直接返回上次算的结果；版本变了（在编辑）只重算行号核对，不发请求。
 * 请求失败不缓存，下次再试。
 */
export class LensCache<R> {
  private readonly data = new Map<string, DataEntry>();
  private readonly memo = new Map<string, { entry: DataEntry; version: number; r: R | null }>();

  constructor(private readonly fetch: (filePath: string) => Promise<FileLensDto | null>) {}

  /** 文件的 lens 数据（缓存）。 */
  get(filePath: string): Promise<FileLensDto | null> {
    return this.entry(filePath).p;
  }

  /** 这个文档版本的 CodeLens（缓存）；compute 拿到数据算一次。数据为 null（服务不可用 / 没索引）结果也是 null。 */
  async lenses(filePath: string, version: number, compute: (dto: FileLensDto) => R): Promise<R | null> {
    const entry = this.entry(filePath);
    const m = this.memo.get(filePath);
    if (m && m.entry === entry && m.version === version) return m.r;
    const dto = await entry.p;
    const r = dto ? compute(dto) : null;
    // 期间作废过（数据换了）：结果照样返回，但不记
    if (this.data.get(filePath) === entry) this.memo.set(filePath, { entry, version, r });
    return r;
  }

  /** 作废一个文件（保存）或全部（索引完成、服务重启）。 */
  invalidate(filePath?: string): void {
    if (filePath === undefined) {
      this.data.clear();
      this.memo.clear();
      return;
    }
    this.data.delete(filePath);
    this.memo.delete(filePath);
  }

  private entry(filePath: string): DataEntry {
    const hit = this.data.get(filePath);
    if (hit) return hit;
    const entry: DataEntry = { p: this.fetch(filePath) };
    this.data.set(filePath, entry);
    entry.p.catch(() => {
      if (this.data.get(filePath) !== entry) return;
      this.data.delete(filePath);
      this.memo.delete(filePath);
    });
    return entry;
  }
}

interface DataEntry {
  p: Promise<FileLensDto | null>;
}
