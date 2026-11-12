import { defineConfig } from "vitest/config";
// 现有测试按中文文案断言；英文用例自己 setLocale("en")。
export default defineConfig({ test: { include: ["test/**/*.test.ts"], environment: "node", env: { CODEATLAS_LOCALE: "zh-cn" } } });
