# CodeAtlas

**[安装 VS Code 插件：CodeTrail Graph](https://marketplace.visualstudio.com/items?itemName=164910441.codetrail-graph)** — 打开市场页面后点击 **Install**，按提示在 VS Code 中完成安装。

本地代码阅读器，帮人重新读懂（尤其是 AI 写的）代码。两个方向：**从项目往下**（容器图 → 框架流程 → 入口流程），**从方法出发**（调用时序、调用大纲、影响范围、模块关系、编辑器里的 CodeLens 跳转）。

当前状态：有 VS Code 扩展（面板 + 侧边栏 + CodeLens）、本地服务和命令行。能把 TypeScript / TSX / JavaScript / C# / Go / Python / Java 仓库索引进 SQLite（C# 另有 Roslyn 精确语义），识别部分分派链路和框架管道，并支持本地或联网 AI 叙述。早期里程碑规划见 [设计文档](docs/superpowers/specs/2026-10-13-codeatlas-design.md) 与 [扩展设计](docs/superpowers/specs/2026-10-13-vscode-extension-design.md)，当前能力和限制以使用指南为准。

## 在 VS Code 里用（推荐）

市场品牌为 **CodeTrail Graph**，后端项目仍叫 CodeAtlas；内部 `codeatlas.*` 命令、设置及 `.codeatlas` 索引目录不变。

1. 打开 [CodeTrail Graph 插件市场页面](https://marketplace.visualstudio.com/items?itemName=164910441.codetrail-graph) 点击 **Install**，或运行 `code --install-extension 164910441.codetrail-graph`。离线包及源码构建方式见 [使用指南](docs/vscode-extension-guide.md)；构建需要 .NET 10 SDK，最终用户无需另装 .NET 运行时。
2. 打开仓库，把光标放在任意方法或类名上，**右键 → CodeTrail Graph → 在调用图中查看**（或 `Ctrl+Alt+G`）。第一次会提示建立索引，耗时取决于项目规模与是否启用 Roslyn。
3. 调用图默认是**时序图**，按调用顺序从上到下、按项目分列；工具栏「时序 | 网状」可切换画法。网状图自动向下展开主分支，「↑ 到源头」追调用者。项目地图包括容器、框架流程、命名空间，面板支持跨视图后退 / 前进。
4. 保存文件后自动增量索引（可在设置 `codeatlas.indexOnSave` 关闭）；编辑器之外的改动（AI 助手直接写盘、git pull / 切分支）由磁盘监听防抖后增量索引（`codeatlas.indexOnExternalChange`），服务启动时还会和磁盘对一次账；视图涉及的文件在索引后改过时会提示结果可能过期；C# 有待更新文件时会自动补算 Roslyn。服务进程由扩展管理，命令面板里有「CodeTrail Graph: 重启服务」。

当前使用索引格式 **13**（0.1.1 发布时为 12；13 加了 TS / JS 事件频道的合成边，见下面「怎么读 confidence」）：旧索引会自动全量重建。原始引用持久化后，增量索引可重新绑定未修改的调用方；索引请求串行排队、全量请求优先，过期的异步视图响应不会覆盖新选择。性能变化需在实际项目上测量，不保证固定耗时。

命令行方式仍然可用，见下面。

完整的安装与使用指南（前提、更新、看懂这张图、工具栏、项目地图、常见问题）见 [docs/vscode-extension-guide.md](docs/vscode-extension-guide.md)。

多平台打包、Marketplace 上传和后续升级见 [发布指南](docs/vscode-extension-publishing.md)。

## 快速上手

### 1. 拿到可执行文件

发布后在仓库的 `dist\codeatlas.exe`（`dist/` 不入 git；自带 .NET 运行时，不用另装）。

想自己重新发布：

```bash
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

会得到 `codeatlas.exe`（自带运行时）和 `roslyn\codeatlas-roslyn.exe`（C# 精确语义用的 Roslyn worker，需要本机 .NET 10 SDK；没有时 C# 退回 tree-sitter）。其他平台加 `-Rid linux-x64` / `osx-arm64` 等。建议把 `dist` 目录加进 PATH，下面的例子直接写 `codeatlas`。

`codeatlas index <repo>` 在 tree-sitter 之后会自动用 Roslyn 覆盖 C# 的调用/实现/引用边（限定名带参数类型，重载不再混淆；分发链路按真实类型解析）。`--roslyn off` 关掉，`--roslyn on` 缺 worker 或解决方案时报错，`--sln <path>` 指定解决方案（默认取仓库里最大的 .sln/.slnx）。

### 2. 索引一个仓库

```bash
codeatlas index D:\code\shop-api
```

- 索引库默认写在仓库的 `.codeatlas\index.db`。**记得把 `.codeatlas/` 加进那个仓库的 `.gitignore`**，工具会提示但不会替你改。
- 不想污染仓库，用 `--db` 指到别处：`codeatlas index <repo> --db C:\tmp\shop.db`。之后 `query` / `search` 也要带同一个 `--db`。
- 第二次运行复用未变化文件的解析结果（大小、修改时间及内容哈希），重新绑定需要更新的引用；架构输入或 Git ref 不变时跳过相应后处理。CLI 输出各阶段 `timings`，实际耗时以项目实测为准。
- `--full` 强制全部重解析。改了工具版本或者感觉索引不对劲时用。
- 自动跳过 `node_modules`、`bin`、`obj`、`.git`、`dist`、`build`、`.vs`、`.idea`、`.codeatlas` 等生成目录。嵌套仓库和子模块默认纳入，`--no-nested` 关闭；Git worktree 副本始终跳过。
- 大于 2MB 的文件标记为 skipped 不解析。

参考数据（一个 C# + TS 的中型后端）：4373 个文件、5.9 万个符号、73 万条边，全量 21 秒。

### 3. 搜一个符号

```bash
codeatlas search D:\code\shop-api OrderApproval
```

按名字前缀搜，不区分大小写，多个词用空格隔开表示都要命中。输出是：限定名、种类、文件:行号。

### 4. 看它的调用关系

```bash
codeatlas query D:\code\shop-api Acme.Shop.Api.Controllers.OrderApprovalController.ApproveAsync
```

输出长这样：

```
Acme.Shop.Api.Controllers.OrderApprovalController.ApproveAsync  [method]  Acme.Shop.Api/Controllers/OrderApprovalController.cs:74
  → references  Acme.Shop.Application.Dtos.ApproveDto  (0.7)  @ line 81
  → calls  Acme.Shop.Application.Orders.Commands.ApproveOrderCommand  (0.7)  @ line 84
  → calls  Acme.Shop.Application.Abstractions.IDispatcher.SendAsync  (0.7)  @ line 85
  ← calls  <调用它的地方>  (0.9)  @ line 12
```

- `→` 是它调出去的，`←` 是谁调它。
- 括号里是 **confidence**，表示这条边是怎么推出来的（见下节）。
- `@ line` 是调用发生在哪一行（在 `→` 的情况下是当前方法里的行；`←` 的情况下是调用方文件里的行）。

常用参数：

| 参数 | 作用 |
|---|---|
| `--in` / `--out` | 只看调用者 / 只看被调用者。默认两个方向都看 |
| `--depth N` | 递归展开 N 层（最多 6），缩进表示层级 |
| `--kinds calls,implements` | 只看某几种边。可选：calls、references、inherits、implements、imports、contains、dispatches。默认不显示 contains 和 imports |
| `--db <path>` | 索引库位置，和 index 时保持一致 |

符号可以用短名或限定名。短名有多个同名定义时会列出所有候选让你挑限定名：

```bash
codeatlas query D:\code\shop-api GetAsync
```

几个值得试的问题：

- 一个接口都有哪些实现：`codeatlas query <repo> <接口限定名> --in --kinds implements`
- 一个方法往下三层都碰了什么：`codeatlas query <repo> <方法限定名> --out --depth 3`
- 一个 Repository 方法被哪些 Handler 用：`codeatlas query <repo> <方法限定名> --in`
- 从 A 怎么走到 B：`codeatlas query <repo> <A> --to <B>`，列出 A → B 的调用路径（沿 calls 与 dispatches 分派边，默认最多 8 步、10 条，`--depth N` 改步数上限（≤ 12）、`--max-paths N` 改条数（≤ 50）、`--json` 输出结构化结果）。最短的在前；每步一行，`→` 是 confidence ≥ 0.7 的调用，`⇢` 是分派边或低置信度边，不是确定调用。两端同名多个时会列出候选（带 `#符号id`，可直接当参数）。面板里对应调用图工具栏的「⇢ 路径到…」

### 5. CI：文件级 import 环

```bash
codeatlas index <repo> --db ci.db
codeatlas arch <repo> --cycles files --db ci.db          # 有环退出码 1，没环 0，没有索引 / 参数错误 2
codeatlas arch <repo> --cycles files --json --db ci.db   # 结构化输出：每个环的文件、一条最短环路径与每一步的 import 行号
```

基于索引里的 imports 边，只看"文件 → 文件"的 import（TS / JS、Python、Java、Go 的模块对模块）；C# 的 `using` 指向命名空间、外部包指向 `external:…`，不是文件，不参与（命名空间 / 项目级的环用 `arch --dsm`）。默认只算**确定 import**：confidence 1.0，且一条 import 只解析到一个文件。`--edges all` 再加**启发式边**：confidence < 1.0 的，以及一条 import 展开到多个文件的（Go 的包 import 连到包目录下每个文件、Java 的 `import a.b.*`）——这些是包级依赖，换成文件级会凭空多出环，所以默认不算；输出里会写有多少条没算，算进去时在那一步后面标「启发式」。

TS 仓库的限定名格式是 `相对路径:类.方法`，比如 `web/src/components/LoginPage/index.tsx:LoginPage`；C# 是命名空间链 `Acme.Orders.OrderService.Find`。

### 6. 给 AI 编程助手用（MCP）

`codeatlas mcp <repo> [--db <path>]` 在 stdio 上提供 MCP server（JSON-RPC 2.0，一行一条消息；stdout 只有协议消息，日志在 stderr），让 Claude Code 等助手直接查索引。先照常 `codeatlas index <repo>` 建好索引；之后重建索引不用重启 server（每次调用重新打开库）。

在 Claude Code 里注册（项目级，写进仓库根的 `.mcp.json`）：

```bash
claude mcp add --scope project codeatlas -- codeatlas mcp .
```

`.` 是 Claude Code 启动 server 时的工作目录（项目根）；不放心就写仓库的绝对路径。也可以手写 `.mcp.json`（`command` 写可执行文件的完整路径更稳；用了 `--db` 就把同一个 `--db` 加进 `args`）：

```json
{
  "mcpServers": {
    "codeatlas": { "command": "D:\\tools\\codeatlas\\codeatlas.exe", "args": ["mcp", "."] }
  }
}
```

工具：

| 工具 | 作用 |
|---|---|
| `codeatlas_explore` | **主工具**。输入自然语言问题或一串符号名 / 文件名，一次返回：最相关符号的源码（磁盘实时读、带行号、按文件分组）、它们之间的调用路径（含分派边）、各自调出去的调用、影响范围摘要（直接调用方、测试调用方数、受影响入口）。`maxChars` 调预算（默认 24000 字符），超出按相关度截断并列出没贴的符号；文件在建索引后改过（大小 / 修改时间对不上）时顶部有过期警告 |
| `codeatlas_search` | 按名字前缀找符号 |
| `codeatlas_callers` / `codeatlas_callees` | 单个符号的直接调用方 / 调出去的调用 |
| `codeatlas_impact` | 完整影响范围（所有受影响入口） |
| `codeatlas_status` | 索引状态与过期文件 |

每条边都带 confidence（见下节）；0.3 的同名多候选在输出里标"低置信"，不当成确定的调用。

### 7. 这次改动波及哪些入口和测试（affected）

```bash
codeatlas affected D:\code\shop-api                       # 还没提交的改动（默认 base = HEAD）
codeatlas affected D:\code\shop-api --base origin/main    # 这个分支相对主干改了什么（审 PR）
codeatlas affected D:\code\shop-api --base origin/main --quiet | xargs npx vitest run   # CI 只跑相关测试
git diff -U0 origin/main | codeatlas affected D:\code\shop-api --stdin
```

- **符号级**：diff 的改动行（新侧行号，对应工作区，也就是索引里的行）映射到包含它的最内层方法 / 函数 / 属性 / 类型；字段算到所属类型上；删掉的行看删除点两边属于谁（删掉整个方法 → 所在的类）。再沿影响范围同一套规则往上走：受影响的入口（HTTP 路由、命令、定时任务……）与测试方法。改类型 / 枚举时还会算上把它当参数、字段类型用的地方。
- **默认 base = HEAD**：索引反映的是工作区，HEAD 是唯一不用猜分支名就总在的基准。给 `--base` 时取它与 HEAD 的 merge-base（主干后来的提交不算），再与工作区比；未跟踪的新文件按新增算。
- **退化为文件级**：改动落在任何符号之外（using / import、文件顶部）、新文件、二进制、`--stdin` 喂的是文件名列表时，文件里的符号全算，输出里标「文件级」。
- **跳过并说明原因**：非源码文件、不在索引里的源码、已删除的文件、只改名、只有空白改动。改动文件在索引之后又改过时提示索引过期（行号可能对不上）。
- `--stdin` 自动识别统一 diff（建议 `-U0`）与文件名列表（`--name-only` / `--name-status`）；`--depth N` 往上的层数，默认 15（与侧边栏影响范围一致）；`--filter <glob>` 可重复，自定义哪些路径算测试（取代默认的 Tests / test / spec 规则）；`--json` 完整结果；`--quiet` 只打印测试文件（相对仓库根，一行一个）。
- 每个入口 / 测试带链路置信度（所有路径里最弱一条边最强的那条），低于 0.7 标「推测」。置信度 0.5 以下的边不走（同影响范围）。
- 没有 git 时报错并提示改用 `--stdin`。只看主仓库：`--project` 外部根和嵌套仓库里的改动不在 git diff 里。

## 怎么读 confidence

这个里程碑的解析是 **tree-sitter 按名字做的启发式**，不是编译器级语义。每条边都带一个 0 到 1 的分数，告诉你它是怎么猜的：

| 分数 | 含义 | 可信度 |
|---|---|---|
| 1.0 | 包含关系、import 解析到了具体文件或命名空间 | 确定 |
| 0.9 | 同一个文件里有且只有一个同名定义 | 很高，但同文件里 `repository.Foo()` 恰好和自己的 `Foo()` 重名时会错 |
| 0.7 | 在 import / using 范围内有且只有一个同名定义 | 高 |
| 0.5 | 全仓库有且只有一个同名定义；或者没找到定义、归为外部库（`external:xxx`） | 中 |
| 0.4（合成） | **合成边**，不是代码里写出来的调用：TS / JS 里 `x.emit('频道')` 所在的函数 → 同名频道 `x.on('频道', handler)`（once / addListener / addEventListener 同理）注册的处理函数，边上 via = `event:频道`。只按频道名配对、不看是不是同一个发射器对象；`data` / `error` / `close` / `click` 等通用事件名、一个频道超过 10 个处理函数或 50 处发出的不配 | 推断，当"运行时大概会走到这里"看；时序图里是紫色虚线、标「事件 频道」 |
| 0.3 | 有多个候选，分不清是哪个，每个候选各一条边（最多 20 条） | 低，当"可能是这些之一"看 |

回调 / observer 注册（`stream.on('data', onData)`、`useMutation({ mutationFn: login })`：注册函数 → 处理函数，via = `callback`，封顶 0.85）与 JSX 渲染（父组件 → `<Child />`，via = `render`）在解析阶段就有，属于按 import / 名字解析出的边，不在合成边这一档。

0.3 的边是常见的噪声来源。同名方法无法按导入与接收者信息唯一定位时，会列出候选。C# 的 Roslyn 增强器已可用，会用真实类型信息覆盖可解析的边；其他语言和缺 SDK 时仍以启发式结果为主。

`external:xxx` 是仓库外的目标（框架、第三方包、标准库），也包括暂时没解析出来的动态目标。支持常见 tsconfig `paths` 别名，但复杂再导出与解构仍有限制；外部节点不能展开。

## 现在能做什么、不能做什么

能：

- TS / TSX / JS / C# / Go / Python / Java 的符号与调用关系（各语言适用的符号种类不同）。
- 直接调用、`new`、继承、接口实现、类型引用、import / using。
- C# 文件级命名空间，`I` 开头的基类型自动判为 implements。
- 增量索引、文件删除后的悬空边保留、重建后自动重挂。
- VS Code 调用时序 / 网状图、调用大纲、影响范围、模块关系、CodeLens，以及项目地图与框架流程。
- C# Roslyn 精确语义、可静态识别的分派链路、部分架构规则与 AI 叙述（可关闭）。

还不能（都有计划）：

- 任意反射、动态分派、运行时生成路由的完整还原；图中框架阶段与顺序仅代表静态证据，未知顺序会标注。
- 无 Roslyn 时，tree-sitter 分派兜底只覆盖直接消息构造实参；先赋变量或工厂生成的消息不保证识别。
- Blazor `.razor` 解析、复杂重命名转导出和完整的跨语言类型推断。
- Kotlin / Rust 等其他语言（底座支持，还没写查询文件）。Java 已支持（Maven / Gradle、Spring MVC / WebFlux / JAX-RS / gRPC / 定时任务 / 消息监听 / picocli），但 Gradle 版本目录、Kotlin、Lombok 生成的方法、按实参类型挑重载不支持。
- 类型推断有边界：调用返回的泛型 `T`、lambda 参数、元组解构、Go 的多返回值第二个起、Python 未注解的参数推不出来，这些调用按名字近似解析（置信度 0.7 / 0.3）。
- 列号按 UTF-8 字节算，含中文的行会偏。

更完整的验收数据和已知问题见 [docs/acceptance.md](docs/acceptance.md)。

## 开发

```bash
dotnet build
dotnet test CodeAtlas.slnx --verbosity minimal
npm --prefix extension run typecheck
npm --prefix extension test
```

项目结构：

```
src/CodeAtlas.Core                 模型、SQLite 读写、启发式解析器、IEdgeResolver 扩展点
src/CodeAtlas.Indexing.TreeSitter  适配器底座 + 每语言一对 .scm 查询 + 薄适配器；扫描器；索引器
src/CodeAtlas.Cli                  index / query / search / arch / serve / mcp
src/CodeAtlas.Server               本地 API、项目地图、框架流程与 AI 叙述
src/CodeAtlas.Indexing.Roslyn       C# 精确分析与独立 worker
extension/                        VS Code 扩展与 Webview
tests/                             每个项目一个测试项目，fixtures 是真实的小源文件
docs/superpowers/specs             设计文档
docs/superpowers/plans             实施计划（里程碑 1 已完成）
docs/acceptance.md                 真实仓库验收记录
```

加一种语言：在 `Queries/` 下写 `<lang>.definitions.scm` 和 `<lang>.references.scm`，在 `Adapters/` 下写一个几十行的子类，在 `tests/.../Fixtures/<lang>/` 放一个小源文件写快照测试。约定见 `LanguageAdapterBase` 的注释。
