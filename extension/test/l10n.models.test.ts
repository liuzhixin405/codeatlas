// 英文界面下的模型文案：调用大纲 / 影响范围 / CodeLens / 快速跳转。
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { EntryDto, ImpactCallerDto, ImpactEntryDto, OutlineStepDto, SymbolDto, SymbolLensDto } from "../webview/types";

function sym(id: number, qualifiedName: string, over: Partial<SymbolDto> = {}): SymbolDto {
  const name = qualifiedName.split(".").pop()!;
  return {
    id, kind: "method", name, qualifiedName, signature: null, path: "src/Ordering/OrderService.cs",
    startLine: 20, startCol: 5, endLine: 60, endCol: 6, visibility: "public", ...over,
  };
}

function step(over: Partial<OutlineStepDto> = {}): OutlineStepDto {
  return {
    order: 1, kind: "call", label: "OrderService.Create", target: sym(2, "Acme.Shop.OrderService.Create"),
    line: 42, pos: 1234, ctx: null, confidence: 1, expandable: true, repeat: 1, children: [], ...over,
  };
}

function entry(over: Partial<EntryDto> = {}): EntryDto {
  return {
    symbolId: 50, kind: "http", ruleId: "aspnet-minimal", route: "/api/orders", httpMethod: "POST", name: "CreateOrder",
    qualifiedName: "Acme.Shop.Api.OrdersApi.CreateOrder", file: "src/Api/OrdersApi.cs", line: 12, ...over,
  };
}

function ie(depth: number): ImpactEntryDto {
  return { entry: entry(), containerKey: "src/Api", containerName: "Ordering.API", depth };
}

function caller(over: Partial<ImpactCallerDto> = {}): ImpactCallerDto {
  return {
    kind: "call", label: "OrdersApi.CreateOrderAsync",
    caller: sym(2, "Acme.Shop.Api.OrdersApi.CreateOrderAsync", { path: "src/Api/OrdersApi.cs", startLine: 38 }),
    sites: [{ line: 45, pos: 1500, ctx: null }], confidence: 1, expandable: true, asEntry: [], ...over,
  };
}

function lens(over: Partial<SymbolLensDto> = {}): SymbolLensDto {
  return { symbol: sym(1, "Acme.Shop.OrderService.PlaceOrder"), callers: 2, callSites: 2, testCallers: 0, callees: 4, isEntry: false, ...over };
}

beforeEach(() => {
  vi.stubEnv("CODEATLAS_LOCALE", "en");
  vi.resetModules();
});

afterEach(() => {
  vi.unstubAllEnvs();
  vi.resetModules();
});

describe("英文界面：调用大纲", () => {
  it("ctx 标签、描述、置信度、tooltip", async () => {
    const m = await import("../src/outlineModel");
    expect(m.CtxLabels.loop).toBe("↻ in loop");
    expect(m.CtxLabels.callback).toBe("λ callback");
    expect(m.stepDescription(step({ kind: "external", ctx: "cond" }), true)).toBe("↺ recursive · framework · ? conditional · L42");
    expect(m.confidenceText(0.5)).toBe("confidence 0.50 (guess)");
    const tip = m.stepTooltip({ type: "step", step: step({ repeat: 3 }), containerPath: "src/A.cs", ancestors: new Set([1]) }, false);
    expect(tip).toContain("Definition: src/Ordering/OrderService.cs:20");
    expect(tip).toContain("Call site: src/A.cs:42");
    expect(tip).toContain("call · confidence 1.00");
    expect(tip).toContain("3 adjacent identical calls merged");
    const kids = m.outlineChildren({ symbol: sym(1, "A.B"), steps: [], truncated: false }, new Set([1]));
    expect(kids[0]).toMatchObject({ type: "info", text: "(no calls)" });
  });
});

describe("英文界面：影响范围", () => {
  it("调用方式标签、分组标题、单复数", async () => {
    const m = await import("../src/impactModel");
    expect(m.CallerKindLabels.impl).toBe("via interface");
    expect(m.CallerKindLabels.call).toBeNull();
    expect(m.EntryPrefixes.job).toBe("job");
    expect(m.callersGroupItem({ type: "callers", count: 3, truncated: true, children: [] }).label).toBe("Direct callers (3+)");
    expect(m.testsItem({ type: "tests", count: 1 }).label).toBe("Called by 1 test");
    expect(m.testsItem({ type: "tests", count: 2 }).label).toBe("Called by 2 tests");
    expect(m.containerItem({ type: "container", key: "k", name: "API", entries: [ie(1)], only: true }).description).toBe("1 entry");
    expect(m.containerItem({ type: "container", key: "k", name: "API", entries: [ie(1), ie(2)], only: true }).description).toBe("2 entries");
    expect(m.entriesGroupItem({ type: "entries", entries: [ie(1)], truncated: true })).toMatchObject({
      label: "Affected entries (1)", description: "(may be incomplete)",
    });
    expect(m.entryItem({ type: "entry", item: ie(1) }).tooltip).toContain("Reached 1 level up the call chain");
    const c = caller({ kind: "impl", sites: [{ line: 45, pos: null, ctx: "loop" }, { line: 50, pos: null, ctx: null }] });
    expect(m.callerDescription(c, true)).toBe("↺ recursive · via interface · ×2 · ↻ in loop · L45");
    const tip = m.callerTooltip(c, false);
    expect(tip).toContain("Call sites (src/Api/OrdersApi.cs): L45 (↻ in loop), L50");
    expect(tip).toContain("Click to jump to the first call site");
  });
});

describe("英文界面：CodeLens", () => {
  it("一行：entry · N callers · K tests · calls M · Impact", async () => {
    const m = await import("../src/codeLensModel");
    const titles = m.lensSpecs(lens({ isEntry: true, testCallers: 3, dispatches: 1 }), false).map((l) => l.title);
    expect(titles).toEqual(["entry", "2 callers", "3 tests", "calls 4 · dispatch 1", "Impact"]);
    expect(m.callersTitle(1, 1)).toBe("1 caller");
    expect(m.callersTitle(0, 0)).toBe("no callers");
    expect(m.callersTitle(1, 3)).toBe("1 caller · 3 call sites");
    expect(m.lensSpecs(lens({ testCallers: 1 }), false)[1]!.title).toBe("1 test");
    expect(m.StaleSuffix).toBe(" (index out of date)");
  });
});

describe("英文界面：快速跳转", () => {
  it("调用方式标签、被调方 / 入口项", async () => {
    const m = await import("../src/jumpModel");
    expect(m.CallerKindPickLabels.call).toBe("call");
    expect(m.CallerKindPickLabels.dispatch).toBe("via dispatch");
    expect(m.linesText([1, 2, 3, 4, 5, 6, 7])).toBe("L1, L2, L3, L4, L5, L6 … (7 total)");
    expect(m.callerPickItem(caller({ kind: "new" })).description).toBe("constructor · L45");
    const amb = step({ kind: "ambiguous", target: null, children: [step()] });
    expect(m.calleePickItem(amb, "src/A.cs").detail).toBe("1 candidate");
    expect(m.calleePickItem({ ...amb, children: [step(), step()] }, "src/A.cs").detail).toBe("2 candidates");
    expect(m.calleePickItem(step({ kind: "external", target: null }), "src/A.cs").detail).toBe("outside repository");
    expect(m.entryPickItem(ie(0)).description).toBe("Ordering.API · is itself an entry");
    expect(m.entryPickItem(ie(1)).description).toBe("Ordering.API · 1 level up");
    expect(m.entryPickItem(ie(3)).description).toBe("Ordering.API · 3 levels up");
  });
});
