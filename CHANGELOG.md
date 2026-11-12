# 更新日志 / Changelog

本文件记录整个项目的版本变化：命令行、本地服务与 VS Code 扩展。
Marketplace 扩展页「Changelog」标签显示的是 [extension/CHANGELOG.md](extension/CHANGELOG.md)，那里只写用户能感知的扩展变化；本文件额外包含命令行与后端。

每个版本写清楚两件事：**改了什么**，以及**升级后需要你做什么**（没写就是不用做）。

---

## 0.2.0 — 2026-11-12

### 新增

- **界面语言跟随 VS Code**：显示语言是中文（简体或繁体）时界面显示中文，其他语言一律英文。覆盖命令、设置说明、调用大纲、影响范围、CodeLens、调用图、模块关系、项目地图、框架流程、入口流程与 AI 叙述。
- **命令行语言**：默认跟随系统语言，可用 `--lang zh|en` 或环境变量 `CODEATLAS_LOCALE` 指定。
- **`codeatlas mcp <repo> [--db <path>]`**：在 stdio 上提供 MCP server（JSON-RPC 2.0，一行一条消息），让 Claude Code 等助手直接查索引。工具：`codeatlas_explore`（主工具，一次返回源码、调用路径、各自调出去的调用与影响摘要）、`codeatlas_search`、`codeatlas_callers`、`codeatlas_callees`、`codeatlas_impact`、`codeatlas_status`。
- **`codeatlas affected <repo>`**：把 diff 的改动行映射到最内层符号，再沿影响范围算出受影响的入口与测试。支持 `--base`、`--stdin`（统一 diff 或文件名列表）、`--depth`、`--filter`、`--json`、`--quiet`。
- **`codeatlas query <repo> <A> --to <B>`**：列出 A 到 B 的调用路径（默认最多 8 步、10 条，可用 `--depth` / `--max-paths` / `--json`）。最短的在前，分派边与低置信度边用 `⇢` 标出。
- **`codeatlas arch <repo> --cycles files`**：文件级 import 环检查，有环退出码 1、没环 0；`--edges all` 额外算启发式包级边，`--json` 输出每个环的文件与最短环路径。
- **扩展**：编辑器之外的改动（AI 助手直接写盘、`git pull`、切分支）由磁盘监听防抖后增量索引（`codeatlas.indexOnExternalChange`）；服务启动时与磁盘对账；视图涉及的文件在索引后改过时提示结果可能过期；新增「⇢ 路径到…」调用路径视图；「⇪ 分享」把浏览路径复制成链接（trail URI），同事打开同一仓库点链接即重放；调用图、模块关系、容器图、命名空间、入口流程、框架流程可导出 PNG / SVG。

### 变更

- **索引格式 13**：新增 TS / JS 事件频道合成边——`x.emit('频道')` 所在的函数 → 同名频道 `x.on('频道', handler)` 注册的处理函数，via = `event:频道`，confidence 0.4。旧索引会自动全量重建。

### 修复

- 并行扫描时规则实例互相污染：`ArchitectureScanner.DefaultRules` 改为每次扫描新建一套规则实例，gRPC 服务名匹配与前端 HTTP 规则的状态和缓存不再跨扫描共享（此前会导致多出或漏掉边）。

### 升级后需要做什么

- **不用操作也能用**：旧索引在服务启动时自动全量重建（首次升级可能较慢）。
- 想让地图与框架流程的文字立刻换成当前语言：运行一次「索引工作区（全量重建）」。少数写进索引的文字只在这时更新，例如 C# 顶级语句的签名。
- AI 叙述按语言分开缓存：切到英文后第一次打开叙述卡会重新生成，中文叙述保留。
- `architecture.yaml` 不用改：节点 key 不随语言变化。

### English

- The UI now follows the VS Code display language (Chinese for zh-cn / zh-tw, English otherwise), including commands, settings, Call Outline, Impact, CodeLens, Call Graph, Module Relations, Project Map, Framework Flow, Entry Flow and AI narrations. The CLI follows the system language; override with `--lang zh|en` or `CODEATLAS_LOCALE`.
- New commands: `codeatlas mcp` (stdio MCP server with `codeatlas_explore`, `_search`, `_callers`, `_callees`, `_impact`, `_status`), `codeatlas affected` (diff → innermost symbols → affected entries and tests), `codeatlas query … --to …` (call paths), `codeatlas arch --cycles files` (file-level import cycles, exit code 1 when a cycle exists).
- Extension: disk-watch indexing for edits made outside the editor, stale-result notice, startup reconcile with disk, trail URI handling, image export for the call graph, path view.
- **Index format 13**: synthesized TS / JS event-channel edges (`emit('channel')` → `on('channel', handler)`, via `event:channel`, confidence 0.4). Old indexes are rebuilt automatically.
- Fix: parallel scans no longer share stateful discovery-rule instances.
- **After upgrading:** nothing is required; old indexes rebuild automatically. Run a full rebuild to switch every stored text to the current language. AI narrations are cached per language.

## 0.1.1 — 2026-10-16

### 变更

- 调用图默认使用时序图：按调用顺序从上到下、按项目分列，可以切回网状图。
- 新增框架流程视图，以及跨视图的后退 / 前进。
- 索引格式升级到 **12**：原始引用持久化并按事务提交；增量索引会重新绑定未修改的调用方，解析失败不会留下不完整的索引。
- C# 有待更新文件时自动补算 Roslyn；保存触发的索引与手动索引统一排队，全量重建优先。
- 搜索、焦点、展开、DSM 等视图丢弃过期的异步响应，索引完成后刷新当前视图；后处理按输入指纹和 Git ref 复用上次结果。
- 恢复已发布的品牌名与图标（显示名 CodeTrail Graph，扩展 ID `164910441.codetrail-graph`），保留 SQLite / Roslyn 的安全依赖更新（Microsoft.Data.Sqlite 10.0.13，Pkcs / Xml 10.0.13）。

### 升级后需要做什么

- 不用操作，旧索引会自动全量重建。

### English

- The Call Graph defaults to a sequence layout; Framework Flow and back / forward navigation were added.
- Index format **12**: raw references are persisted transactionally, incremental runs rebind unchanged callers.
- Roslyn runs automatically for pending C# files; save-triggered and manual index runs share one priority queue.
- Views discard stale async responses; post-processing is reused by input fingerprint and Git ref.
- Restored the published brand and icon; kept the SQLite / Roslyn security dependency updates.
- **After upgrading:** nothing to do; old indexes rebuild automatically.

## 0.1.0 — 2026-10-15

首个可安装版本（当时品牌为 CodeAtlas，publisher 仍为 `victor-liu`；0.1.1 起改为 CodeTrail Graph / `164910441`）。

### 新增

- tree-sitter 索引：TypeScript / TSX / JavaScript / C# / Go / Python / Java 的符号、调用、`new`、继承、接口实现与 import / using 关系，每条边带 confidence。
- 命令行：`index`（增量 / 全量）、`query`、`search`、`arch`、`serve`。
- 本地 API 服务与面板：调用图（时序 / 网状）、调用大纲、影响范围、模块关系、CodeLens、项目地图。
- C# Roslyn 精确语义（独立 worker，需要本机 .NET 10 SDK）。

### English

- First installable release (branded CodeAtlas, publisher `victor-liu`; renamed to CodeTrail Graph / `164910441` in 0.1.1).
- tree-sitter indexing for TS / TSX / JS / C# / Go / Python / Java with per-edge confidence, CLI (`index` / `query` / `search` / `arch` / `serve`), a local API server with the call graph, outline, impact, module relations, CodeLens and project map, plus optional Roslyn precision for C#.

---

更完整的验收数据、已知问题与各里程碑记录见 [docs/acceptance.md](docs/acceptance.md)。
