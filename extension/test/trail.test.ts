import { describe, expect, it } from "vitest";
import { NavHistory } from "../webview/navHistory";
import { isTrailStep, MaxTrailSteps, searchNameFor, type TrailStep } from "../webview/trail";
import { clampTrail, decodeTrail, encodeTrail, parseTrailUri, pickTrailFolder, trailUri } from "../src/trailUri";

const call = (q: string, v: "seq" | "graph" = "seq"): TrailStep => ({ k: "call", v, q });

describe("NavHistory.trail：时间线里可分享的步骤", () => {
  it("只收带 share 的条目，at 指向当前条目（或它之前最近的可分享条目）", async () => {
    const h = new NavHistory();
    const noop = (): void => {};
    h.record({ key: "call:seq:1", label: "A", restore: noop, share: call("Acme.A.Run()") });
    h.record({ key: "map:arch", label: "地图", restore: noop });
    h.record({ key: "rel:2", label: "B", restore: noop, share: { k: "rel", q: "Acme.B" } });
    h.record({ key: "path:1:3", label: "P", restore: noop, share: { k: "path", f: "Acme.A.Run()", t: "Acme.C.Save()" } });
    expect(h.trail()).toEqual({ steps: [call("Acme.A.Run()"), { k: "rel", q: "Acme.B" }, { k: "path", f: "Acme.A.Run()", t: "Acme.C.Save()" }], at: 2 });
    await h.back();
    await h.back(); // 停在地图那条：取它之前最近的可分享条目
    expect(h.trail().at).toBe(0);
    expect(new NavHistory().trail()).toEqual({ steps: [], at: -1 });
  });
});

describe("trail 链接", () => {
  const trail = { steps: [call("Acme.Orders.OrderService.Place(Order)"), { k: "rel", q: "src/app.ts:Svc" } as TrailStep, call("订单.处理", "graph")], at: 1 };

  it("编码进 vscode://<扩展 id>/trail?repo=&d=，解回来一模一样", () => {
    const uri = trailUri("vscode", "164910441.codetrail-graph", "shop api", trail);
    expect(uri.startsWith("vscode://164910441.codetrail-graph/trail?repo=shop%20api&d=")).toBe(true);
    const u = new URL(uri);
    expect(parseTrailUri({ path: u.pathname, query: u.search.slice(1) })).toEqual({ repo: "shop api", trail });
    expect(encodeTrail(trail)).toMatch(/^[A-Za-z0-9_-]+$/); // base64url，不用再转义
  });

  it("不可信输入：路径不对、乱码、版本不对、步骤字段缺失都拒收", () => {
    const d = encodeTrail(trail);
    expect(parseTrailUri({ path: "/other", query: `d=${d}` })).toBeNull();
    expect(decodeTrail("not base64!")).toBeNull();
    expect(decodeTrail(Buffer.from(JSON.stringify({ v: 2, s: trail.steps, a: 0 })).toString("base64url"))).toBeNull();
    expect(decodeTrail(Buffer.from(JSON.stringify({ v: 1, s: [{ k: "call", v: "seq" }], a: 0 })).toString("base64url"))).toBeNull();
    expect(decodeTrail(Buffer.from(JSON.stringify({ v: 1, s: [], a: 0 })).toString("base64url"))).toBeNull();
    // at 越界夹回范围
    expect(decodeTrail(Buffer.from(JSON.stringify({ v: 1, s: [call("A")], a: 9 })).toString("base64url"))).toEqual({ steps: [call("A")], at: 0 });
    expect(isTrailStep({ k: "path", f: "A", t: "" })).toBe(false);
    expect(isTrailStep({ k: "call", v: "tree", q: "A" })).toBe(false);
  });

  it("超过上限只留最近的步骤，at 跟着平移", () => {
    const many = Array.from({ length: MaxTrailSteps + 5 }, (_, i) => call(`S${i}`));
    const c = clampTrail({ steps: many, at: MaxTrailSteps + 3 });
    expect(c.steps.length).toBe(MaxTrailSteps);
    expect(c.steps[0]).toEqual(call("S5"));
    expect(c.at).toBe(MaxTrailSteps - 2);
  });

  it("按目录名找仓库（不分大小写）；只开了一个文件夹就用它；否则找不到", () => {
    const folders = [{ name: "shop-web" }, { name: "Shop-API" }];
    expect(pickTrailFolder(folders, "shop-api")).toBe(folders[1]);
    expect(pickTrailFolder(folders, "other")).toBeNull();
    expect(pickTrailFolder([{ name: "x" }], "other")).toEqual({ name: "x" });
  });
});

describe("searchNameFor：按限定名回搜用的短名", () => {
  it("去掉参数表、脚本路径前缀、命名空间", () => {
    expect(searchNameFor("Acme.Orders.OrderService.Place(Order, int)")).toBe("Place");
    expect(searchNameFor("src/app.ts:Svc.find")).toBe("find");
    expect(searchNameFor("web/src/components/LoginPage/index.tsx:LoginPage")).toBe("LoginPage");
    expect(searchNameFor("external:Npgsql.NpgsqlConnection.Open")).toBe("Open");
    expect(searchNameFor("src/util/format.ts")).toBe("format.ts");
    expect(searchNameFor("github.com/acme/svc.Handler")).toBe("Handler");
  });
});
