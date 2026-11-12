/**
 * 页面「⤓ 导出」发来的 saveImage 消息：校验后交给 PanelHost 弹保存对话框写文件。
 * 这里只做纯校验（不 import vscode，vitest 直接测）：格式只认 svg / png，SVG 必须是 &lt;svg 开头的文本，PNG 必须是带 PNG 文件头的 base64；
 * 文件名去掉路径与非法字符（只当默认文件名，用户在对话框里还能改）；超过 MaxImageBytes 拒收。
 */
export const MaxImageBytes = 30 * 1024 * 1024;

export interface SaveImage {
  format: "svg" | "png";
  bytes: Uint8Array;
  name: string;
}

const PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

export function parseSaveImage(msg: unknown): SaveImage | null {
  if (!msg || typeof msg !== "object") return null;
  const m = msg as { format?: unknown; data?: unknown; name?: unknown };
  if ((m.format !== "svg" && m.format !== "png") || typeof m.data !== "string") return null;
  const name = safeName(typeof m.name === "string" ? m.name : "", m.format);
  if (m.format === "svg") {
    if (!/^\s*(<\?xml[^>]*>\s*)?<svg[\s>]/.test(m.data)) return null;
    const bytes = new TextEncoder().encode(m.data);
    return bytes.length > MaxImageBytes ? null : { format: "svg", bytes, name };
  }
  const b64 = m.data.replace(/^data:image\/png;base64,/, "");
  if (!/^[A-Za-z0-9+/]+=*$/.test(b64) || b64.length * 0.75 > MaxImageBytes) return null;
  const bytes = Uint8Array.from(Buffer.from(b64, "base64"));
  if (bytes.length < PngSignature.length || PngSignature.some((b, i) => bytes[i] !== b)) return null;
  return { format: "png", bytes, name };
}

/** 只取文件名部分，去掉 Windows / POSIX 不认的字符，补对扩展名。 */
export function safeName(name: string, ext: "svg" | "png"): string {
  const base = (name.split(/[\\/]/).pop() ?? "").replace(/\.(svg|png)$/i, "").replace(/[<>:"|?*\u0000-\u001f]+/g, "_").trim().slice(0, 80);
  return `${base || "codetrail-graph"}.${ext}`;
}
