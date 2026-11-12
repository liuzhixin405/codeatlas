import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import * as path from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// 宿主纯函数的英文文案：切到 en 后重新加载模块（模块级常量在加载时按语言求值）
describe("host l10n (en)", () => {
  beforeEach(() => {
    vi.stubEnv("CODEATLAS_LOCALE", "en");
    vi.resetModules();
  });
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.resetModules();
  });

  it("backendDetect suggestion messages", async () => {
    const m = await import("../src/backendDetect");
    const c = { name: "ccs-backend", sln: "Ccs.sln", dir: "/code/ccs-backend", relative: "../ccs-backend" } as Parameters<typeof m.suggestionMessage>[0];
    expect(m.suggestionMessage(c)).toBe("Found a .NET backend nearby: ccs-backend (Ccs.sln). Indexing it together lets the Project Map show full call chains from frontend pages to backend controllers. Index it together?");
    const d = { ...c, name: "other", sln: "Other.sln" };
    expect(m.multiSuggestionMessage([c, d])).toBe("Found 2 .NET backends nearby (ccs-backend, other). Indexing one together lets the Project Map show full call chains from frontend pages to backend controllers. Index together?");
  });

  it("aiServices labels and service names", async () => {
    const m = await import("../src/aiServices");
    expect(m.AiServicePresets.find((p) => p.id === "local")?.label).toBe("Local llama-server");
    expect(m.AiServicePresets.find((p) => p.id === "dashscope")?.label).toBe("Qwen (Alibaba Cloud Model Studio)");
    expect(m.serviceName("openai", "http://127.0.0.1:8080/v1")).toBe("Local");
    expect(m.serviceName("openai", "https://dashscope.aliyuncs.com/compatible-mode/v1")).toBe("Qwen");
    expect(m.serviceName("anthropic", "https://gw.example.com/v1")).toBe("gw.example.com (Anthropic protocol)");
  });

  it("serverManager user-facing errors", async () => {
    const m = await import("../src/serverManager");
    expect(new m.ServerNotFoundError().message).toBe("Cannot find the codeatlas executable. Set it in the codeatlas.serverPath setting, or add it to PATH.");

    const dir = mkdtempSync(path.join(tmpdir(), "codeatlas-l10n-"));
    try {
      writeFileSync(path.join(dir, "target.json"), JSON.stringify({ target: "darwin-x64" }));
      expect(m.packageTargetMismatch(dir, "1.2.3", "win32", "x64")).toBe(
        "CodeTrail Graph: this package is built for macOS (Intel) (darwin-x64), but the current system is Windows x64 (win32-x64), so the server cannot start. "
        + "Quit VS Code completely, then install codetrail-graph-win32-x64-1.2.3.vsix.");
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }

    const sm = new m.ServerManager(() => null, { info: () => {}, error: () => {} }, () => { throw new Error("unused"); });
    await expect(sm.recover("D:/repo")).rejects.toBeInstanceOf(m.ServerNotFoundError);
    await expect(sm.recover("D:/repo")).rejects.toThrow('Run "CodeTrail Graph: Restart Server" to restart it manually.');
  });
});
