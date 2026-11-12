import * as path from "node:path";
import * as vscode from "vscode";
import { L } from "../webview/l10n";
import type { TrailData } from "../webview/trail";
import { parseTrailUri, pickTrailFolder, trailUri } from "./trailUri";

/**
 * 可分享的浏览路径：
 * - 分享：页面「🔗 分享」把时间线里的步骤发来（shareTrail），这里拼成 vscode://<扩展 id>/trail?… 链接复制到剪贴板。
 * - 打开：注册 UriHandler；同事点链接 → VS Code 激活扩展 → 在打开的工作区里按仓库目录名找到仓库 → 打开面板并让页面重放。
 */
export async function copyTrailLink(context: vscode.ExtensionContext, repoRoot: string, trail: TrailData): Promise<void> {
  if (trail.steps.length === 0) {
    void vscode.window.showInformationMessage(L("时间线里还没有能分享的步骤（调用图、模块关系、调用路径）", "Nothing to share yet: the timeline has no Call Graph, Module Relations or call path steps"));
    return;
  }
  const uri = trailUri(vscode.env.uriScheme, context.extension.id, path.basename(repoRoot), trail);
  await vscode.env.clipboard.writeText(uri);
  void vscode.window.showInformationMessage(L(`已复制浏览路径链接（${trail.steps.length} 步）。同事在 VS Code 里打开同一个仓库后点链接即可重放。`, `Copied the trail link (${trail.steps.length} steps). Colleagues open the same repository in VS Code and click the link to replay it.`));
}

export function registerTrailHandler(replay: (repoRoot: string, trail: TrailData) => Promise<void>): vscode.Disposable {
  return vscode.window.registerUriHandler({
    handleUri: async (uri: vscode.Uri) => {
      const parsed = parseTrailUri(uri);
      if (!parsed) { void vscode.window.showErrorMessage(L("CodeTrail Graph：链接无法识别或已损坏", "CodeTrail Graph: the link is not recognized or is corrupted")); return; }
      const folders = (vscode.workspace.workspaceFolders ?? []).map((f) => ({ name: f.name, fsPath: f.uri.fsPath }));
      const folder = pickTrailFolder(folders, parsed.repo);
      if (!folder) {
        void vscode.window.showErrorMessage(L(`CodeTrail Graph：先在 VS Code 里打开仓库「${parsed.repo}」，再点一次链接`, `CodeTrail Graph: open the repository "${parsed.repo}" in VS Code first, then click the link again`));
        return;
      }
      await replay(folder.fsPath, parsed.trail);
    },
  });
}
