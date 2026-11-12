import type { FlowLayout, FlowRow } from "./flowLayout";
import { rowText } from "./seqDiagram";

/**
 * 把面板里的图导出成独立的 SVG（贴 PR / 文档用）。纯函数，不碰 DOM：
 * - flowSvg：时序图（调用图时序、两点间路径、入口流程、框架流程）按 flowLayout 算好的列与行重画成 SVG，箭头样式与面板一致
 *   （分派橙色虚线、跨服务紫色、框架调用点线、低置信度半透明；weakDashed 时低置信度也画虚线，路径图用）。
 * - graphSvg：cytoscape 画的图（网状调用图、模块关系、容器图、命名空间地图）先在 main 里抽成 GraphScene（节点框 + 边的折线），再画成 SVG。
 * PNG：cytoscape 的图直接用 cy.png；时序图把这里的 SVG 画进 canvas（svgToPng，在 main 里，需要 DOM）。
 * 文件由扩展侧弹保存对话框写（消息 saveImage，见 src/imageExport.ts）：webview 里不能直接写文件，剪贴板写图片也不可靠。
 */

export interface FlowPalette {
  bg: string;
  fg: string;
  muted: string;
  border: string;
}

export const LightPalette: FlowPalette = { bg: "#ffffff", fg: "#1f2328", muted: "#6e7781", border: "#d0d7de" };

const ColW = 170;
const RowH = 26;
const Pad = 12;
const TitleH = 22;
const HeadH = 32;

/** 箭头颜色与线型：与 panel.css 的 .flow-arrow.kind-* 一致。 */
export function rowStyle(row: FlowRow, p: FlowPalette, weakDashed = false): { color: string; dash: string | null; opacity: number; italic: boolean } {
  const n = row.node;
  let color = p.fg;
  let dash: string | null = null;
  if (n.kind === "dispatch") { color = "#d18616"; dash = "5 3"; }
  else if (n.kind === "hop") color = "#8957e5";
  else if (n.kind === "external-group") { color = p.muted; dash = "1.5 2.5"; }
  else if (n.kind === "more") color = p.muted;
  else if (n.kind === "function") color = "#22a06b";
  else if (n.kind === "http") { color = "#5b7a99"; dash = "5 3"; }
  if (row.via?.cls === "via-render") color = "#22a06b";
  if (row.via?.cls === "via-event") { color = "#8a63d2"; dash = "5 3"; }
  const weak = n.confidence < 0.7;
  if (weak && weakDashed && dash === null) dash = "5 3";
  return { color, dash, opacity: weak ? (n.kind === "http" ? 0.85 : 0.65) : 1, italic: n.kind === "more" };
}

/** 时序图 → SVG。title 画在最上面一行（如「调用 OrderService.Place」）。 */
export function flowSvg(layout: FlowLayout, title: string, opts: { palette?: FlowPalette; weakDashed?: boolean } = {}): string {
  const p = opts.palette ?? LightPalette;
  const cols = Math.max(1, layout.columns.length);
  const width = Pad * 2 + cols * ColW;
  const top = Pad + TitleH + HeadH;
  const height = top + layout.rows.length * RowH + Pad;
  const cx = (c: number): number => Pad + c * ColW + ColW / 2;
  const out: string[] = [];
  out.push(`<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" font-family="Consolas, 'Courier New', monospace" font-size="11">`);
  out.push(`<rect width="100%" height="100%" fill="${p.bg}"/>`);
  out.push(`<text x="${Pad}" y="${Pad + 13}" fill="${p.fg}" font-weight="600" font-size="12">${xml(fit(title, width - Pad * 2, 7))}</text>`);
  // 参与者与生命线
  layout.columns.forEach((a, i) => {
    const x = cx(i);
    const y = Pad + TitleH;
    const dashed = a.kind === "external" ? ` stroke-dasharray="4 2"` : "";
    out.push(`<line x1="${x}" y1="${y + 22}" x2="${x}" y2="${height - Pad}" stroke="${p.border}"/>`);
    out.push(`<rect x="${x - ColW / 2 + 8}" y="${y}" width="${ColW - 16}" height="22" rx="3" fill="${p.bg}" stroke="${a.kind === "external" ? p.border : p.fg}"${dashed}/>`);
    out.push(`<text x="${x}" y="${y + 15}" text-anchor="middle" fill="${a.kind === "external" ? p.muted : p.fg}"${a.kind === "container" ? ` font-weight="600"` : ""}>${xml(fit(a.label, ColW - 24, 6.6))}</text>`);
  });
  layout.rows.forEach((row, i) => {
    const y = top + i * RowH;
    const ly = y + RowH - 7;
    const st = rowStyle(row, p, opts.weakDashed);
    const dash = st.dash ? ` stroke-dasharray="${st.dash}"` : "";
    const g = `<g opacity="${st.opacity}" fill="${st.color}" stroke="${st.color}"${st.italic ? ` font-style="italic"` : ""}>`;
    const text = labelText(row);
    const from = cx(row.fromCol);
    const to = cx(row.toCol);
    if (row.node.kind === "entry") {
      out.push(`${g}<circle cx="${to}" cy="${ly}" r="4.5" stroke="none"/><text x="${to + 9}" y="${ly + 4}" stroke="none">${xml(fit(text, ColW * 2, 6.6))}</text></g>`);
    } else if (row.direction === "self") {
      out.push(`${g}<path d="M ${to} ${ly - 9} h 12 a 5 5 0 0 1 0 10 h -12" fill="none" stroke-width="1.5"${dash}/>` +
        `<polygon points="${to},${ly + 1} ${to + 7},${ly - 3.5} ${to + 7},${ly + 5.5}" stroke="none"/>` +
        `<text x="${to + 22}" y="${ly + 3}" stroke="none">${xml(fit(text, ColW * 2, 6.6))}</text></g>`);
    } else {
      const dir = to > from ? 1 : -1;
      const tip = to;
      const span = Math.abs(to - from);
      const label = dir > 0
        ? `<text x="${tip - 8}" y="${ly - 5}" text-anchor="end" stroke="none">${xml(fit(text, Math.max(span - 12, ColW - 20), 6.6))}</text>`
        : `<text x="${tip + 8}" y="${ly - 5}" stroke="none">${xml(fit(text, Math.max(span - 12, ColW - 20), 6.6))}</text>`;
      out.push(`${g}<line x1="${from}" y1="${ly}" x2="${tip - dir * 7}" y2="${ly}" stroke-width="1.5"${dash}/>` +
        `<polygon points="${tip},${ly} ${tip - dir * 7},${ly - 4.5} ${tip - dir * 7},${ly + 4.5}" stroke="none"/>${label}</g>`);
    }
  });
  out.push("</svg>");
  return out.join("\n");
}

/** 一行的文字：与面板同一个 rowText，再接来历标记（渲染 / 回调）与折起的孩子数。 */
function labelText(row: FlowRow): string {
  let t = rowText(row);
  if (row.via) t += ` [${row.via.tag}]`;
  if (row.node.kind === "hop" && row.node.via) t += ` [${row.node.via}]`;
  if (row.collapsed) t += ` (${row.node.children.length})`;
  return t;
}

// ---------- cytoscape 图 ----------

export interface Point { x: number; y: number }

export interface SceneNode {
  x: number; y: number; w: number; h: number;
  label: string;
  /** cytoscape 的 shape：rectangle / round-rectangle / ellipse / diamond / hexagon / barrel……认不得的按圆角矩形。 */
  shape: string;
  fill: string; stroke: string; strokeWidth: number;
  strokeStyle: "solid" | "dashed" | "dotted";
  textColor: string; fontSize: number; opacity: number;
  /** 复合节点（分组框）：先画、标签在框上沿。 */
  parent: boolean;
}

export interface SceneEdge {
  /** 起点、控制点 / 折点、终点。 */
  points: Point[];
  /** bezier：points 中间是二次贝塞尔控制点；其余按折线。 */
  curve: "bezier" | "polyline";
  color: string; width: number; style: "solid" | "dashed" | "dotted"; opacity: number;
  arrow: boolean;
  label: string;
}

export interface GraphScene { nodes: SceneNode[]; edges: SceneEdge[]; bg: string; title?: string }

const Margin = 20;

/** cytoscape 场景 → SVG：按全部节点框与边点算外框，留 20px 边。 */
export function graphSvg(scene: GraphScene): string {
  const xs: number[] = [];
  const ys: number[] = [];
  for (const n of scene.nodes) { xs.push(n.x - n.w / 2, n.x + n.w / 2); ys.push(n.y - n.h / 2, n.y + n.h / 2); }
  for (const e of scene.edges) for (const pt of e.points) { xs.push(pt.x); ys.push(pt.y); }
  const minX = xs.length ? Math.min(...xs) : 0;
  const minY = ys.length ? Math.min(...ys) : 0;
  const titleH = scene.title ? 22 : 0;
  const ox = Margin - minX;
  const oy = Margin + titleH - minY;
  const width = Math.ceil((xs.length ? Math.max(...xs) - minX : 0) + Margin * 2);
  const height = Math.ceil((ys.length ? Math.max(...ys) - minY : 0) + Margin * 2 + titleH);
  const r = (v: number): string => String(Math.round(v * 10) / 10);
  const out: string[] = [];
  out.push(`<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" font-family="system-ui, 'Segoe UI', sans-serif">`);
  out.push(`<rect width="100%" height="100%" fill="${scene.bg}"/>`);
  const firstText = scene.nodes.find((n) => !n.parent)?.textColor ?? "#333";
  if (scene.title) out.push(`<text x="${Margin}" y="${Margin + 12}" font-size="12" font-weight="600" fill="${firstText}">${xml(scene.title)}</text>`);
  const dashOf = (s: "solid" | "dashed" | "dotted", w: number): string => s === "dashed" ? ` stroke-dasharray="${r(w * 4)} ${r(w * 3)}"` : s === "dotted" ? ` stroke-dasharray="${r(w)} ${r(w * 2)}"` : "";

  for (const n of [...scene.nodes.filter((n) => n.parent), ...scene.nodes.filter((n) => !n.parent)]) {
    const x = n.x + ox, y = n.y + oy;
    const attrs = `fill="${n.fill}" stroke="${n.stroke}" stroke-width="${n.strokeWidth}"${dashOf(n.strokeStyle, n.strokeWidth || 1)}`;
    out.push(`<g opacity="${n.opacity}">${shapeSvg(n.shape, x, y, n.w, n.h, attrs, r)}` +
      (n.label ? `<text x="${r(x)}" y="${r(n.parent ? y - n.h / 2 + n.fontSize + 3 : y + n.fontSize * 0.35)}" text-anchor="middle" font-size="${n.fontSize}" fill="${n.textColor}">${xml(fit(n.label, n.w - 6, n.fontSize * 0.6))}</text>` : "") + "</g>");
  }
  for (const e of scene.edges) {
    const pts = e.points.map((pt) => ({ x: pt.x + ox, y: pt.y + oy }));
    if (pts.length < 2) continue;
    const d = edgePath(pts, e.curve, r);
    const last = pts[pts.length - 1]!;
    const prev = pts[pts.length - 2]!;
    let arrow = "";
    if (e.arrow) {
      const a = Math.atan2(last.y - prev.y, last.x - prev.x);
      const s = 4 + e.width * 1.5;
      const p1 = { x: last.x - s * Math.cos(a - 0.45), y: last.y - s * Math.sin(a - 0.45) };
      const p2 = { x: last.x - s * Math.cos(a + 0.45), y: last.y - s * Math.sin(a + 0.45) };
      arrow = `<polygon points="${r(last.x)},${r(last.y)} ${r(p1.x)},${r(p1.y)} ${r(p2.x)},${r(p2.y)}" fill="${e.color}"/>`;
    }
    const mid = pts[Math.floor(pts.length / 2)]!;
    const midPrev = pts[Math.floor(pts.length / 2) - 1] ?? pts[0]!;
    const lx = pts.length === 2 ? (pts[0]!.x + last.x) / 2 : (mid.x + midPrev.x) / 2;
    const ly = pts.length === 2 ? (pts[0]!.y + last.y) / 2 : (mid.y + midPrev.y) / 2;
    out.push(`<g opacity="${e.opacity}"><path d="${d}" fill="none" stroke="${e.color}" stroke-width="${e.width}"${dashOf(e.style, e.width)}/>${arrow}` +
      (e.label ? `<text x="${r(lx)}" y="${r(ly - 3)}" text-anchor="middle" font-size="10" fill="${e.color}">${xml(e.label)}</text>` : "") + "</g>");
  }
  out.push("</svg>");
  return out.join("\n");
}

function shapeSvg(shape: string, x: number, y: number, w: number, h: number, attrs: string, r: (v: number) => string): string {
  const l = x - w / 2, t = y - h / 2;
  const poly = (pts: [number, number][]): string => `<polygon points="${pts.map(([a, b]) => `${r(a)},${r(b)}`).join(" ")}" ${attrs}/>`;
  switch (shape) {
    case "ellipse": return `<ellipse cx="${r(x)}" cy="${r(y)}" rx="${r(w / 2)}" ry="${r(h / 2)}" ${attrs}/>`;
    case "diamond": return poly([[x, t], [l + w, y], [x, t + h], [l, y]]);
    case "hexagon": return poly([[l + w * 0.25, t], [l + w * 0.75, t], [l + w, y], [l + w * 0.75, t + h], [l + w * 0.25, t + h], [l, y]]);
    case "rectangle": return `<rect x="${r(l)}" y="${r(t)}" width="${r(w)}" height="${r(h)}" ${attrs}/>`;
    default: return `<rect x="${r(l)}" y="${r(t)}" width="${r(w)}" height="${r(h)}" rx="${r(Math.min(8, h / 4))}" ${attrs}/>`;
  }
}

function edgePath(pts: Point[], curve: "bezier" | "polyline", r: (v: number) => string): string {
  const first = pts[0]!;
  const last = pts[pts.length - 1]!;
  if (curve === "bezier" && pts.length === 3) return `M ${r(first.x)} ${r(first.y)} Q ${r(pts[1]!.x)} ${r(pts[1]!.y)} ${r(last.x)} ${r(last.y)}`;
  if (curve === "bezier" && pts.length === 4) return `M ${r(first.x)} ${r(first.y)} C ${r(pts[1]!.x)} ${r(pts[1]!.y)} ${r(pts[2]!.x)} ${r(pts[2]!.y)} ${r(last.x)} ${r(last.y)}`;
  return "M " + pts.map((pt) => `${r(pt.x)} ${r(pt.y)}`).join(" L ");
}

// ---------- 文本 ----------

/** 按估算宽度截断（CJK 记两个字宽），超出时末尾加 …。 */
export function fit(s: string, maxPx: number, charPx: number): string {
  let w = 0;
  for (let i = 0; i < s.length; i++) {
    w += s.charCodeAt(i) > 0x2e80 ? charPx * 1.8 : charPx;
    if (w > maxPx) return s.slice(0, Math.max(0, i - 1)) + "…";
  }
  return s;
}

export function xml(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&apos;" })[c]!);
}

/** 导出文件名：去掉文件系统不认的字符，限长，补扩展名。 */
export function exportFileName(base: string, ext: "svg" | "png"): string {
  const clean = base.replace(/[\\/:*?"<>|\s]+/g, "_").replace(/^_+|_+$/g, "").slice(0, 80) || "codetrail-graph";
  return `${clean}.${ext}`;
}

// ---------- 从 cytoscape 抽场景（需要真实的 cy 实例，main 里调） ----------

type Cy = import("cytoscape").Core;

function lineStyle(v: unknown): "solid" | "dashed" | "dotted" {
  return v === "dashed" ? "dashed" : v === "dotted" ? "dotted" : "solid";
}

function px(v: unknown, fallback: number): number {
  const n = parseFloat(String(v));
  return Number.isFinite(n) ? n : fallback;
}

/** 可见的节点与边按模型坐标抽出来（不受当前缩放 / 平移影响）。 */
export function sceneFromCy(cy: Cy, bg: string, title?: string): GraphScene {
  const nodes: SceneNode[] = [];
  cy.nodes(":visible").forEach((n) => {
    const parent = n.isParent();
    const bb = n.boundingBox({ includeLabels: false, includeOverlays: false });
    nodes.push({
      x: (bb.x1 + bb.x2) / 2, y: (bb.y1 + bb.y2) / 2, w: bb.w, h: bb.h,
      label: String(n.style("label") ?? ""),
      shape: String(n.style("shape")),
      fill: String(n.style("background-color")),
      stroke: String(n.style("border-color")),
      strokeWidth: px(n.style("border-width"), 1),
      strokeStyle: lineStyle(n.style("border-style")),
      textColor: String(n.style("color")),
      fontSize: px(n.style("font-size"), 11),
      opacity: n.effectiveOpacity(),
      parent,
    });
  });
  const edges: SceneEdge[] = [];
  cy.edges(":visible").forEach((e) => {
    const curve = String(e.style("curve-style"));
    const mids = (curve === "segments" || curve === "taxi" ? e.segmentPoints() : e.controlPoints()) ?? [];
    edges.push({
      points: [e.sourceEndpoint(), ...mids, e.targetEndpoint()].map((p) => ({ x: p.x, y: p.y })),
      curve: curve.includes("bezier") && mids.length > 0 ? "bezier" : "polyline",
      color: String(e.style("line-color")),
      width: px(e.style("width"), 1),
      style: lineStyle(e.style("line-style")),
      opacity: e.effectiveOpacity(),
      arrow: String(e.style("target-arrow-shape")) !== "none",
      label: String(e.style("label") ?? ""),
    });
  });
  return { nodes, edges, bg, title };
}
