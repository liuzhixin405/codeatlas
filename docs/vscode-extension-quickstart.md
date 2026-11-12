# CodeTrail Graph 用户使用说明（快速上手）

这份文档按**你要做的事**组织：装好之后每天怎么用、每个视图解决什么问题、卡住了怎么办。
完整的功能清单见扩展页 README（[extension/readme.md](../extension/readme.md)），逐项细节见 [完整使用指南](vscode-extension-guide.md)，版本变化见 [CHANGELOG.md](../CHANGELOG.md)。

适用版本：扩展 **0.2.0**，索引格式 **13**。

---

## 1. 五分钟上手

1. **装扩展**：市场搜 **CodeTrail Graph**，或运行
   `code --install-extension 164910441.codetrail-graph`。
   按系统装对应平台的包（Windows x64、Linux x64 / arm64、macOS x64 / arm64）；**不需要装 .NET**，服务自带运行时。
2. **完全退出并重新打开 VS Code**（不是重载窗口）。更新扩展时也一样，否则服务文件被占用会报 `EPERM`。
3. **打开一个仓库**（TS / JS / C# / Go / Python / Java 都行）。
4. **把光标放到方法名或类名上**，右键 → **CodeTrail Graph → 在调用图中查看**，或按 `Ctrl+Alt+G`（macOS `Cmd+Alt+G`）。
5. 首次会提示"这个工作区还没有 CodeTrail Graph 索引"，点 **建立索引**。四五千个文件的仓库约 40 秒；面板随后在右侧打开，并聚焦到你选中的符号。

就这样。之后每次保存文件都会自动增量索引，不用再手动点。

## 2. 我想知道……该点哪里

| 你的问题 | 怎么做 |
| --- | --- |
| 这个方法往下调了谁、按什么顺序 | 光标放上去 `Ctrl+Alt+G`，调用图默认是**时序图**：从上往下就是调用顺序，按项目分列 |
| 谁调用了这个方法 | 方法上方的 **CodeLens**（`ctg:` 开头）点「调用方」，或看侧边栏 **影响范围** |
| 改了它会不会影响线上接口 | 侧边栏 **影响范围**：逐层往上展开，最后列出走到的入口（如 `HTTP POST /api/orders`），测试调用方默认只计数 |
| 这个方法内部按源码顺序做了什么 | 侧边栏 **调用大纲**：逐层展开，标出循环内 / 条件内 / 回调里的调用 |
| 一个请求从进来到返回经过哪些框架环节 | 项目地图 → **框架流程**（中间件 → 控制器 → 分派器 → 行为链 → Handler），点某个 Handler 进 **入口流程** |
| 这个类跟哪些类耦合、谁反向依赖我 | 面板顶部切 **模块关系**，反向依赖标红；编辑器右键「在模块关系中查看」 |
| 项目整体有哪些服务、怎么互相调用 | 面板顶部切 **项目地图**，双击模块钻进去，双击类型回到调用图 |
| 从 A 能不能走到 B | 调用图工具栏 **⇢ 路径到…**（0.2.0）：点它再选终点 B；每步一行，`⇢` 表示分派边或低置信度边 |
| 这段改动要跑哪些测试 | 命令行 `codeatlas affected <repo>`（0.2.0），见第 6 节 |
| 想让 AI 助手自己查这个仓库 | `codeatlas mcp <repo>` 挂 MCP server（0.2.0），见第 7 节 |

## 3. 调用图怎么读、怎么操作

- **两种画法**：工具栏 **时序 | 网状**。时序图按调用顺序自上而下、按项目分列；网状图调用者在左、被调用者在右。面板会记住你的选择。
- **单击**一行看详情，**双击**把它设为新焦点，点 **▸** 展开 / 折叠。
- **默认只画它调了谁**：调用者收在焦点旁的 **被 N 处调用** 胶囊里，点一下才加入。
- **自动向下**（网状，默认开）：自动展开到叶子，每个节点挑最像主流程的分支；**↑ 到源头** 反过来追到没有调用者的入口。
- **线的含义**：橙色虚线 = 分发到 Handler（线上写 `new 消息类型`）；灰色点状虚框 = 外部库（不能展开）；线越粗 confidence 越高；紫色虚线 = 事件频道合成边（`emit('x')` → `on('x', handler)`，confidence 0.4，当"运行时大概会走到"看）。
- **工具栏开关**（网状）：类型关系、类型引用、测试代码、分发细节，默认都关。
- **搜索框**按名字前缀搜（空格分隔多个词）；**面包屑**是焦点历史，可回退。
- **导出**：工具栏可把当前图导出为 **PNG / SVG**（0.2.0）——调用图（时序 / 网状 / 调用路径）、模块关系、容器图、命名空间、入口流程、框架流程都行；组件矩阵是表格，不支持。
- **分享浏览路径**：页面「⇪ 分享」把时间线里的步骤（调用图、模块关系、调用路径）复制成 `vscode://…/trail?…` 链接；同事在 VS Code 里打开同一个仓库后点链接，就会在同样的位置重放一遍（0.2.0）。

## 4. 侧边栏与编辑器里的入口

- **调用大纲**（活动栏 CodeTrail Graph 图标）：光标所在方法的调用树，可**固定**（不跟随光标）、可隐藏框架调用。
- **影响范围**（同一活动栏，调用大纲下面）：反向的调用树 + 受影响的入口；和调用大纲共用一次光标解析，固定各管各的。
- **CodeLens**：显示 `被 N 处调用 · 调用 M 个 · 分派 K · 影响范围`；点开可跳到调用处或经分派的 Handler。快捷键 `Ctrl+Alt+H / J / R`（macOS `Ctrl+Cmd+H / J / R`），编辑器 `Alt+←` 退回。设置 `codeatlas.codeLens.enabled` 可关。
- **命令面板**里的常用项：打开面板、索引工作区（增量）、索引工作区（全量重建）、重启服务、选择 AI 服务、设置 / 清除 AI API Key。

## 5. 索引、设置与多项目

- **索引库**在 `<仓库>/.codeatlas/index.db`，**请加入 `.gitignore`**。索引格式 13 之前的旧库会自动全量重建。
- **自动索引**：保存文件后增量索引（`codeatlas.indexOnSave`，默认开）；编辑器之外的改动（AI 助手直接写盘、`git pull`、切分支）由磁盘监听防抖后索引（`codeatlas.indexOnExternalChange`，默认开）。服务启动时还会和磁盘对一次账；视图涉及的文件在索引之后又改过时，会提示结果可能过期。
- **手动**：面板 **⟳ 索引** 按钮，或命令面板「索引工作区（增量 / 全量重建）」。全量重建优先，且会按当前界面语言重新生成地图与框架流程的文字。
- **多项目**：
  - **多根工作区**：第一个文件夹是主根，其余自动作为外部项目一起索引，前端 `fetch('/api/...')` 才能连到另一个仓库的后端。
  - **仓库外目录**：设置 `codeatlas.projects`（如 `["../shop-api"]`），或写进 `.codeatlas/config.json`。
  - **嵌套仓库**默认一起索引（带独立 `.git` 的克隆、子模块）；git worktree 总是跳过。`codeatlas.includeNestedRepos: false` 可关。
  - 单独打开前端仓库时，会提示一次"是否一起索引旁边的后端"。
- **界面语言**跟随 VS Code：中文环境显示中文，其他语言显示英文。想立刻换掉索引里已存的文字，跑一次全量重建。

## 6. 命令行：改动影响面与调用路径

服务与扩展之外的常用命令（先 `codeatlas index <repo>` 建索引）：

```bash
# 还没提交的改动会影响哪些入口、要跑哪些测试
codeatlas affected /path/to/repo
codeatlas affected /path/to/repo --base origin/main --quiet | xargs npx vitest run   # CI 只跑相关测试
git diff -U0 origin/main | codeatlas affected /path/to/repo --stdin

# 从 A 到 B 的调用路径
codeatlas query /path/to/repo Acme.Orders.OrderService.Find --to Acme.Orders.OrderController.Create

# CI：文件级 import 环（有环退出码 1）
codeatlas arch /path/to/repo --cycles files
```

输出语言默认跟随系统，可用 `--lang zh|en` 或环境变量 `CODEATLAS_LOCALE` 指定。

## 7. 给 AI 编程助手用（MCP）

```bash
codeatlas mcp /path/to/repo           # stdio 上的 MCP server
claude mcp add --scope project codeatlas -- codeatlas mcp .
```

工具：`codeatlas_explore`（主工具，一次给源码 + 调用路径 + 影响摘要）、`codeatlas_search`、`codeatlas_callers`、`codeatlas_callees`、`codeatlas_impact`、`codeatlas_status`。
重建索引不需要重启 server。

## 8. AI 叙述（可选）

项目地图的容器图 / 组件矩阵 / 入口流程右侧有叙述卡：模型写一段话 + 关键点 + 风险，每条后面的标签是它引用的事实（灰色"推断"表示对不上事实）。

- 结果缓存在 `.codeatlas/narratives.db`，**重建索引不丢**，按语言分开缓存。
- 相关文件变了才会提示"可能过期"，点 **更新** 才重算；换模型不算过期。
- 选服务：命令面板「选择 AI 服务」；联网需要 API Key，存在 VS Code 凭据库，不进 `settings.json`。
- 隐私：联网时会把事实子图和（可选）入口方法源码发到该端点，设置 `codeatlas.ai.includeSource` 可关源码。**把公司代码发给第三方前先确认合规。**

## 9. 卡住了怎么办

| 现象 | 处理 |
| --- | --- |
| 首次右键提示等待服务端口超时 | 杀毒扫描拖慢了启动，等几秒再右键一次 |
| 服务不可用 | 命令面板「CodeTrail Graph: 重启服务」，看输出面板 **CodeTrail Graph** 通道；确认装的是本机平台的包 |
| 右键菜单里没有 CodeTrail Graph | 没有完全退出 VS Code，退出所有窗口再打开 |
| 更新扩展报 `EPERM` | 服务文件被占用，完全退出 VS Code 后重装 |
| 结果可能过期 | 索引后文件又改了，点面板 ⟳ 索引刷新 |
| 装错平台的包 | 面板会提示该装哪个平台的包；按提示换成对应 VSIX |
| "N 个可能目标"几十个候选 | tree-sitter 无法唯一绑定；C# 装 .NET 10 SDK 后可用 Roslyn 提高精度，但不能消除所有动态调用歧义 |

更长的排查清单和每一项功能的细节见 [完整使用指南](vscode-extension-guide.md)。
