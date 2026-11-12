// 界面文案跟随 VS Code 显示语言：zh*（zh-cn / zh-tw …）显示中文，其他一律英文。
// 宿主与 webview 共用这一份。语言在模块加载时就要定好——不少文案是模块级常量，加载时求值：
// · 宿主：extension.ts 的第一个 import 是 ./localeInit，它在其余模块求值前按 vscode.env.language 调 setLocale；
// · webview：panel.ts 把 vscode.env.language 写进 <html lang>，脚本加载时读到；
// · 测试 / 浏览器调试：环境变量 CODEATLAS_LOCALE（vitest.config 固定为 zh-cn）或 ?lang=en。

export type Locale = "zh" | "en";

export function normalizeLocale(lang: string | undefined | null): Locale {
  return /^zh/i.test(lang ?? "") ? "zh" : "en";
}

function detect(): string | undefined {
  const env = typeof process !== "undefined" ? process.env?.CODEATLAS_LOCALE : undefined;
  if (env) return env;
  if (typeof document !== "undefined") {
    const q = typeof location !== "undefined" ? new URLSearchParams(location.search).get("lang") : null;
    return q ?? document.documentElement?.lang ?? undefined;
  }
  return undefined;
}

let current: Locale = normalizeLocale(detect());

export function setLocale(lang: string | undefined | null): void {
  current = normalizeLocale(lang);
}

export function locale(): Locale {
  return current;
}

/** 按当前语言取一条文案：L("中文", "English")。两边都写全，带插值时用模板字符串。 */
export function L(zh: string, en: string): string {
  return current === "zh" ? zh : en;
}

/** 列表连接符：中文顿号，英文逗号。 */
export function listSep(): string {
  return current === "zh" ? "、" : ", ";
}
