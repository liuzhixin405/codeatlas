import * as path from "node:path";

/**
 * 扫描器跳过的目录名（大小写不敏感），与 C# 的 IndexOptions.ExcludedDirectories 同一份；
 * test/fileWatch.test.ts 直接读那个 C# 文件对照，改一边忘了另一边会红。
 */
export const ExcludedDirectories: readonly string[] = [
  "node_modules", "bin", "obj", ".git", "dist", "build", ".codeatlas", ".vs", ".idea",
  "venv", ".venv", "__pycache__", "site-packages",
  "target", ".gradle", ".mvn",
];

export type WatchEventKind = "create" | "change" | "delete";

export interface PathApi {
  relative(from: string, to: string): string;
  isAbsolute(p: string): boolean;
  extname(p: string): string;
  sep: string;
}

/**
 * 这个磁盘事件可能影响索引吗：落在某个根（主根 / 外部根）之下、相对那个根的各级目录都不是跳过目录、扩展名受支持。
 * 删除事件额外放过没有扩展名的路径：删目录（git checkout 去掉整个目录）时只来一个目录事件。
 * 不判断嵌套 worktree / 符号链接等扫描器的细节规则：多放过的只会让增量索引白扫一遍（没变的文件不读）。
 */
export function isIndexablePath(
  file: string, kind: WatchEventKind, roots: readonly string[], supported: ReadonlySet<string>,
  excluded: readonly string[] = ExcludedDirectories, p: PathApi = path,
): boolean {
  const ext = p.extname(file).toLowerCase();
  if (!supported.has(ext) && !(kind === "delete" && ext === "")) return false;
  const skip = new Set(excluded.map((d) => d.toLowerCase()));
  for (const root of roots) {
    const rel = p.relative(root, file);
    if (rel === "" || rel.startsWith("..") || p.isAbsolute(rel)) continue;
    const parts = rel.split(/[\\/]/);
    // 文件名本身不算目录；删除的是目录时它自己也要查（删 node_modules 不该触发）
    const dirs = kind === "delete" && ext === "" ? parts : parts.slice(0, -1);
    return !dirs.some((d) => skip.has(d.toLowerCase()));
  }
  return false;
}

/** 编辑器保存后这么久内同一文件的磁盘事件算"保存引起的"：归 codeatlas.indexOnSave 管，监听不重复触发。 */
export const SaveEchoWindowMs = 5000;

interface PendingBatch {
  /** 路径 → 最后一次事件的时间。 */
  paths: Map<string, number>;
  timer: ReturnType<typeof setTimeout> | null;
  /** 这一批第一个事件的时间：持续不断的事件流最多攒 maxWaitMs 也要交一次。 */
  firstAt: number;
}

/**
 * 把编辑器之外的磁盘改动（AI 助手直接写文件、git pull / checkout、外部工具生成代码）防抖成每个根一次回调。
 * 每来一个事件就把这个根的计时器往后推 delayMs；切分支那种几千个事件也只在安静下来后交一次（最长攒 maxWaitMs）。
 * 编辑器保存的文件（markSaved）在交出前剔除：保存由 indexOnSave 负责，不重复排索引；剔完为空就不回调。
 * 不依赖 vscode，便于用假计时器测试。
 */
export class ChangeBatcher {
  private readonly pending = new Map<string, PendingBatch>();
  private readonly saved = new Map<string, number>();

  constructor(
    private readonly flush: (root: string, paths: string[]) => void,
    private readonly delayMs: number,
    private readonly maxWaitMs: number = Math.max(delayMs * 10, 30000),
    private readonly now: () => number = Date.now,
    private readonly keyOf: (file: string) => string = defaultKey,
  ) {}

  add(root: string, file: string): void {
    const t = this.now();
    let b = this.pending.get(root);
    if (!b) { b = { paths: new Map(), timer: null, firstAt: t }; this.pending.set(root, b); }
    b.paths.set(file, t);
    if (b.timer) clearTimeout(b.timer);
    const wait = Math.max(0, Math.min(this.delayMs, b.firstAt + this.maxWaitMs - t));
    b.timer = setTimeout(() => this.fire(root), wait);
  }

  /** 编辑器保存了这个文件（onDidSaveTextDocument，不管 indexOnSave 开没开）。 */
  markSaved(file: string): void {
    this.saved.set(this.keyOf(file), this.now());
  }

  /** 还在攒的事件数（测试 / 日志用）。 */
  pendingCount(root: string): number {
    return this.pending.get(root)?.paths.size ?? 0;
  }

  dispose(): void {
    for (const b of this.pending.values()) if (b.timer) clearTimeout(b.timer);
    this.pending.clear();
    this.saved.clear();
  }

  private fire(root: string): void {
    const b = this.pending.get(root);
    if (!b) return;
    this.pending.delete(root);
    const now = this.now();
    for (const [k, at] of this.saved) if (now - at > SaveEchoWindowMs * 4) this.saved.delete(k); // 旧记录随手清掉
    const paths = [...b.paths].filter(([file, at]) => {
      const savedAt = this.saved.get(this.keyOf(file));
      return savedAt === undefined || Math.abs(at - savedAt) > SaveEchoWindowMs;
    }).map(([file]) => file).sort();
    if (paths.length > 0) this.flush(root, paths);
  }
}

/** Windows 上路径大小写不敏感（VS Code 的盘符还常是小写）。 */
function defaultKey(file: string): string {
  const n = path.normalize(file);
  return process.platform === "win32" ? n.toLowerCase() : n;
}
