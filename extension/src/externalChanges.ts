import * as vscode from "vscode";
import { ChangeBatcher, isIndexablePath, type WatchEventKind } from "./fileWatch";

/** codeatlas.externalChangeDebounceMs 的默认值与下限。 */
export const DefaultExternalChangeDebounceMs = 2000;
export const MinExternalChangeDebounceMs = 200;

export interface ExternalChangeHost {
  /** 要监听的根：主根 + 外部根（其余 folder 与 codeatlas.projects）。 */
  roots(): string[];
  /** 文件归哪个服务（主根）；不归任何服务为 undefined。 */
  serverRootFor(file: vscode.Uri): string | undefined;
  /** 这个服务本会话里已经起过：只对它们做增量，绝不因为磁盘事件静默拉起服务、往仓库写索引（同 indexOnSave）。 */
  hasServer(root: string): boolean;
  supported: ReadonlySet<string>;
  /** 一批改动 → 交给现有的索引排队（IndexScheduler：串行、进行中再来就置脏，结束后再跑一次）。 */
  requestIndex(root: string, paths: string[]): void;
  log(line: string): void;
}

export interface ExternalChangeWatch extends vscode.Disposable {
  /** 编辑器保存了这个文件：它的磁盘事件归 indexOnSave，监听不重复触发（不管 indexOnSave 开没开）。 */
  markSaved(file: string): void;
}

/**
 * 第一层：监听编辑器之外的磁盘改动（AI 编程助手直接写盘、git pull / checkout、代码生成器），防抖后排一次增量索引。
 * 开关 codeatlas.indexOnExternalChange（默认开）独立于 indexOnSave：关掉 indexOnSave 通常是不想"边写边索引"，
 * 而切分支 / 助手批量改文件后索引整体变旧是另一回事；编辑器保存永远只归 indexOnSave，两个开关互不重复触发。
 * 设置、工作区 folder、codeatlas.projects 变了就重建监听。
 */
export function registerExternalChangeWatch(host: ExternalChangeHost): ExternalChangeWatch {
  let watchers: vscode.Disposable[] = [];
  let batcher: ChangeBatcher | null = null;

  const onEvent = (uri: vscode.Uri, kind: WatchEventKind) => {
    if (!batcher || uri.scheme !== "file") return;
    const root = host.serverRootFor(uri);
    if (!root || !host.hasServer(root)) return;
    if (!isIndexablePath(uri.fsPath, kind, host.roots(), host.supported)) return;
    batcher.add(root, uri.fsPath);
  };

  const rebuild = () => {
    for (const w of watchers) w.dispose();
    watchers = [];
    batcher?.dispose();
    batcher = null;
    const cfg = vscode.workspace.getConfiguration("codeatlas");
    if (!cfg.get<boolean>("indexOnExternalChange", true)) return;
    const raw = cfg.get<number>("externalChangeDebounceMs", DefaultExternalChangeDebounceMs);
    const delay = Number.isFinite(raw) ? Math.max(MinExternalChangeDebounceMs, Math.floor(raw)) : DefaultExternalChangeDebounceMs;
    batcher = new ChangeBatcher((root, paths) => {
      host.log(`检测到编辑器之外的改动 ${paths.length} 个文件（如 ${paths.slice(0, 3).join("、")}），排一次增量索引`);
      host.requestIndex(root, paths);
    }, delay);
    const glob = `**/*{${[...host.supported].join(",")}}`;
    for (const root of host.roots()) {
      const base = vscode.Uri.file(root);
      // 源文件：增 / 改 / 删
      const files = vscode.workspace.createFileSystemWatcher(new vscode.RelativePattern(base, glob));
      files.onDidCreate((u) => onEvent(u, "create"));
      files.onDidChange((u) => onEvent(u, "change"));
      files.onDidDelete((u) => onEvent(u, "delete"));
      // 删目录（切分支去掉整个目录）只来目录本身的事件，上面的扩展名 glob 匹配不到
      const dirs = vscode.workspace.createFileSystemWatcher(new vscode.RelativePattern(base, "**/*"), true, true, false);
      dirs.onDidDelete((u) => onEvent(u, "delete"));
      watchers.push(files, dirs);
    }
  };

  rebuild();
  const subs = [
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration("codeatlas.indexOnExternalChange") || e.affectsConfiguration("codeatlas.externalChangeDebounceMs")
        || e.affectsConfiguration("codeatlas.projects")) rebuild();
    }),
    vscode.workspace.onDidChangeWorkspaceFolders(() => rebuild()),
  ];

  return {
    markSaved: (file) => batcher?.markSaved(file),
    dispose: () => {
      for (const d of [...watchers, ...subs]) d.dispose();
      batcher?.dispose();
    },
  };
}
