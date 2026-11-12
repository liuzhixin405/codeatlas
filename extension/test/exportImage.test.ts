import { describe, expect, it } from "vitest";
import { exportFileName, fit, flowSvg, graphSvg, LightPalette, rowStyle, type GraphScene } from "../webview/exportImage";
import { layoutFlow } from "../webview/flowLayout";
import { parseSaveImage, safeName } from "../src/imageExport";
import type { FlowDto, FlowNodeDto } from "../webview/types";

function node(partial: Partial<FlowNodeDto> & { label: string; kind: FlowNodeDto["kind"]; actor: string; depth: number }): FlowNodeDto {
  return { symbolId: 1, qualifiedName: partial.label, via: null, confidence: 1, count: 1, children: [], hidden: 0, note: null, ...partial };
}

// Ctl.Create（api 列）→ 分派 Handler.Handle（app 列）→ 0.3 的 Repo.Save（infra 列）；Handler 自调用 Validate
const dto: FlowDto = {
  entrySymbolId: 1, entryLabel: "Ctl.Create", route: null, containerKey: "",
  actors: [{ key: "api", label: "Acme.Api", kind: "container" }, { key: "app", label: "Acme.App", kind: "project" }, { key: "infra", label: "Infra<&>", kind: "external" }],
  root: node({
    label: "Ctl.Create", kind: "entry", actor: "api", depth: 0,
    children: [node({
      label: "Handler.Handle", kind: "dispatch", actor: "app", depth: 1, via: "dispatch:Cmd",
      children: [node({ label: "Repo.Save", kind: "call", actor: "infra", depth: 2, confidence: 0.3 }), node({ label: "Handler.Validate", kind: "call", actor: "app", depth: 2 })],
    })],
  }),
  behaviors: [], nodes: 4, truncated: false, note: null,
};

describe("flowSvg", () => {
  const layout = layoutFlow(dto, new Set());

  it("一列一个参与者框 + 生命线，一行一个箭头，坐标按列中线", () => {
    const svg = flowSvg(layout, "调用 Ctl.Create");
    expect(svg.startsWith('<svg xmlns="http://www.w3.org/2000/svg" width="534" height="182"')).toBe(true); // 12*2 + 3*170；12+22+32 + 4*26 + 12
    expect(svg.match(/<rect x="/g)?.length).toBe(3);
    expect(svg).toContain('<text x="12" y="25" fill="#1f2328" font-weight="600" font-size="12">调用 Ctl.Create</text>');
    expect(svg).toContain(">Infra&lt;&amp;&gt;</text>"); // 转义
    // 分派：从 api 列（x=97）到 app 列（x=267），橙色虚线，箭头尖在 267
    expect(svg).toContain('<g opacity="1" fill="#d18616" stroke="#d18616"><line x1="97" y1="111" x2="260" y2="111" stroke-width="1.5" stroke-dasharray="5 3"/><polygon points="267,111 260,106.5 260,115.5" stroke="none"/>');
    // 自调用画回环
    expect(svg).toContain('<path d="M 267 154 h 12 a 5 5 0 0 1 0 10 h -12"');
  });

  it("低置信度半透明；weakDashed 时再画虚线", () => {
    const weak = layout.rows[2]!;
    expect(rowStyle(weak, LightPalette)).toEqual({ color: "#1f2328", dash: null, opacity: 0.65, italic: false });
    expect(rowStyle(weak, LightPalette, true).dash).toBe("5 3");
    expect(flowSvg(layout, "t", { weakDashed: true })).toContain('<g opacity="0.65" fill="#1f2328" stroke="#1f2328"><line x1="267" y1="137" x2="430" y2="137" stroke-width="1.5" stroke-dasharray="5 3"/>');
  });
});

describe("graphSvg", () => {
  const scene: GraphScene = {
    bg: "#fff", title: "调用图 A",
    nodes: [
      { x: 0, y: 0, w: 80, h: 26, label: "A.Run", shape: "round-rectangle", fill: "#eee", stroke: "#999", strokeWidth: 1, strokeStyle: "solid", textColor: "#333", fontSize: 11, opacity: 1, parent: false },
      { x: 200, y: 100, w: 60, h: 40, label: "IRepo", shape: "diamond", fill: "#eee", stroke: "#999", strokeWidth: 1, strokeStyle: "dotted", textColor: "#333", fontSize: 11, opacity: 1, parent: false },
    ],
    edges: [{ points: [{ x: 40, y: 0 }, { x: 170, y: 100 }], curve: "polyline", color: "#f59e0b", width: 2, style: "dashed", opacity: 1, arrow: true, label: "new Cmd" }],
  };

  it("外框按节点与边算、留 20 边距，标题占一行；形状、线型、标签都画出来", () => {
    const svg = graphSvg(scene);
    expect(svg).toContain('width="310" height="195"'); // x: -40..230 → 270+40；y: -13..120 → 133+40+22（标题）
    expect(svg).toContain('<rect x="20" y="42" width="80" height="26" rx="6.5" fill="#eee" stroke="#999" stroke-width="1"/>');
    expect(svg).toContain('<polygon points="260,135 290,155 260,175 230,155" fill="#eee" stroke="#999" stroke-width="1" stroke-dasharray="1 2"/>');
    expect(svg).toContain('<path d="M 100 55 L 230 155" fill="none" stroke="#f59e0b" stroke-width="2" stroke-dasharray="8 6"/>');
    expect(svg).toContain(">new Cmd</text>");
    expect(svg).toContain(">调用图 A</text>");
  });
});

describe("文字与文件名", () => {
  it("fit 按估算宽度截断，CJK 记宽", () => {
    expect(fit("OrderService.Place", 200, 6.6)).toBe("OrderService.Place");
    expect(fit("OrderService.Place", 60, 6.6)).toBe("OrderSer…");
    expect(fit("订单服务下单", 40, 6.6)).toBe("订单…");
  });

  it("exportFileName 去掉非法字符、补扩展名；空名用默认", () => {
    expect(exportFileName("seq-Ctl.Create<T>", "svg")).toBe("seq-Ctl.Create_T.svg");
    expect(exportFileName("paths-A → B", "png")).toBe("paths-A_→_B.png");
    expect(exportFileName("", "png")).toBe("codetrail-graph.png");
  });
});

describe("parseSaveImage（扩展侧校验）", () => {
  const png = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3]).toString("base64");

  it("SVG 文本与带 PNG 文件头的 base64 才收", () => {
    const svg = parseSaveImage({ type: "saveImage", format: "svg", data: '<svg xmlns="http://www.w3.org/2000/svg"></svg>', name: "a.svg" });
    expect(svg?.format).toBe("svg");
    expect(svg?.name).toBe("a.svg");
    expect(new TextDecoder().decode(svg!.bytes)).toBe('<svg xmlns="http://www.w3.org/2000/svg"></svg>');
    const p = parseSaveImage({ format: "png", data: png, name: "../../evil/x.png" });
    expect(p?.bytes.length).toBe(11);
    expect(p?.name).toBe("x.png"); // 只取文件名
    expect(parseSaveImage({ format: "png", data: Buffer.from("GIF89a").toString("base64"), name: "x" })).toBeNull();
    expect(parseSaveImage({ format: "svg", data: "<html><script>", name: "x" })).toBeNull();
    expect(parseSaveImage({ format: "exe", data: "x", name: "x" })).toBeNull();
    expect(parseSaveImage(null)).toBeNull();
  });

  it("safeName 去掉 Windows 不认的字符", () => {
    expect(safeName('a:b*?"c', "svg")).toBe("a_b_c.svg");
    expect(safeName(String.raw`C:\tmp\graph.PNG`, "png")).toBe("graph.png");
  });
});
