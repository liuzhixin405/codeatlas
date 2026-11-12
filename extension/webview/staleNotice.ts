import { L, listSep } from "./l10n";

/** 提示条里直接列出的文件数；其余的折成「等 N 个」，悬停看全部。 */
export const StaleListedMax = 3;

export interface StaleNotice {
  /** 一行正文。 */
  text: string;
  /** 悬停：全部过期文件，一行一个。 */
  title: string;
}

function baseName(p: string): string {
  const i = p.lastIndexOf("/");
  return i >= 0 ? p.slice(i + 1) : p;
}

/**
 * 服务端在调用图 / 大纲 / 影响 / 模块关系的结果里带的 staleFiles（涉及的文件在上次索引后改过）→ 一条不打扰的提示。
 * 没有过期文件（含旧服务不带这个字段）返回 null，页面 / 侧边栏据此隐藏提示。
 */
export function staleNotice(files: readonly string[] | null | undefined, max = StaleListedMax): StaleNotice | null {
  if (!files || files.length === 0) return null;
  const listed = files.slice(0, max).map(baseName).join(listSep());
  const rest = files.length - Math.min(files.length, max);
  const more = rest > 0 ? L(` 等 ${files.length} 个`, ` and ${rest} more`) : "";
  return {
    text: L(`以下文件在上次索引后已改动，结果可能过期：${listed}${more}`, `These files changed since the last index; results may be stale: ${listed}${more}`),
    title: files.join("\n"),
  };
}

/** 页面里的提示条 HTML：正文 + 「立即重建」按钮（data-act="reindex"，页面接到后发增量索引请求）。files 为空时返回空串。 */
export function staleBarHtml(files: readonly string[] | null | undefined, esc: (s: string) => string): string {
  const n = staleNotice(files);
  if (!n) return "";
  return `<span class="stale-text" title="${esc(n.title)}">${esc(n.text)}</span>` +
    `<button class="secondary" data-act="reindex" title="${L("增量索引：只重解析改过的文件", "Incremental index: re-parse only the changed files")}">${L("立即重建", "Rebuild now")}</button>`;
}
