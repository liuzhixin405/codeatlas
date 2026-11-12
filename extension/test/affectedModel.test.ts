import { describe, expect, it } from "vitest";
import {
  affectedChildren, affectedClick, affectedDefinition, affectedItemSpec, affectedRootChildren, isAffectedNode, isGuess, lineList,
  type AffectedGroupNode, type AffectedNode, type AffectedRootNode, type AffectedSymbolNode, type AffectedTestFileNode,
} from "../src/affectedModel";
import type { AffectedDto, AffectedEntryDto, AffectedSymbolDto, AffectedTestDto, EntryDto, SymbolDto } from "../webview/types";

function sym(id: number, qualifiedName: string, over: Partial<SymbolDto> = {}): SymbolDto {
  return {
    id, kind: "method", name: qualifiedName.split(".").pop()!, qualifiedName, signature: null, path: "src/App/Order.cs",
    startLine: 6, startCol: 5, endLine: 12, endCol: 6, visibility: "public", ...over,
  };
}

function entry(over: Partial<EntryDto> = {}): EntryDto {
  return { symbolId: 50, kind: "http", ruleId: "aspnet-minimal", route: "/api/orders", httpMethod: "POST", name: "Post", qualifiedName: "App.OrdersApi.Post", file: "src/Api/Api.cs", line: 5, ...over };
}

function ae(confidence: number, from: number[], over: Partial<AffectedEntryDto> = {}): AffectedEntryDto {
  return { entry: entry(), containerKey: "src/Api", containerName: "Ordering.API", depth: 1, confidence, from, ...over };
}

function at(id: number, label: string, path: string, confidence: number, from: number[], depth = 1): AffectedTestDto {
  return { symbol: sym(id, `App.Tests.${label}`, { path, startLine: 3 }), label, depth, confidence, from };
}

const place: AffectedSymbolDto = {
  symbol: sym(1, "App.Order.Place"), label: "Order.Place", reason: "lines", lines: [8, 9, 11], isTest: false,
  entries: [ae(1, [1])], tests: [at(10, "OrderTests.PlaceWorks", "tests/OrderTests.cs", 0.5, [1])], callers: 1, truncated: false,
};
const order: AffectedSymbolDto = {
  symbol: sym(2, "App.Order", { kind: "class", startLine: 3 }), label: "Order", reason: "file", lines: [], isTest: false,
  entries: [], tests: [], callers: 0, truncated: true,
};

function data(over: Partial<AffectedDto> = {}): AffectedDto {
  return {
    base: "HEAD", mergeBase: "0123456789abcdef", input: "git",
    files: [
      { path: "src/App/Order.cs", oldPath: null, status: "modified", analysis: "file", ranges: [{ start: 1, count: 1 }, { start: 8, count: 2 }], outside: [1], symbols: 2, isTest: false, stale: false, note: "改动落在符号之外" },
      { path: "README.md", oldPath: null, status: "modified", analysis: "skipped", ranges: [{ start: 1, count: 1 }], outside: [], symbols: 0, isTest: false, stale: false, note: "非源码文件，不在索引里" },
      { path: "src/Gone.cs", oldPath: null, status: "deleted", analysis: "skipped", ranges: [], outside: [], symbols: 0, isTest: false, stale: false, note: "已删除" },
    ],
    symbols: [place, order],
    entries: [ae(0.5, [1, 2], { entry: entry({ route: "/api/guess", symbolId: 51 }), depth: 3 }), ae(1, [1])],
    tests: [at(10, "OrderTests.PlaceWorks", "tests/OrderTests.cs", 0.5, [1])],
    testFiles: ["tests/OrderTests.cs", "tests/ChangedTests.cs"],
    depth: 15, truncated: true, warnings: ["索引比工作区旧"],
    ...over,
  };
}

const root = (d = data()): AffectedRootNode => ({ type: "aRoot", data: d, repoRoot: "/repo" });

describe("affectedModel 树结构", () => {
  it("根下面：提示在前，再是改动的符号 / 入口 / 测试 / 文件四组", () => {
    const kids = affectedRootChildren(root());
    expect(kids.map((k) => k.type === "aGroup" ? k.group : k.type)).toEqual(["aInfo", "symbols", "entries", "tests", "files"]);
    expect((kids[0] as { text: string }).text).toBe("索引比工作区旧");
  });

  it("没有改动时只有一行说明", () => {
    const kids = affectedRootChildren(root(data({ files: [], symbols: [], entries: [], tests: [], testFiles: [], warnings: [] })));
    expect(kids).toHaveLength(1);
    expect(affectedItemSpec(kids[0]!).label).toBe("没有改动");
  });

  it("改动的符号：按行或文件级，展开看它自己的入口与测试（置信度保留）", () => {
    const group: AffectedGroupNode = { type: "aGroup", group: "symbols", data: data() };
    const [p, o] = affectedChildren(group) as AffectedSymbolNode[];
    const ps = affectedItemSpec(p!);
    expect(ps.label).toBe("Order.Place");
    expect(ps.description).toBe("Order.cs:6 · L8-9, L11 · 入口 1 · 测试 1");
    expect(ps.dim).toBe(false);
    const os = affectedItemSpec(o!);
    expect(os.description).toBe("Order.cs:3 · 文件级 · 入口 0 · 测试 0 · 可能不全");
    expect(os.dim).toBe(true);
    expect(os.icon).toBe("symbol-class");

    const under = affectedChildren(p!);
    expect(under.map((n) => n.type)).toEqual(["aEntry", "aTest"]);
    const test = affectedItemSpec(under[1]!);
    expect(test.description).toBe("L3 · 深度 1 · 推测 0.50");
    expect(test.icon).toBe("question");
    expect(test.iconColor).toBe("editorWarning.foreground");
    expect(test.tooltip).toContain("来自：Order.Place");
    expect(affectedItemSpec(affectedChildren(o!)[0]!).label).toBe("没有走到入口或测试");
  });

  it("受影响的入口：低置信度的能看出来（问号图标、推测标签、组标题计数）", () => {
    const group: AffectedGroupNode = { type: "aGroup", group: "entries", data: data() };
    const gs = affectedItemSpec(group);
    expect(gs.label).toBe("受影响的入口（2）");
    expect(gs.description).toBe("可能不全 · 1 个推测");
    const [guess, sure] = affectedChildren(group);
    const g = affectedItemSpec(guess!);
    expect(g.label).toBe("HTTP POST /api/guess");
    expect(g.description).toBe("Ordering.API · 深度 3 · 推测 0.50");
    expect(g.icon).toBe("question");
    expect(g.tooltip).toContain("来自：Order.Place、Order");
    expect(g.tooltip).toContain("（推测）");
    const s = affectedItemSpec(sure!);
    expect(s.description).toBe("Ordering.API · 深度 1");
    expect(s.icon).toBe("globe");
    expect(s.iconColor).toBeUndefined();
  });

  it("受影响的测试按文件分组；文件本身改了但没走到方法时也列出", () => {
    const group: AffectedGroupNode = { type: "aGroup", group: "tests", data: data() };
    expect(affectedItemSpec(group).label).toBe("受影响的测试（2 个文件）");
    const files = affectedChildren(group) as AffectedTestFileNode[];
    expect(files.map((f) => [f.path, f.tests.length])).toEqual([["tests/OrderTests.cs", 1], ["tests/ChangedTests.cs", 0]]);
    expect(affectedItemSpec(files[1]!).description).toBe("tests · 文件本身改了");
    expect(affectedItemSpec(files[1]!).collapsible).toBe("none");
    expect(affectedChildren(files[0]!).map((n) => affectedItemSpec(n).label)).toEqual(["OrderTests.PlaceWorks"]);
  });

  it("改动的文件：处理方式、跳过原因在悬停里", () => {
    const group: AffectedGroupNode = { type: "aGroup", group: "files", data: data() };
    expect(affectedItemSpec(group).description).toBe("2 个跳过");
    const [order, readme, gone] = affectedChildren(group);
    expect(affectedItemSpec(order!).description).toBe("M · 文件级 · 2 个符号");
    expect(affectedItemSpec(order!).tooltip).toContain("符号之外的改动：L1");
    expect(affectedItemSpec(readme!)).toMatchObject({ description: "M · 跳过", dim: true, icon: "circle-slash" });
    expect(affectedItemSpec(readme!).tooltip).toContain("非源码文件");
    expect(affectedItemSpec(gone!).clickable).toBe(false);
  });
});

describe("affectedModel 跳转", () => {
  it("符号跳到第一处改动行；入口到入口方法；测试到定义；文件到第一处改动；删除的不跳", () => {
    const kids = affectedChildren({ type: "aGroup", group: "symbols", data: data() });
    expect(affectedClick(kids[0]!)).toEqual({ path: "src/App/Order.cs", line: 8, col: null, pos: null });
    expect(affectedClick(kids[1]!)).toEqual({ path: "src/App/Order.cs", line: 3, col: null, pos: null });
    expect(affectedDefinition(kids[0]!)).toEqual({ path: "src/App/Order.cs", line: 6, col: 5, pos: null });
    const [entryNode, testNode] = affectedChildren(kids[0]!);
    expect(affectedClick(entryNode!)).toEqual({ path: "src/Api/Api.cs", line: 5, col: null, pos: null });
    expect(affectedClick(testNode!)).toEqual({ path: "tests/OrderTests.cs", line: 3, col: 5, pos: null });
    const files = affectedChildren({ type: "aGroup", group: "files", data: data() });
    expect(affectedClick(files[0]!)).toEqual({ path: "src/App/Order.cs", line: 1, col: null, pos: null });
    expect(affectedClick(files[2]!)).toBeNull();
    const deletion: AffectedNode = { type: "aFile", file: { ...data().files[0]!, ranges: [{ start: 4, count: 0 }] } };
    expect(affectedClick(deletion)).toEqual({ path: "src/App/Order.cs", line: 5, col: null, pos: null });
  });

  it("识别自己的节点；行号列表合并连续行；0.7 以下算推测", () => {
    expect(isAffectedNode({ type: "aRoot" })).toBe(true);
    expect(isAffectedNode({ type: "root" })).toBe(false);
    expect(lineList([9, 3, 7, 8, 3])).toBe("L3, L7-9");
    expect(isGuess(0.69)).toBe(true);
    expect(isGuess(0.7)).toBe(false);
    expect(affectedItemSpec(root()).description).toBe("相对 HEAD（01234567） → 工作区 · 2 个符号");
  });
});
