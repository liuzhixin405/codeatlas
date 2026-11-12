import { isTrailStep, MaxTrailSteps, type TrailData, type TrailStep } from "../webview/trail";

/**
 * trail 链接：<scheme>://<扩展 id>/trail?repo=<仓库目录名>&d=<base64url(JSON)>，例如
 * vscode://164910441.codetrail-graph/trail?repo=shop-api&d=eyJ2IjoxLC4uLn0。
 * scheme 用 vscode.env.uriScheme（稳定版 vscode、Insiders 是 vscode-insiders），扩展 id 用 context.extension.id（publisher.name）。
 * 纯函数，不 import vscode（vitest 直接测）；注册 UriHandler 与复制到剪贴板在 trailHandler.ts / panel.ts。
 */
export const TrailPath = "/trail";
export const TrailVersion = 1;

interface Payload { v: number; s: TrailStep[]; a: number }

/** 只留最近的 MaxTrailSteps 步，at 跟着平移；at 越界时夹到范围内。 */
export function clampTrail(t: TrailData): TrailData {
  const drop = Math.max(0, t.steps.length - MaxTrailSteps);
  const steps = t.steps.slice(drop);
  return { steps, at: Math.min(Math.max(0, t.at - drop), Math.max(0, steps.length - 1)) };
}

export function encodeTrail(t: TrailData): string {
  const c = clampTrail(t);
  const payload: Payload = { v: TrailVersion, s: c.steps, a: c.at };
  return Buffer.from(JSON.stringify(payload), "utf8").toString("base64url");
}

/** 解码不可信的 d 参数：版本、步骤逐个校验，任何一处不对返回 null。 */
export function decodeTrail(d: string): TrailData | null {
  if (!/^[A-Za-z0-9_-]+$/.test(d) || d.length > 64 * 1024) return null;
  let p: unknown;
  try { p = JSON.parse(Buffer.from(d, "base64url").toString("utf8")); } catch { return null; }
  if (!p || typeof p !== "object") return null;
  const o = p as Partial<Payload>;
  if (o.v !== TrailVersion || !Array.isArray(o.s) || o.s.length === 0 || o.s.length > MaxTrailSteps || !o.s.every(isTrailStep)) return null;
  const at = typeof o.a === "number" && Number.isInteger(o.a) ? Math.min(Math.max(0, o.a), o.s.length - 1) : o.s.length - 1;
  return { steps: o.s, at };
}

export function trailUri(scheme: string, extensionId: string, repo: string, t: TrailData): string {
  return `${scheme}://${extensionId}${TrailPath}?repo=${encodeURIComponent(repo)}&d=${encodeTrail(t)}`;
}

/** UriHandler 收到的 uri（path + query）→ 仓库名 + trail；不是 trail 链接或内容不合法返回 null。 */
export function parseTrailUri(uri: { path: string; query: string }): { repo: string; trail: TrailData } | null {
  if (uri.path !== TrailPath) return null;
  const q = new URLSearchParams(uri.query);
  const d = q.get("d");
  const trail = d ? decodeTrail(d) : null;
  return trail ? { repo: q.get("repo") ?? "", trail } : null;
}

/** 在打开的工作区文件夹里找链接指的仓库：目录名相同的优先（不分大小写）；只开了一个文件夹时就用它；都对不上返回 null。 */
export function pickTrailFolder<T extends { name: string }>(folders: readonly T[], repo: string): T | null {
  const hit = folders.find((f) => f.name.toLowerCase() === repo.toLowerCase());
  if (hit) return hit;
  return folders.length === 1 ? folders[0]! : null;
}
