# 更新日志 / Changelog

每个版本写清楚两件事：**改了什么**，以及**升级后需要你做什么**（没写就是不用做）。
Each release lists **what changed** and **what you need to do after upgrading** (nothing, if not mentioned).

## 0.2.0 — 2026-11-12

### 新功能

- **界面语言跟随 VS Code**：显示语言是中文（简体或繁体）时界面显示中文，其他语言一律显示英文。
  - 覆盖范围：
    - 命令、设置说明、侧边栏的调用大纲和影响范围、CodeLens；
    - 调用图、模块关系、项目地图、框架流程、入口流程；
    - AI 叙述。
  - 切换语言：命令面板运行 **Configure Display Language**（配置显示语言），选好后重启 VS Code。
- **⇢ 路径到…**：调用图里求从当前焦点到另一个符号的调用路径（含分派边），最短的在前。
- **导出图片**：调用图、模块关系、容器图、命名空间、入口流程、框架流程可导出 PNG / SVG。
- **⇪ 分享**：把浏览路径（调用图、模块关系、调用路径的步骤）复制成链接，同事打开同一个仓库后点链接即可重放。
- **编辑器之外的改动也会索引**：AI 助手直接写盘、`git pull`、切分支后由磁盘监听防抖后增量索引（`codeatlas.indexOnExternalChange`）；服务启动时与磁盘对账；视图涉及的文件在索引后改过时提示结果可能过期。

### 升级后需要做什么

- **不做任何操作也能用。** 已有的索引继续有效。下一次索引时（保存文件，或运行“索引工作区（增量）”），项目地图和框架流程会按当前语言重新生成。
- **想让所有文字马上换成当前语言**：运行一次 **CodeTrail Graph: 索引工作区（全量重建）**。少数写进索引的文字只在全量重建时更新，比如 C# 顶级语句的签名“顶级语句（Main）”。
- **AI 叙述按语言分开保存。** 切到英文后第一次打开叙述卡，会重新生成一份英文叙述（本地模型较慢，请耐心等待）。原来的中文叙述不受影响，切回中文后直接显示。
- **`architecture.yaml` 不用改。** 节点 key 不随语言变化，里面的声明照常生效。

### 命令行

- 输出语言默认跟随系统语言。
- 可以用 `--lang zh` / `--lang en` 指定，也可以设置环境变量 `CODEATLAS_LOCALE`。

### 仍然是中文的部分

- 输出面板“CodeTrail Graph”里的日志。
- 你的代码里本来就有的中文（注释、字符串等）会原样显示。

### English

- **New: the UI follows the VS Code display language.**
  - Chinese (Simplified or Traditional) shows Chinese; every other language shows English.
  - Covers commands, settings, the Call Outline and Impact views, CodeLens, the Call Graph, Module Relations, Project Map, Framework Flow, Entry Flow, and AI narrations.
  - To switch: run **Configure Display Language** from the Command Palette, then restart VS Code.
- **⇢ Path to…**: find the call paths from the current focus to another symbol in the Call Graph (dispatch edges included, shortest first).
- **Image export**: export the Call Graph, Module Relations, Containers, Namespaces, Entry Flow and Framework Flow as PNG / SVG.
- **⇪ Share**: copy the current trail (Call Graph, Module Relations and call-path steps) as a link; a colleague who opens the same repository and clicks it replays the same trail.
- **Edits made outside the editor are indexed too**: after an AI assistant writes files directly, `git pull` or a branch switch, the disk watcher re-indexes incrementally (`codeatlas.indexOnExternalChange`). The server also reconciles with disk at startup, and views warn when a file they show has changed since indexing.
- **After upgrading:**
  - Nothing is required. Your existing index keeps working. The next index run (saving a file, or *Index Workspace (Incremental)*) regenerates the project map and framework flows in the current language.
  - To switch every text at once, run **CodeTrail Graph: Index Workspace (Full Rebuild)**. A few texts stored in the index, such as the C# top-level statements signature, only update on a full rebuild.
  - AI narrations are cached per language. The first time you open a narration card in English, an English narration is generated (slow with local models). Your Chinese narrations are kept and show again when you switch back.
  - `architecture.yaml` needs no changes: node keys do not depend on the language.
- **CLI:** output follows the system language. Override it with `--lang zh|en` or the `CODEATLAS_LOCALE` environment variable.
- **Still in Chinese:** logs in the "CodeTrail Graph" output channel. Chinese text from your own code is shown as is.

## 0.1.1

- 调用图默认使用时序图，按调用顺序、按项目分列，可以切回网状图。新增框架流程，以及跨视图的后退 / 前进。
- 索引格式升级到 **12**，旧索引会自动全量重建。原始引用持久化并按事务提交；增量索引时会重新绑定未修改的调用方，解析失败也不会留下不完整的索引。
- C# 有待更新的文件时自动补算 Roslyn。保存触发的索引和手动索引统一排队，全量重建优先。
- 搜索、焦点、展开、DSM 等视图会丢弃过期的异步响应，索引完成后刷新当前视图。后处理按输入指纹和 Git ref 复用上次结果。
- 恢复已发布的品牌名和图标，保留 SQLite / Roslyn 的安全依赖更新。

**升级后需要做什么：** 不用操作，旧索引会自动全量重建。

### English

- The Call Graph defaults to a sequence view laid out by call order and project; the network view is still available. Added Framework Flow and back / forward navigation across views.
- Index format **12**: old indexes are rebuilt automatically. Raw references are persisted and committed transactionally; incremental runs rebind unchanged callers, and a failed parse never leaves a partial index.
- Roslyn runs automatically for pending C# files. Save-triggered and manual index runs share one queue, and full rebuilds take priority.
- Views discard stale async responses and refresh after indexing. Post-processing reuses previous results based on input fingerprints and Git refs.
- Restored the published brand and icon; kept the SQLite / Roslyn security updates.

**After upgrading:** nothing to do; old indexes are rebuilt automatically.
