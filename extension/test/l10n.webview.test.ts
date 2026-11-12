import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// 英文界面：语言在模块加载时定好，所以每个用例先改环境变量、清模块缓存，再动态 import。
beforeEach(() => {
  vi.stubEnv("CODEATLAS_LOCALE", "en");
  vi.resetModules();
});

afterEach(() => {
  vi.unstubAllEnvs();
  vi.resetModules();
});

describe("webview English UI", () => {
  it("flowView entry kind labels", async () => {
    const m = await import("../webview/flowView");
    expect(m.EntryKindLabels.http).toBe("HTTP routes");
    expect(m.EntryKindLabels.job).toBe("Scheduled jobs");
  });

  it("narrationCard labels and status text", async () => {
    const m = await import("../webview/narrationCard");
    expect(m.KindLabels).toEqual({ container: "Container", component: "Component", flow: "Entry Flow" });
    expect(m.formatTime(null)).toBe("Unknown time");
    expect(m.ApiKeyHint).toBe("API key not set (Command Palette: CodeTrail Graph: Set AI API Key)");
    expect(m.generateButtonState(null).label).toBe("Generate");
  });

  it("relationsLayout kind labels and summary", async () => {
    const m = await import("../webview/relationsLayout");
    expect(m.RelationKindLabels.inherits).toBe("inherits");
    expect(m.kindsSummary({ call: 3, implements: 1 })).toBe("call 3 · implements");
  });

  it("formatters pluralize in English", async () => {
    const g = await import("../webview/graphState");
    expect(g.callersCapsuleLabel(1)).toBe("Called from 1 place");
    expect(g.callersCapsuleLabel(4)).toBe("Called from 4 places");
    const d = await import("../webview/dsmView");
    expect(d.rulesSummary({ ruleCount: 2, violationCells: 1 })).toBe("2 declared rules, 1 violation");
    const f = await import("../webview/frameworkModel");
    expect(f.placeholderText({ kind: "handlers", count: 1 })).toBe("1 handler ▸");
    expect(f.messageKindLabel("query")).toBe("Query");
  });
});
