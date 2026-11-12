import { describe, expect, it } from "vitest";
import { viaStyle } from "../webview/flowLayout";
import { edgeLabel } from "../webview/graphState";
import type { FlowNodeDto, NeighborDto } from "../webview/types";

describe("合成边（事件频道配对）的显示", () => {
  it("时序图：via=event:x 的调用行单独一种样式，标签旁写「事件 x」", () => {
    const node = { symbolId: 2, label: "sendMail", qualifiedName: "src/listeners.ts:sendMail", kind: "call", actor: "a", via: "event:order:placed", confidence: 0.4, count: 1, depth: 1, children: [], hidden: 0, note: null } as FlowNodeDto;
    expect(viaStyle(node)).toEqual({ cls: "via-event", tag: "事件 order:placed" });
    expect(viaStyle({ ...node, via: "callback" })).toEqual({ cls: "via-render", tag: "回调" });
  });

  it("网状图：边上写「事件 x」", () => {
    expect(edgeLabel({ kind: "calls", via: "event:order:placed" } as NeighborDto)).toBe("事件 order:placed");
  });
});
