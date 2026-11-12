using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Server.Tests;

/// <summary>改动影响分析：手写 diff 文本的解析（AffectedDiff）+ 内存库上手造带真实行区间的符号（AffectedQuery.Build：行 → 符号 → 入口 / 测试）。</summary>
public class AffectedTests
{
    // ---------- diff 解析 ----------

    [Fact]
    public void ParseDiff_U0_NewSideRanges_StatusesRenameBinaryQuotedPath()
    {
        var diff = string.Join("\n",
            "diff --git a/src/svc.ts b/src/svc.ts",
            "index 1..2 100644",
            "--- a/src/svc.ts",
            "+++ b/src/svc.ts",
            "@@ -1 +1 @@",
            "-import { a } from './a';",
            "+import { a } from \"./a\";",
            "@@ -7,2 +6,0 @@ export class Svc {",
            "-    return 1;",
            "---- a line that starts with three dashes",            // 内容以 --- 开头的删除行：按块内计数消费，不当文件头
            "@@ -20,0 +19,3 @@",
            "+a",
            "+++b",                                                  // 内容以 ++ 开头的新增行
            "+c",
            "diff --git a/new.ts b/new.ts",
            "new file mode 100644",
            "--- /dev/null",
            "+++ b/new.ts",
            "@@ -0,0 +1,2 @@",
            "+x",
            "+y",
            "diff --git a/gone.cs b/gone.cs",
            "deleted file mode 100644",
            "--- a/gone.cs",
            "+++ /dev/null",
            "@@ -1,3 +0,0 @@",
            "-a", "-b", "-c",
            "diff --git a/old/A.cs b/new/A.cs",
            "similarity index 100%",
            "rename from old/A.cs",
            "rename to new/A.cs",
            "diff --git a/img.png b/img.png",
            "Binary files a/img.png and b/img.png differ",
            "diff --git \"a/src/\\344\\270\\255.cs\" \"b/src/\\344\\270\\255.cs\"",
            "--- \"a/src/\\344\\270\\255.cs\"",
            "+++ \"b/src/\\344\\270\\255.cs\"",
            "@@ -3 +3 @@",
            "-",
            "+   ",                                                   // 空行改成只有空格：只有空白
            "");
        Assert.True(AffectedDiff.LooksLikeDiff(diff));
        var files = AffectedDiff.ParseDiff(diff);
        Assert.Equal(["src/svc.ts", "new.ts", "gone.cs", "new/A.cs", "img.png", "src/中.cs"], files.Select(f => f.Path));
        var svc = files[0];
        Assert.Equal(DiffStatus.Modified, svc.Status);
        Assert.Equal([(1, 1), (6, 0), (19, 3)], svc.Hunks!.Select(h => (h.NewStart, h.NewCount)));
        Assert.Equal((DiffStatus.Added, 1, 2), (files[1].Status, files[1].Hunks![0].NewStart, files[1].Hunks![0].NewCount));
        Assert.Equal(DiffStatus.Deleted, files[2].Status);
        Assert.Equal((DiffStatus.Renamed, "old/A.cs", 0), (files[3].Status, files[3].OldPath, files[3].Hunks!.Count));
        Assert.True(files[4].Binary);
        Assert.True(Assert.Single(files[5].Hunks!).Blank);
        Assert.False(svc.Hunks![0].Blank);
    }

    [Fact]
    public void ParseDiff_WithContext_OnlyChangedLinesAndDeletionPoints()
    {
        // -U3 风格：上下文行不算改动；纯删除记成 (删除点之前的新侧行, 0)
        var diff = string.Join("\n",
            "--- a/f.py",
            "+++ b/f.py",
            "@@ -1,7 +1,7 @@",
            " a",
            " b",
            "-c",
            " d",
            " e",
            "-f",
            "+F",
            " g",
            "");
        var f = Assert.Single(AffectedDiff.ParseDiff(diff));
        Assert.Equal("f.py", f.Path);
        Assert.Equal([(2, 0), (5, 1)], f.Hunks!.Select(h => (h.NewStart, h.NewCount)));
    }

    [Fact]
    public void ParseFileList_NameOnlyAndNameStatus()
    {
        var text = "src/a.ts\r\nM\tsrc/b.ts\nA\tsrc/c.ts\nD\tsrc/d.ts\nR087\tsrc/old.ts\tsrc/new.ts\n\n# 注释\n./src/a.ts\n";
        Assert.False(AffectedDiff.LooksLikeDiff(text));
        var files = AffectedDiff.ParseFileList(text);
        Assert.Equal(
            ["src/a.ts:Modified:", "src/b.ts:Modified:", "src/c.ts:Added:", "src/d.ts:Deleted:", "src/new.ts:Renamed:src/old.ts"],
            files.Select(f => $"{f.Path}:{f.Status}:{f.OldPath}"));
        Assert.All(files, f => Assert.Null(f.Hunks));
    }

    [Fact]
    public void GlobRegex_DoubleStarBracesAndBareFileNames()
    {
        Assert.Matches(AffectedQuery.GlobRegex("**/*.spec.ts"), "web/src/a.spec.ts");
        Assert.Matches(AffectedQuery.GlobRegex("**/*.spec.ts"), "a.spec.ts");
        Assert.DoesNotMatch(AffectedQuery.GlobRegex("src/*.ts"), "src/x/a.ts");
        Assert.Matches(AffectedQuery.GlobRegex("test/**"), "test/a/b.py");
        Assert.Matches(AffectedQuery.GlobRegex("*Check.{cs,java}"), "src/App/OrderCheck.java"); // 不含 / 的 glob 匹配任意目录下的文件名
        Assert.DoesNotMatch(AffectedQuery.GlobRegex("*Check.{cs,java}"), "src/App/OrderCheck.go");
    }

    // ---------- 行 → 符号 → 入口 / 测试 ----------

    /// <summary>内存库：文件 + 显式行区间的符号 + 边。</summary>
    private sealed class Repo : IDisposable
    {
        public IndexDatabase Db { get; } = IndexDatabase.OpenInMemory();
        private readonly IndexWriter _w;
        public Repo() => _w = new IndexWriter(Db);

        public long File(string path) => _w.UpsertFile(path, path.EndsWith(".cs", StringComparison.Ordinal) ? "csharp" : "typescript", "h", 1, FileStatus.Ok);

        public long Sym(long file, SymbolKind kind, string name, int start, int end, long? parent = null)
        {
            var qn = parent is { } p ? Db.Query<string>("SELECT qualified_name FROM symbols WHERE id = $id", ("$id", p)).Single() + "." + name : "App." + name;
            return Db.Scalar<long>("""
                INSERT INTO symbols (file_id, kind, name, qualified_name, start_line, start_col, end_line, end_col, parent_id, source)
                VALUES ($f, $k, $n, $q, $l, 0, $e, 0, $p, 'roslyn') RETURNING id
                """, ("$f", file), ("$k", DbNames.ToDb(kind)), ("$n", name), ("$q", qn), ("$l", start), ("$e", end), ("$p", parent));
        }

        public void Edge(long from, long to, double conf = 1.0, EdgeKind kind = EdgeKind.Calls, string? via = null) =>
            _w.WriteEdges([new NewEdge(from, to, "x", kind, IndexSource.Roslyn, conf, Db.Scalar<long>("SELECT file_id FROM symbols WHERE id = $id", ("$id", from)), 1, via)]);

        public void Http(long symbol, string route) =>
            EntryStorage.Replace(Db, [new EntryPoint(symbol, "src/Api", "aspnet.minimal", "http", route, "POST", "src/Api/Api.cs", 1)], []);

        public AffectedDto Run(string diff, AffectedOptions? options = null) =>
            AffectedQuery.Build(Db, null, AffectedDiff.LooksLikeDiff(diff) ? AffectedDiff.ParseDiff(diff) : AffectedDiff.ParseFileList(diff), "diff", null, null, options ?? new AffectedOptions());

        public void Dispose() => Db.Dispose();
    }

    private static string U0(string path, params string[] hunks) =>
        string.Join("\n", new[] { $"diff --git a/{path} b/{path}", $"--- a/{path}", $"+++ b/{path}" }.Concat(hunks)) + "\n";

    /// <summary>
    /// src/App/Order.cs：1-2 using，3-30 class Order（4 字段 Total，6-12 Place，14-20 Cancel）；
    /// src/Api/Api.cs：Post（HTTP 入口）→ Place；tests/App.Tests/OrderTests.cs：PlaceWorks → Place，CancelWorks → Cancel。
    /// </summary>
    private static (Repo R, long Order, long Place, long Cancel, long Post, long PlaceWorks, long CancelWorks) Shop()
    {
        var r = new Repo();
        ArchStorage.Replace(r.Db, new ArchModel([new ArchNode("src/Api", ArchNodeKind.Container, "dotnet-web", "Ordering.API", "src/Api", ArchSource.Discovered, [], ["src/Api"], [])], [], [], []));
        var f = r.File("src/App/Order.cs");
        var order = r.Sym(f, SymbolKind.Class, "Order", 3, 30);
        r.Sym(f, SymbolKind.Field, "Total", 4, 4, order);
        var place = r.Sym(f, SymbolKind.Method, "Place", 6, 12, order);
        var cancel = r.Sym(f, SymbolKind.Method, "Cancel", 14, 20, order);
        var api = r.File("src/Api/Api.cs");
        var post = r.Sym(api, SymbolKind.Method, "Post", 5, 9, r.Sym(api, SymbolKind.Class, "OrdersApi", 1, 20));
        r.Edge(post, place);
        r.Http(post, "/api/orders");
        var tf = r.File("tests/App.Tests/OrderTests.cs");
        var tc = r.Sym(tf, SymbolKind.Class, "OrderTests", 1, 40);
        var placeWorks = r.Sym(tf, SymbolKind.Method, "PlaceWorks", 3, 9, tc);
        var cancelWorks = r.Sym(tf, SymbolKind.Method, "CancelWorks", 11, 19, tc);
        r.Edge(placeWorks, place);
        r.Edge(cancelWorks, cancel);
        return (r, order, place, cancel, post, placeWorks, cancelWorks);
    }

    private static string Seeds(AffectedDto a) => string.Join(" | ", a.Symbols.Select(s => $"{s.Label}:{s.Reason}" + (s.Lines.Count > 0 ? ":" + string.Join(",", s.Lines) : "")));

    [Fact]
    public void LineInsideMethod_SeedsThatMethodOnly_ReachesEntryAndItsTest()
    {
        var (r, _, place, _, post, placeWorks, _) = Shop();
        using var _r = r;
        var a = r.Run(U0("src/App/Order.cs", "@@ -8 +8,2 @@", "-x", "+y", "+z"));
        Assert.Equal("Order.Place:lines:8,9", Seeds(a));
        var file = Assert.Single(a.Files);
        Assert.Equal(("symbols", 1, false), (file.Analysis, file.Symbols, file.IsTest));
        var entry = Assert.Single(a.Entries);
        Assert.Equal(("/api/orders", "Ordering.API", 1, 1.0, place), (entry.Entry.Route, entry.ContainerName, entry.Depth, entry.Confidence, entry.From.Single()));
        Assert.Equal(post, entry.Entry.SymbolId);
        var test = Assert.Single(a.Tests);
        Assert.Equal((placeWorks, "OrderTests.PlaceWorks", 1), (test.Symbol.Id, test.Label, test.Depth));
        Assert.Equal(["tests/App.Tests/OrderTests.cs"], a.TestFiles);
        Assert.Equal(1, a.Symbols[0].Callers); // Post（测试不算）
    }

    [Fact]
    public void ChangeOutsideAnySymbol_FallsBackToFileLevel_AndSaysWhere()
    {
        var (r, _, _, _, _, placeWorks, cancelWorks) = Shop();
        using var _r = r;
        var a = r.Run(U0("src/App/Order.cs", "@@ -1 +1 @@", "-using A;", "+using B;", "@@ -15 +15 @@", "-a", "+b"));
        var file = Assert.Single(a.Files);
        Assert.Equal("file", file.Analysis);
        Assert.Equal([1], file.Outside);
        Assert.Contains("L1", file.Note);
        // 字段不当种子；落在 Cancel 里的那行仍标 lines
        Assert.Equal("Order:file | Order.Place:file | Order.Cancel:lines:15", Seeds(a));
        Assert.Equal([placeWorks, cancelWorks], a.Tests.Select(t => t.Symbol.Id));
    }

    [Fact]
    public void PureDeletionBetweenMethods_SeedsEnclosingClass_FieldLineToo_AndTypeSeedsWalkReferences()
    {
        var (r, order, _, _, _, _, _) = Shop();
        using var _r = r;
        // 用到 Order 这个类型（参数类型）的方法：references 入边，类型种子的第一跳
        var f = r.File("src/App/Billing.cs");
        var bill = r.Sym(f, SymbolKind.Method, "Bill", 3, 8, r.Sym(f, SymbolKind.Class, "Billing", 1, 10));
        r.Edge(bill, order, conf: 0.9, kind: EdgeKind.References);
        var tf = r.File("tests/App.Tests/BillingTests.cs");
        var billWorks = r.Sym(tf, SymbolKind.Method, "BillWorks", 2, 6, r.Sym(tf, SymbolKind.Class, "BillingTests", 1, 10));
        r.Edge(billWorks, bill, conf: 0.5);

        // 删在 12 行（Place 的结尾）之后：12 与 13 不同属一个方法 → 类
        var del = r.Run(U0("src/App/Order.cs", "@@ -13,1 +12,0 @@", "-    // gone"));
        Assert.Equal("Order:lines:13", Seeds(del));
        // 改字段那一行 → 所属类型
        var field = r.Run(U0("src/App/Order.cs", "@@ -4 +4 @@", "-int Total;", "+long Total;"));
        Assert.Equal("Order:lines:4", Seeds(field));
        var t = Assert.Single(field.Tests);
        // 链路 Order ←ref 0.9← Bill ←0.5← BillWorks：最弱一环 0.5（推测）
        Assert.Equal((billWorks, 2, 0.5), (t.Symbol.Id, t.Depth, t.Confidence));
        Assert.Equal(["tests/App.Tests/BillingTests.cs"], field.TestFiles);
    }

    [Fact]
    public void LowConfidenceEdgesBelowWalkThreshold_AreNotFollowed_MaxMinPicksStrongerPath()
    {
        var (r, _, place, _, _, _, _) = Shop();
        using var _r = r;
        var f = r.File("src/App/Jobs.cs");
        var t = r.Sym(f, SymbolKind.Class, "Jobs", 1, 30);
        var guess = r.Sym(f, SymbolKind.Method, "Guess", 2, 5, t);    // 0.3 多候选：不走
        var weak = r.Sym(f, SymbolKind.Method, "Weak", 7, 10, t);
        var strong = r.Sym(f, SymbolKind.Method, "Strong", 12, 15, t);
        var top = r.Sym(f, SymbolKind.Method, "Top", 17, 20, t);
        r.Edge(guess, place, conf: 0.3);
        r.Edge(weak, place, conf: 0.5);
        r.Edge(strong, place, conf: 0.9);
        r.Edge(top, weak, conf: 1.0);
        r.Edge(top, strong, conf: 0.7);
        var tf = r.File("tests/App.Tests/JobsTests.cs");
        var guessTest = r.Sym(tf, SymbolKind.Method, "GuessTest", 2, 4, r.Sym(tf, SymbolKind.Class, "JobsTests", 1, 10));
        r.Edge(guessTest, guess);
        var topTest = r.Sym(tf, SymbolKind.Method, "TopTest", 6, 8, r.Sym(tf, SymbolKind.Class, "JobsTests2", 5, 10));
        r.Edge(topTest, top);

        var a = r.Run(U0("src/App/Order.cs", "@@ -7 +7 @@", "-a", "+b"));
        Assert.DoesNotContain(a.Tests, x => x.Symbol.Id == guessTest);
        var tt = Assert.Single(a.Tests, x => x.Symbol.Id == topTest);
        // Top 经 Weak 是 min(0.5, 1) = 0.5，经 Strong 是 min(0.9, 0.7) = 0.7：取 0.7
        Assert.Equal((3, 0.7), (tt.Depth, tt.Confidence));
    }

    [Fact]
    public void TestHelpers_PropagateToTests_ButTestCodeNeverWalksBackIntoProduct()
    {
        var (r, _, place, _, _, _, _) = Shop();
        using var _r = r;
        var tf = r.File("tests/App.Tests/Fixtures.cs");
        var fx = r.Sym(tf, SymbolKind.Class, "Fixtures", 1, 50);
        var helper = r.Sym(tf, SymbolKind.Method, "MakeOrder", 2, 6, fx);
        var usesHelper = r.Sym(tf, SymbolKind.Method, "UsesHelper", 8, 12, fx);
        r.Edge(helper, place);
        r.Edge(usesHelper, helper);
        // 产品代码调了测试辅助方法（不该有，但真有时不能经它把产品代码算进来）
        var f = r.File("src/App/Weird.cs");
        var weird = r.Sym(f, SymbolKind.Method, "Weird", 2, 5, r.Sym(f, SymbolKind.Class, "Weird", 1, 10));
        r.Edge(weird, helper);
        r.Http(weird, "/weird");

        var a = r.Run(U0("src/App/Order.cs", "@@ -7 +7 @@", "-a", "+b"));
        Assert.Contains(a.Tests, t => t.Symbol.Id == usesHelper && t.Depth == 2);
        Assert.DoesNotContain(a.Entries, e => e.Entry.Route == "/weird");
        Assert.Contains("tests/App.Tests/Fixtures.cs", a.TestFiles);
    }

    [Fact]
    public void ChangedTestFile_IsItselfATest_AtDepthZero()
    {
        var (r, _, _, _, _, _, cancelWorks) = Shop();
        using var _r = r;
        var a = r.Run(U0("tests/App.Tests/OrderTests.cs", "@@ -12 +12 @@", "-a", "+b"));
        var f = Assert.Single(a.Files);
        Assert.True(f.IsTest);
        Assert.Equal("OrderTests.CancelWorks:lines:12", Seeds(a));
        Assert.True(a.Symbols[0].IsTest);
        var t = Assert.Single(a.Tests);
        Assert.Equal((cancelWorks, 0), (t.Symbol.Id, t.Depth));
        Assert.Equal(["tests/App.Tests/OrderTests.cs"], a.TestFiles);
    }

    [Fact]
    public void SkippedFiles_NotIndexed_DeletedRenameOnlyWhitespace_HaveExplicitNotes()
    {
        var (r, _, _, _, _, _, _) = Shop();
        using var _r = r;
        var diff = U0("README.md", "@@ -1 +1 @@", "-a", "+b")
                 + U0("src/App/Missing.cs", "@@ -1 +1 @@", "-a", "+b")
                 + "diff --git a/src/App/Gone.cs b/src/App/Gone.cs\ndeleted file mode 100644\n--- a/src/App/Gone.cs\n+++ /dev/null\n@@ -1 +0,0 @@\n-a\n"
                 + "diff --git a/src/Old.cs b/src/App/Order.cs\nsimilarity index 100%\nrename from src/Old.cs\nrename to src/App/Order.cs\n"
                 + U0("src/Api/Api.cs", "@@ -6 +6 @@", "-  ", "+	");
        var a = r.Run(diff);
        Assert.Empty(a.Symbols);
        var byPath = a.Files.ToDictionary(f => f.Path);
        Assert.All(a.Files, f => Assert.Equal("skipped", f.Analysis));
        Assert.Contains(L("非源码", "Not a source file"), byPath["README.md"].Note);
        Assert.Contains(L("源码文件不在索引里", "Source file is not in the index"), byPath["src/App/Missing.cs"].Note);
        Assert.Equal("deleted", byPath["src/App/Gone.cs"].Status);
        Assert.Contains(L("已删除", "Deleted"), byPath["src/App/Gone.cs"].Note);
        Assert.Equal(("renamed", "src/Old.cs"), (byPath["src/App/Order.cs"].Status, byPath["src/App/Order.cs"].OldPath));
        Assert.Contains(L("只改名", "Rename only"), byPath["src/App/Order.cs"].Note);
        Assert.Contains(L("只有空白", "Whitespace-only"), byPath["src/Api/Api.cs"].Note);
    }

    [Fact]
    public void FileList_IsFileLevel_NewFileToo()
    {
        var (r, _, _, _, _, placeWorks, cancelWorks) = Shop();
        using var _r = r;
        var a = r.Run("A\tsrc/App/Order.cs\n");
        Assert.Equal(("added", "file"), (a.Files[0].Status, a.Files[0].Analysis));
        Assert.Equal("Order:file | Order.Place:file | Order.Cancel:file", Seeds(a));
        Assert.Equal([placeWorks, cancelWorks], a.Tests.Select(t => t.Symbol.Id));
    }

    [Fact]
    public void Filter_ReplacesDefaultTestDetection()
    {
        var (r, _, place, _, _, _, _) = Shop();
        using var _r = r;
        var f = r.File("src/App/OrderCheck.cs");
        var check = r.Sym(f, SymbolKind.Method, "Run", 2, 5, r.Sym(f, SymbolKind.Class, "OrderCheck", 1, 10));
        r.Edge(check, place);
        var a = r.Run(U0("src/App/Order.cs", "@@ -7 +7 @@", "-a", "+b"), new AffectedOptions(TestGlobs: ["*Check.cs"]));
        Assert.Equal([check], a.Tests.Select(t => t.Symbol.Id)); // OrderTests 不再算测试（它成了普通调用方）
        Assert.Equal(["src/App/OrderCheck.cs"], a.TestFiles);
    }

    [Fact]
    public void Depth_LimitsWalk_AndMarksTruncated()
    {
        var (r, _, place, _, _, _, _) = Shop();
        using var _r = r;
        var f = r.File("src/App/Chain.cs");
        var t = r.Sym(f, SymbolKind.Class, "Chain", 1, 100);
        var prev = place;
        for (var i = 0; i < 3; i++)
        {
            var m = r.Sym(f, SymbolKind.Method, "M" + i, 2 + i * 10, 8 + i * 10, t);
            r.Edge(m, prev);
            prev = m;
        }
        var tf = r.File("tests/App.Tests/ChainTests.cs");
        var deep = r.Sym(tf, SymbolKind.Method, "Deep", 2, 4, r.Sym(tf, SymbolKind.Class, "ChainTests", 1, 10));
        r.Edge(deep, prev); // 深度 4

        var full = r.Run(U0("src/App/Order.cs", "@@ -7 +7 @@", "-a", "+b"));
        Assert.Contains(full.Tests, x => x.Symbol.Id == deep && x.Depth == 4);
        Assert.False(full.Truncated);
        var shallow = r.Run(U0("src/App/Order.cs", "@@ -7 +7 @@", "-a", "+b"), new AffectedOptions(Depth: 3));
        Assert.DoesNotContain(shallow.Tests, x => x.Symbol.Id == deep);
        Assert.True(shallow.Truncated);
        Assert.True(shallow.Symbols[0].Truncated);
        Assert.NotEmpty(shallow.Warnings);
    }
}
