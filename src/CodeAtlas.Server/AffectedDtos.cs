namespace CodeAtlas.Server;

// ---- 改动影响分析（codeatlas affected / GET /api/affected）：git diff 的改动行 → 改动的符号 → 受影响的入口与测试 ----
// 单独一个文件，不往 Dtos.cs 里加：规则见 AffectedQuery。

/// <summary>
/// 一次改动影响分析的结果。
/// </summary>
/// <param name="Base">比较基准：git 模式是用户给的 ref（默认 HEAD），--stdin 时为 null。</param>
/// <param name="MergeBase">git 模式下实际比较的提交（base 与 HEAD 的 merge-base，短哈希）；--stdin 时为 null。</param>
/// <param name="Input">git（自己跑 git diff）/ diff（stdin 里的统一 diff）/ files（stdin 里的文件名列表，只能文件级）。</param>
/// <param name="Files">每个改动文件怎么处理的（按路径排）。</param>
/// <param name="Symbols">改动的符号（种子），各自往上走到的入口与测试；按文件、行排。</param>
/// <param name="Entries">全部种子合起来受影响的入口（按深度、种类、路由排），From 是能走到它的种子符号 id。</param>
/// <param name="Tests">全部种子合起来受影响的测试方法（按文件、行排）。</param>
/// <param name="TestFiles">要跑的测试文件：受影响测试所在的文件 + 本身就改了的测试文件（已删除的不算），按路径排，相对仓库根。</param>
/// <param name="Depth">往上走的层数上限。</param>
/// <param name="Truncated">有种子按层数 / 节点上限停下了，入口与测试可能不全。</param>
/// <param name="Warnings">索引过期（改动文件在索引之后又改过）等提示。</param>
public sealed record AffectedDto(
    string? Base, string? MergeBase, string Input,
    IReadOnlyList<AffectedFileDto> Files, IReadOnlyList<AffectedSymbolDto> Symbols,
    IReadOnlyList<AffectedEntryDto> Entries, IReadOnlyList<AffectedTestDto> Tests, IReadOnlyList<string> TestFiles,
    int Depth, bool Truncated, IReadOnlyList<string> Warnings);

/// <param name="Path">新侧路径（相对仓库根，'/' 分隔）；删除的文件是旧路径。</param>
/// <param name="OldPath">重命名前的路径；其余为 null。</param>
/// <param name="Status">added / deleted / renamed / modified。</param>
/// <param name="Analysis">
/// symbols：改动行都落在符号里，按符号算；file：退化为文件级（改动落在符号之外——import / using / 文件顶部，或新文件、文件名列表、二进制），文件里的符号全算；
/// skipped：没算（不在索引里 / 非源码、已删除、只改名、只有空白改动），原因在 Note。
/// </param>
/// <param name="Ranges">新侧改动区间（git -U0 的 +start,count；count = 0 是纯删除，删在 start 行之后）。文件级与文件名列表时为空。</param>
/// <param name="Outside">落在任何符号之外的改动行（新侧行号；纯删除记删除点的下一行）。Analysis = file 的原因之一。</param>
/// <param name="Symbols">这个文件贡献了几个种子符号。</param>
/// <param name="IsTest">文件本身是测试文件（按 --filter 或默认的测试识别规则）。</param>
/// <param name="Stale">索引里的大小 / 修改时间与磁盘上不一致：索引比工作区旧，行号可能对不上。</param>
public sealed record AffectedFileDto(
    string Path, string? OldPath, string Status, string Analysis, IReadOnlyList<AffectedRangeDto> Ranges, IReadOnlyList<int> Outside,
    int Symbols, bool IsTest, bool Stale, string? Note);

/// <param name="Start">新侧起始行（1 起）。</param>
/// <param name="Count">新侧行数；0 = 纯删除（删在 Start 行之后，Start = 0 时删在文件开头）。</param>
public sealed record AffectedRangeDto(int Start, int Count);

/// <summary>一个改动的符号（种子）。</summary>
/// <param name="Reason">lines：有改动行落在它里面；file：文件级退化带进来的（见 AffectedFileDto.Analysis）。</param>
/// <param name="Lines">落在它里面的改动行（新侧，Reason = lines 时才有）。</param>
/// <param name="Callers">往上走到的非测试方法个数（不含它自己）。</param>
/// <param name="Truncated">这个符号往上走按层数 / 节点上限停下了。</param>
public sealed record AffectedSymbolDto(
    SymbolDto Symbol, string Label, string Reason, IReadOnlyList<int> Lines, bool IsTest,
    IReadOnlyList<AffectedEntryDto> Entries, IReadOnlyList<AffectedTestDto> Tests, int Callers, bool Truncated);

/// <summary>受影响的入口。</summary>
/// <param name="Depth">往上走了几层到达（种子自己就是入口 = 0）。</param>
/// <param name="Confidence">链路置信度：所有到达路径里"最弱一条边"最强的那条（max-min）。低于 0.7 是推测（经 0.5 档的多实现 / 接口补充 / 全仓同名边）。</param>
/// <param name="From">能走到它的种子符号 id（单个种子的列表里只有它自己）。</param>
public sealed record AffectedEntryDto(EntryDto Entry, string ContainerKey, string ContainerName, int Depth, double Confidence, IReadOnlyList<long> From);

/// <summary>受影响的测试方法（测试代码里、往上走到的符号；本身就改了的测试符号 Depth = 0）。</summary>
public sealed record AffectedTestDto(SymbolDto Symbol, string Label, int Depth, double Confidence, IReadOnlyList<long> From);
