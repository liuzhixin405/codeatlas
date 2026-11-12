import { afterEach, describe, expect, it } from "vitest";
import { setLocale } from "../webview/l10n";
import { esc } from "../webview/seqDiagram";
import { staleBarHtml, staleNotice } from "../webview/staleNotice";

describe("staleNotice", () => {
  afterEach(() => setLocale("zh-cn"));

  it("没有过期文件（含旧服务不带字段）不提示", () => {
    expect(staleNotice(undefined)).toBeNull();
    expect(staleNotice(null)).toBeNull();
    expect(staleNotice([])).toBeNull();
    expect(staleBarHtml([], esc)).toBe("");
  });

  it("列出文件名，悬停给全路径", () => {
    expect(staleNotice(["src/app.ts", "src/svc.ts"])).toEqual({
      text: "以下文件在上次索引后已改动，结果可能过期：app.ts、svc.ts",
      title: "src/app.ts\nsrc/svc.ts",
    });
  });

  it("超过 3 个折成「等 N 个」", () => {
    expect(staleNotice(["a/1.ts", "a/2.ts", "a/3.ts", "a/4.ts", "a/5.ts"])!.text).toBe("以下文件在上次索引后已改动，结果可能过期：1.ts、2.ts、3.ts 等 5 个");
    setLocale("en");
    expect(staleNotice(["a/1.ts", "a/2.ts", "a/3.ts", "a/4.ts", "a/5.ts"])!.text).toBe("These files changed since the last index; results may be stale: 1.ts, 2.ts, 3.ts and 2 more");
  });

  it("提示条带「立即重建」按钮，文件名转义", () => {
    const html = staleBarHtml(["src/<x>.ts"], esc);
    expect(html).toContain('data-act="reindex"');
    expect(html).toContain(">立即重建</button>");
    expect(html).toContain("&lt;x&gt;.ts");
    expect(html).not.toContain("<x>");
  });
});
