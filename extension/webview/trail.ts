/**
 * 可分享的浏览路径（trail）：面板后退 / 前进时间线里能在别的机器上重放的那些步骤。
 * 符号按限定名记（符号 id 只在一份索引里有意义，同事机器上的索引 id 不同）；项目地图里的视图依赖容器键与入口，暂不进 trail。
 * 宿主把它编码进 vscode://<扩展 id>/trail?… 链接（见 src/trailUri.ts），同事点开后扩展把 steps 发回页面按顺序重放。
 */
export type TrailStep =
  /** 调用图：焦点 + 画法。 */
  | { k: "call"; v: "seq" | "graph"; q: string }
  /** 模块关系：中心单元。 */
  | { k: "rel"; q: string }
  /** 两点间调用路径。 */
  | { k: "path"; f: string; t: string };

export interface TrailData {
  steps: TrailStep[];
  /** 分享时停在第几步（0 起）；重放完回到这一步。 */
  at: number;
}

/** 链接里最多带这么多步（取离当前最近的），免得 URI 过长。 */
export const MaxTrailSteps = 30;

/** 校验一步（来自链接，不可信）：字段齐全、类型对、限定名不超长。 */
export function isTrailStep(x: unknown): x is TrailStep {
  if (!x || typeof x !== "object") return false;
  const s = x as Record<string, unknown>;
  const name = (v: unknown): boolean => typeof v === "string" && v.length > 0 && v.length <= 1000;
  if (s["k"] === "call") return (s["v"] === "seq" || s["v"] === "graph") && name(s["q"]);
  if (s["k"] === "rel") return name(s["q"]);
  if (s["k"] === "path") return name(s["f"]) && name(s["t"]);
  return false;
}

/**
 * 按限定名搜符号用的名字：搜索接口按短名前缀匹配，所以取最后一个成员段（去掉参数表与脚本路径前缀）。
 * Acme.Orders.OrderService.Place(Order) → Place；src/app.ts:Svc.find → find；web/page.tsx:LoginPage → LoginPage。
 */
export function searchNameFor(qualifiedName: string): string {
  let q = qualifiedName.replace(/^external:/, "");
  const paren = q.indexOf("(");
  if (paren >= 0) q = q.slice(0, paren);
  const colon = q.lastIndexOf(":");
  if (colon >= 0) q = q.slice(colon + 1);
  // 模块（文件）符号的限定名是路径本身：短名是文件名，不能按 "." 切出扩展名
  else if (/\.(ts|tsx|js|jsx|mjs|cjs|mts|cts|py|go)$/.test(q)) return q.slice(q.lastIndexOf("/") + 1);
  const dot = q.lastIndexOf(".");
  return dot >= 0 ? q.slice(dot + 1) : q;
}

/** 一步的显示文字（重放时找不到符号的提示用）。 */
export function stepText(s: TrailStep): string {
  return s.k === "path" ? `${s.f} → ${s.t}` : s.q;
}
