import { describe, expect, it } from "vitest";
import { layoutFlow } from "../webview/flowLayout";
import { PathPicker, pathStats, pickPlaceholder } from "../webview/pathView";
import type { CallPathsDto, FlowNodeDto, SymbolDto } from "../webview/types";

function sym(id: number, name: string): SymbolDto {
  return { id, kind: "method", name, qualifiedName: `Acme.${name}`, signature: null, path: "a.cs", startLine: 1, startCol: 1, endLine: 2, endCol: 1, visibility: null } as SymbolDto;
}

function node(label: string, depth: number, children: FlowNodeDto[] = [], extra: Partial<FlowNodeDto> = {}): FlowNodeDto {
  return { symbolId: depth + 1, label, qualifiedName: label, kind: depth === 0 ? "entry" : "call", actor: "a", via: null, confidence: 1, count: 1, depth, children, hidden: 0, note: null, ...extra };
}

// Ctl.Create → Svc.Place → Repo.Save → Db.Write → Log.Flush（深度 4，超过时序图默认展开的 3 层）
const chain = node("Ctl.Create", 0, [node("Svc.Place", 1, [node("Repo.Save", 2, [node("Db.Write", 3, [node("Log.Flush", 4)])])])]);

function dto(partial: Partial<CallPathsDto>): CallPathsDto {
  return {
    fromId: 1, fromLabel: "Ctl.Create", toId: 5, toLabel: "Log.Flush", paths: [], maxDepth: 8, maxPaths: 10, truncated: false,
    flow: { entrySymbolId: 1, entryLabel: "Ctl.Create → Log.Flush", route: null, containerKey: "", actors: [{ key: "a", label: "A", kind: "project" }], root: chain, behaviors: [], nodes: 5, truncated: false, note: null },
    note: null, ...partial,
  };
}

describe("PathPicker", () => {
  it("没有焦点时不进入选终点，给出提示", () => {
    const p = new PathPicker();
    expect(p.arm(null)).toBe("先在调用图里选一个起点（焦点），再点「路径到…」");
    expect(p.armed).toBe(false);
  });

  it("记下起点，下一次选中的就是终点；选到起点自己报错且继续等", () => {
    const p = new PathPicker();
    expect(p.arm(sym(1, "Create"))).toBeNull();
    expect(p.armed).toBe(true);
    expect(p.take(sym(1, "Create"))).toEqual({ error: "终点和起点是同一个符号，换一个" });
    expect(p.armed).toBe(true);
    const r = p.take(sym(5, "Flush"));
    expect(r).toEqual({ from: sym(1, "Create"), to: sym(5, "Flush") });
    expect(p.armed).toBe(false);
    expect(p.take(sym(5, "Flush"))).toEqual({ error: "没有起点" });
  });

  it("取消后不再等终点", () => {
    const p = new PathPicker();
    p.arm(sym(1, "Create"));
    p.cancel();
    expect(p.armed).toBe(false);
    expect(p.from).toBeNull();
    expect(pickPlaceholder("Ctl.Create")).toBe("选终点：从 Ctl.Create 出发的调用路径（Esc 取消）");
  });
});

describe("pathStats", () => {
  const step = { fromId: 1, toId: 2, label: "x", qualifiedName: "x", kind: "call" as const, via: null, confidence: 1, path: "a.cs", line: 1, pos: null, weak: false };
  it("条数、步数、含分派 / 低置信度的条数、截断", () => {
    expect(pathStats(dto({ paths: [{ steps: [step], minConfidence: 1, inferred: false }] }))).toBe("1 条路径 · 最多 8 步");
    expect(pathStats(dto({
      truncated: true, maxPaths: 2,
      paths: [{ steps: [step], minConfidence: 1, inferred: false }, { steps: [{ ...step, kind: "dispatch", weak: true }], minConfidence: 1, inferred: true }],
    }))).toBe("2 条路径 · 最多 8 步 · 1 条含分派 / 低置信度边 · 超过 2 条，已截断");
  });
});

describe("layoutFlow collapseDepth", () => {
  it("默认只展开到第 3 层；路径图传 Infinity 时整条摊开", () => {
    expect(layoutFlow(dto({}).flow, new Set()).rows.map((r) => r.label)).toEqual(["Ctl.Create", "Svc.Place", "Repo.Save", "Db.Write"]);
    expect(layoutFlow(dto({}).flow, new Set(), "first-seen", Infinity).rows.map((r) => r.label)).toEqual(["Ctl.Create", "Svc.Place", "Repo.Save", "Db.Write", "Log.Flush"]);
  });
});
