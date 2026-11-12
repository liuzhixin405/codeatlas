import { readFileSync } from "node:fs";
import * as path from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ChangeBatcher, ExcludedDirectories, isIndexablePath, SaveEchoWindowMs } from "../src/fileWatch";
import { IndexScheduler } from "../src/indexScheduler";

const Supported = new Set([".ts", ".tsx", ".js", ".cs", ".go", ".py", ".java"]);
const root = path.resolve("/repo");
const other = path.resolve("/other-root");
const at = (base: string, ...parts: string[]) => path.join(base, ...parts);

describe("ExcludedDirectories", () => {
  it("与 C# 的 IndexOptions.ExcludedDirectories 一致（扫描器跳过的目录，监听也跳过）", () => {
    const cs = readFileSync(path.join(__dirname, "../../src/CodeAtlas.Indexing.TreeSitter/IndexOptions.cs"), "utf8");
    const block = /ExcludedDirectories\s*\{[^}]*\}\s*=([\s\S]*?\]);/.exec(cs)?.[1] ?? "";
    const names = [...block.matchAll(/"([^"]+)"/g)].map((m) => m[1]);
    expect(names.length).toBeGreaterThan(10);
    expect([...ExcludedDirectories]).toEqual(names);
  });
});

describe("isIndexablePath", () => {
  it("受支持扩展名、在根之下、不在跳过目录里的才算", () => {
    expect(isIndexablePath(at(root, "src", "a.ts"), "change", [root], Supported)).toBe(true);
    expect(isIndexablePath(at(root, "src", "A.CS"), "create", [root], Supported)).toBe(true);           // 扩展名大小写不敏感
    expect(isIndexablePath(at(root, "README.md"), "change", [root], Supported)).toBe(false);            // 不受支持
    expect(isIndexablePath(at(root, "node_modules", "x", "i.js"), "create", [root], Supported)).toBe(false);
    expect(isIndexablePath(at(root, "src", "Bin", "a.cs"), "change", [root], Supported)).toBe(false);   // 跳过目录大小写不敏感
    expect(isIndexablePath(at(root, ".codeatlas", "x.ts"), "change", [root], Supported)).toBe(false);
    expect(isIndexablePath(path.resolve("/elsewhere/a.ts"), "change", [root], Supported)).toBe(false);  // 不在任何根下
  });

  it("按相对根的路径判断：根自己的上级目录叫 build 不算", () => {
    const nested = path.resolve("/build/repo");
    expect(isIndexablePath(at(nested, "src", "a.ts"), "change", [nested], Supported)).toBe(true);
  });

  it("外部根下的文件也算", () => {
    expect(isIndexablePath(at(other, "api", "h.go"), "change", [root, other], Supported)).toBe(true);
  });

  it("删除事件放过没有扩展名的路径（删目录），但删跳过目录本身不算", () => {
    expect(isIndexablePath(at(root, "src", "feature"), "delete", [root], Supported)).toBe(true);
    expect(isIndexablePath(at(root, "src", "feature"), "change", [root], Supported)).toBe(false);
    expect(isIndexablePath(at(root, "node_modules"), "delete", [root], Supported)).toBe(false);
    expect(isIndexablePath(at(root, "web", "dist"), "delete", [root], Supported)).toBe(false);
  });
});

describe("ChangeBatcher", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  const make = (delay = 2000, maxWait?: number) => {
    const calls: { root: string; paths: string[] }[] = [];
    const b = new ChangeBatcher((r, paths) => calls.push({ root: r, paths }), delay, maxWait, () => Date.now(), (f) => f);
    return { b, calls };
  };

  it("一串事件在安静 delay 之后合并成一次，路径去重并排序", async () => {
    const { b, calls } = make(2000);
    b.add(root, "/repo/b.ts");
    await vi.advanceTimersByTimeAsync(1500);
    b.add(root, "/repo/a.ts");
    b.add(root, "/repo/b.ts");
    await vi.advanceTimersByTimeAsync(1999);
    expect(calls).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(1);
    expect(calls).toEqual([{ root, paths: ["/repo/a.ts", "/repo/b.ts"] }]);
  });

  it("切分支那种几千个事件只交一次", async () => {
    const { b, calls } = make(2000);
    for (let i = 0; i < 3000; i++) b.add(root, `/repo/f${String(i).padStart(4, "0")}.ts`);
    await vi.advanceTimersByTimeAsync(2000);
    expect(calls).toHaveLength(1);
    expect(calls[0]!.paths).toHaveLength(3000);
  });

  it("持续不断的事件流最多攒 maxWait 也要交一次", async () => {
    const { b, calls } = make(2000, 5000);
    for (let t = 0; t < 7000; t += 500) { b.add(root, `/repo/t${t}.ts`); await vi.advanceTimersByTimeAsync(500); }
    expect(calls).toHaveLength(1);
    expect(calls[0]!.paths).toHaveLength(10); // 0、500…4500 共 10 个事件，在第 5000ms（第一个事件后 maxWait）交出
  });

  it("每个根各自一批", async () => {
    const { b, calls } = make(100);
    b.add(root, "/repo/a.ts");
    b.add(other, "/other-root/b.ts");
    await vi.advanceTimersByTimeAsync(100);
    expect(calls).toEqual([{ root, paths: ["/repo/a.ts"] }, { root: other, paths: ["/other-root/b.ts"] }]);
  });

  it("编辑器保存的文件归 indexOnSave：事件在保存之后或之前到都剔除；剔完为空不回调", async () => {
    const { b, calls } = make(2000);
    b.markSaved("/repo/saved.ts");
    b.add(root, "/repo/saved.ts");        // 保存后磁盘事件
    b.add(root, "/repo/late.ts");         // 磁盘事件先到……
    b.markSaved("/repo/late.ts");         // ……保存事件后到
    b.add(root, "/repo/external.ts");     // AI 助手直接写的
    await vi.advanceTimersByTimeAsync(2000);
    expect(calls).toEqual([{ root, paths: ["/repo/external.ts"] }]);

    b.markSaved("/repo/only.ts");
    b.add(root, "/repo/only.ts");
    await vi.advanceTimersByTimeAsync(2000);
    expect(calls).toHaveLength(1);
  });

  it("保存很久以后同一文件又被外部改了：照常触发", async () => {
    const { b, calls } = make(100);
    b.markSaved("/repo/a.ts");
    await vi.advanceTimersByTimeAsync(SaveEchoWindowMs + 1);
    b.add(root, "/repo/a.ts");
    await vi.advanceTimersByTimeAsync(100);
    expect(calls).toEqual([{ root, paths: ["/repo/a.ts"] }]);
  });

  it("交给 IndexScheduler：索引进行中又来一批只置脏，结束后合并成一次再跑", async () => {
    let release!: () => void;
    let runs = 0;
    const s = new IndexScheduler(() => { runs++; return new Promise<void>((r) => { release = r; }); }, 1500);
    const b = new ChangeBatcher(() => void s.runNow(false), 2000, undefined, () => Date.now(), (f) => f);
    b.add(root, "/repo/a.ts");
    await vi.advanceTimersByTimeAsync(2000);
    expect(runs).toBe(1);
    for (let i = 0; i < 500; i++) b.add(root, `/repo/x${i}.ts`);
    await vi.advanceTimersByTimeAsync(2000);
    b.add(root, "/repo/y.ts");
    await vi.advanceTimersByTimeAsync(2000);
    expect(runs).toBe(1);   // 还在跑：两批都只置脏
    release();
    await vi.advanceTimersByTimeAsync(0);
    expect(runs).toBe(2);   // 结束后只补跑一次
    release();
    await vi.advanceTimersByTimeAsync(0);
    expect(runs).toBe(2);
  });
});
