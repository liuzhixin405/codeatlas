import { readFileSync } from "node:fs";
import * as path from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { L, listSep, locale, normalizeLocale, setLocale } from "../webview/l10n";

const extensionDir = path.join(__dirname, "..");
const readJson = (name: string) => JSON.parse(readFileSync(path.join(extensionDir, name), "utf8")) as Record<string, unknown>;

describe("l10n 语言判定", () => {
  afterEach(() => setLocale("zh-cn"));

  it("zh* 都按中文，其余按英文", () => {
    for (const lang of ["zh-cn", "zh-CN", "zh-tw", "zh"]) expect(normalizeLocale(lang)).toBe("zh");
    for (const lang of ["en", "en-us", "ja", "de", "", undefined, null]) expect(normalizeLocale(lang)).toBe("en");
  });

  it("测试环境默认中文（vitest.config 设了 CODEATLAS_LOCALE）", () => {
    expect(locale()).toBe("zh");
    expect(L("调用图", "Call Graph")).toBe("调用图");
    expect(listSep()).toBe("、");
  });

  it("setLocale 切到英文后取英文", () => {
    setLocale("en-us");
    expect(L("调用图", "Call Graph")).toBe("Call Graph");
    expect(listSep()).toBe(", ");
  });

  it("模块加载时按 CODEATLAS_LOCALE 定语言（模块级常量依赖这一点）", async () => {
    vi.stubEnv("CODEATLAS_LOCALE", "en");
    vi.resetModules();
    const fresh = await import("../webview/l10n");
    expect(fresh.locale()).toBe("en");
    vi.unstubAllEnvs();
    vi.resetModules();
  });
});

describe("服务端语言随界面", () => {
  afterEach(() => { vi.unstubAllEnvs(); vi.resetModules(); });

  it("中文界面给服务传 CODEATLAS_LOCALE=zh-cn", async () => {
    const { buildServeLaunch, LocaleEnvVar } = await import("../src/serverManager");
    expect(buildServeLaunch("codeatlas", "D:/repo", "tok", 1, null, undefined, {}).env[LocaleEnvVar]).toBe("zh-cn");
  });

  it("英文界面传 en", async () => {
    vi.stubEnv("CODEATLAS_LOCALE", "en-us");
    vi.resetModules();
    const { buildServeLaunch, LocaleEnvVar } = await import("../src/serverManager");
    expect(buildServeLaunch("codeatlas", "D:/repo", "tok", 1, null, undefined, {}).env[LocaleEnvVar]).toBe("en");
  });
});

describe("package.nls 清单文案", () => {
  const manifest = readFileSync(path.join(extensionDir, "package.json"), "utf8");
  const en = readJson("package.nls.json");
  const zh = readJson("package.nls.zh-cn.json");
  const placeholders = [...manifest.matchAll(/"%([^%"]+)%"/g)].map((m) => m[1]!);

  it("package.json 里不再有中文，全部走占位符", () => {
    expect(manifest).not.toMatch(/[一-鿿]/);
    expect(placeholders.length).toBeGreaterThan(50);
  });

  it("每个占位符在中英两份里都有，且两份 key 一致", () => {
    for (const key of placeholders) {
      expect(typeof en[key], key).toBe("string");
      expect(typeof zh[key], key).toBe("string");
    }
    expect(Object.keys(en).sort()).toEqual(Object.keys(zh).sort());
    expect(new Set(placeholders)).toEqual(new Set(Object.keys(en)));
  });

  it("英文版没有残留中文，中文版保持中文", () => {
    for (const [key, value] of Object.entries(en)) expect(value, key).not.toMatch(/[一-鿿]/);
    expect(zh["command.showSymbol"]).toBe("在调用图中查看");
    expect(en["command.showSymbol"]).toBe("Show in Call Graph");
  });
});
