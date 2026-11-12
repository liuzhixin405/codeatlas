import { L } from "./l10n";
import { entryItemHtml, filterEntries } from "./flowView";
import {
  controllerEntries, ControllersLabel, entriesLabel, filterHandlerGroups, frameworkSummary, groupByOwner, handlerKey, handlerTitle, handlerTotal, messageKindLabel,
  type PlaceholderKind,
} from "./frameworkModel";
import { esc } from "./seqDiagram";
import type { ArchNodeDto, EntryDto, FrameworkFlowDto, FrameworkHandlerDto, FrameworkHandlerGroupDto } from "./types";

/**
 * 框架流程视图的 HTML 片段（纯字符串，不碰 DOM，便于在 node 里测）。FrameworkView 只负责塞进页面、按 data-* 分派点击。
 */

/** 标题行：「框架流程」标签、容器下拉（当前容器不在下拉里时补在最前）、各阶段个数；下面是 notes 灰字列表。 */
export function frameworkHeaderHtml(dto: Pick<FrameworkFlowDto, "containerKey" | "containerName" | "stages" | "handlers" | "notes">, containers: readonly Pick<ArchNodeDto, "key" | "name">[]): string {
  const options = containers.some((c) => c.key === dto.containerKey) ? containers : [{ key: dto.containerKey, name: dto.containerName }, ...containers];
  const select = `<select class="fw-container" title="${L("看哪个容器的框架流程", "Container whose framework flow to show")}">` +
    options.map((c) => `<option value="${esc(c.key)}"${c.key === dto.containerKey ? " selected" : ""}>${esc(c.name)}</option>`).join("") + `</select>`;
  const notes = dto.notes.length > 0 ? `<ul class="fw-notes">${dto.notes.map((n) => `<li>${esc(n)}</li>`).join("")}</ul>` : "";
  return `<div class="flow-title"><span class="kind">${L("框架流程", "Framework Flow")}</span>${select}<span class="muted">${esc(frameworkSummary(dto.stages, dto.handlers))}</span></div>${notes}`;
}

export const FrameworkHint = L("每个请求都经过的框架骨架，和某一个请求的实际调用（入口流程）不同。点「N 个 … ▸」看清单、点一项打开它的入口流程；单击行看详情，双击行在调用图里以它为焦点，点 ▸ 展开关键调用", "The framework skeleton every request passes through, as opposed to the actual calls of one request (Entry Flow). Click \"N … ▸\" to see the list and click an item to open its Entry Flow; click a row for details, double-click a row to focus it in the Call Graph, click ▸ to expand key calls");

/** 清单面板的标题。controllers 的入口还没取到时 entries 传 null。 */
export function panelTitle(kind: PlaceholderKind, dto: FrameworkFlowDto, entries: readonly EntryDto[] | null): string {
  if (kind === "handlers") return `Handler · ${handlerTotal(dto.handlers)}`;
  const label = entriesLabel(dto);
  const name = label === "控制器" || label === ControllersLabel ? L("控制器入口", "Controller entries") : label;
  return entries === null ? name : `${name} · ${controllerEntries(entries).length}`;
}

export function panelPlaceholder(kind: PlaceholderKind, entriesName = L("控制器入口", "Controller entries")): string {
  return kind === "handlers" ? L("筛选 Handler（消息类型 / 模块 / 命令 查询 事件）", "Filter handlers (message type / module / command query event)") : L(`筛选${entriesName}（路由 / 名字）`, `Filter ${entriesName} (route / name)`);
}

/** Handler 清单里一个 Handler 的入口行的 data 键。 */
export function handlerEntryKey(h: FrameworkHandlerDto, i: number): string {
  return `${handlerKey(h)}#${i}`;
}

/**
 * Handler 清单：按模块分组（筛选后为空的组不出），每项「种类徽标 · 消息类型 · 入口 N」；
 * 有多个入口且展开着的 Handler 下面列出它的入口让用户选。点击按 data-h / data-he 分派。
 */
export function handlerListHtml(groups: readonly FrameworkHandlerGroupDto[], filter: string, isExpanded: (key: string) => boolean): string {
  const total = groups.reduce((n, g) => n + g.items.length, 0);
  const shown = filterHandlerGroups(groups, filter);
  if (shown.length === 0) return `<p class="flow-empty">${total === 0 ? L("没有发现 Handler", "No handlers found") : L("没有匹配的 Handler", "No matching handlers")}</p>`;
  return shown.map((g) =>
    `<div class="flow-group">${esc(g.module || L("（未分模块）", "(no module)"))} · ${g.items.length}</div>` + g.items.map((h) => handlerItemHtml(h, isExpanded(handlerKey(h)))).join("")).join("");
}

function handlerItemHtml(h: FrameworkHandlerDto, open: boolean): string {
  const kindText = messageKindLabel(h.messageKind);
  const n = h.entries.length;
  const arrow = n > 1 ? (open ? " ▾" : " ▸") : "";
  const count = n === 0 ? L("无入口", "No entries") : L(`入口 ${n}${arrow}`, `${n} ${n === 1 ? "entry" : "entries"}${arrow}`);
  const title = `${h.handler.qualifiedName}\n${h.handler.path}:${h.handler.startLine}` + (n === 0 ? L("\n没有入口分派到它：点了在调用图里看它", "\nNo entry dispatches to it: click to view it in the Call Graph") : n === 1 ? L("\n点了打开它的入口流程", "\nClick to open its Entry Flow") : L("\n点了列出会分派到它的入口", "\nClick to list the entries that dispatch to it"));
  const item = `<div class="flow-entry fw-handler-item${open ? " open" : ""}" data-h="${esc(handlerKey(h))}" title="${esc(title)}">` +
    (kindText ? `<span class="fw-mk ${esc((h.messageKind ?? "").toLowerCase())}">${esc(kindText)}</span>` : "") +
    `<span class="route">${esc(handlerTitle(h))}</span><span class="name">${esc(count)}</span></div>`;
  if (!open || n < 2) return `<div class="fw-handler">${item}</div>`;
  const entries = h.entries.map((e, i) => entryItemHtml(e, false, { "data-he": handlerEntryKey(h, i) })).join("");
  return `<div class="fw-handler">${item}<div class="fw-handler-entries"><div class="muted fw-choose">${L(`${n} 个入口会分派到它，选一个打开入口流程：`, `${n} entries dispatch to it; pick one to open its Entry Flow:`)}</div>${entries}</div></div>`;
}

/** 控制器清单：容器的 HTTP 入口，按所在类型（控制器 / Minimal API 静态类）分组；每项按入口清单的样子画，data-ci 是在 entries 里的下标。 */
export function controllerListHtml(entries: readonly EntryDto[] | null, filter: string): string {
  if (entries === null) return `<p class="flow-empty">${L("正在取入口…", "Loading entries…")}</p>`;
  const all = controllerEntries(entries);
  const shown = filterEntries(all, filter);
  if (shown.length === 0) return `<p class="flow-empty">${all.length === 0 ? L("这个容器里没有发现入口", "No entries found in this container") : L("没有匹配的入口", "No matching entries")}</p>`;
  return groupByOwner(shown).map((g) =>
    `<div class="flow-group" title="${esc(g.owner)}">${esc(g.label)} · ${g.entries.length}</div>` +
    g.entries.map((e) => entryItemHtml(e, false, { "data-ci": String(entries.indexOf(e)) })).join("")).join("");
}
