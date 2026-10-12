using System;
using System.Collections.Generic;
using ThothTAS.Core;

internal static class ChapterTests
{
    private static readonly string[] Order = { "Basic-1", "Basic-2", "Basic-3", "Basic-4", "Lasers-1", "Teleporters-4", "Teleporters-5", "zapper-1" };
    private static readonly HashSet<string> Starts = new HashSet<string>(Order, StringComparer.Ordinal);

    public static void Run(Action<string, Action> test)
    {
        test("legacy script exposes an implicit chapter without changing flat frames", delegate
        {
            TasScript script = Parse("Version,1\nStart,Basic-1\n2,U\n3,D");
            Equal(script.HasExplicitRooms, false);
            Equal(script.Chapters.Count, 1);
            TasChapter chapter = script.Chapters[0];
            Equal(chapter.Index, 0); Equal(chapter.Scene, "Basic-1"); Equal(chapter.SourceLine, 2);
            Equal(chapter.SourceOrigin, null); Equal(chapter.TotalFrames, 5);
            Equal(chapter.Segments[1].StartFrame, 2);
            int line;
            Equal(chapter.GetInputAtFrame(4, out line), script.GetInputAtFrame(4, out line));
            Equal(line, 4);
        });
        test("explicit chapters have local frames and nominal flattened segments", delegate
        {
            TasScript script = Parse("Version,1\nStart,Basic-1\nSeed,7\nRoom,Basic-1\n2,U\n3,D\nRoom,Basic-2\n4,M=0.5:0,A=(-12.5,3.25)\n1,N");
            Equal(script.HasExplicitRooms, true); Equal(script.Seed, 7); Equal(script.TotalFrames, 10);
            Equal(script.Chapters.Count, 2); Equal(script.Segments.Count, 4);
            Equal(script.Chapters[0].TotalFrames, 5); Equal(script.Chapters[1].TotalFrames, 5);
            Equal(script.Chapters[1].Index, 1); Equal(script.Chapters[1].Scene, "Basic-2");
            Equal(script.Chapters[1].SourceLine, 7); Equal(script.Chapters[1].Segments[0].StartFrame, 0);
            Equal(script.Chapters[1].Segments[1].StartFrame, 4); Equal(script.Segments[2].StartFrame, 5);
            int line;
            TasFrameInput input = script.GetInputAtChapterFrame(1, 0, out line);
            Equal(line, 8); Equal(input.MoveX, 0.5); Equal(input.AimKind, AimKind.WorldTarget);
            Equal(input, script.GetInputAtFrame(5, out line));
            Equal(script.GetInputAtChapterFrame(1, 4, out line).AimKind, AimKind.None); Equal(line, 9);
        });
        test("one explicit Room is distinguishable from legacy behavior", delegate
        {
            TasScript script = Parse("Version,1\nStart,Basic-1\nRoom,Basic-1\n1,N");
            Equal(script.HasExplicitRooms, true); Equal(script.Chapters.Count, 1);
        });
        test("Source comments annotate chapters and are consumed once", delegate
        {
            TasScript script = Parse("Version,1\nStart,Basic-1\n# Source: first.tas (input lines 4-6)\nRoom,Basic-1\n1,N\nRoom,Basic-2\n1,N");
            Equal(script.Chapters[0].SourceOrigin, "first.tas (input lines 4-6)");
            Equal(script.Chapters[1].SourceOrigin, null);
        });
        test("chapter and segment collections cannot be mutated", delegate
        {
            TasScript script = Parse("Version,1\nStart,Basic-1\nRoom,Basic-1\n1,N");
            Throws<NotSupportedException>(delegate { ((IList<TasChapter>)script.Chapters).Clear(); });
            Throws<NotSupportedException>(delegate { ((IList<TasSegment>)script.Chapters[0].Segments).Clear(); });
        });
        test("chapter lookup rejects invalid local frames and indices", delegate
        {
            TasScript script = Parse("Version,1\nStart,Basic-1\nRoom,Basic-1\n2,N\nRoom,Basic-2\n1,R");
            int line;
            Throws<ArgumentOutOfRangeException>(delegate { script.GetInputAtChapterFrame(-1, 0, out line); });
            Throws<ArgumentOutOfRangeException>(delegate { script.GetInputAtChapterFrame(2, 0, out line); });
            Throws<ArgumentOutOfRangeException>(delegate { script.GetInputAtChapterFrame(1, -1, out line); });
            Throws<ArgumentOutOfRangeException>(delegate { script.GetInputAtChapterFrame(1, 1, out line); });
            Throws<ArgumentOutOfRangeException>(delegate { script.Chapters[0].GetInputAtFrame(2, out line); });
        });
        test("explicit chapter maximum input budget stays indexable", delegate
        {
            TasScript script = Parse("Version,1\nStart,Basic-1\nRoom,Basic-1\n2147483647,R");
            int line;
            Equal(script.Chapters[0].GetInputAtFrame(int.MaxValue - 1, out line).MoveX, 1.0);
        });
        test("parser checks known scenes while composition checks natural adjacency", delegate
        {
            TasScript script = Parse("Version,1\nStart,Basic-1\nRoom,Basic-1\n1,N\nRoom,Basic-3\n1,N");
            Equal(script.Chapters[1].Scene, "Basic-3");
        });
        Invalid(test, "Room before Start", "Version,1\nRoom,Basic-1\n1,N", 2);
        Invalid(test, "first Room must match Start", "Version,1\nStart,Basic-1\nRoom,Basic-2\n1,N", 3);
        Invalid(test, "unknown Room", "Version,1\nStart,Basic-1\nRoom,Basic-999\n1,N", 3);
        Invalid(test, "duplicate scene chapter", "Version,1\nStart,Basic-1\nRoom,Basic-1\n1,N\nRoom,Basic-1\n1,N", 5);
        Invalid(test, "empty first chapter", "Version,1\nStart,Basic-1\nRoom,Basic-1\nRoom,Basic-2\n1,N", 3);
        Invalid(test, "empty last chapter", "Version,1\nStart,Basic-1\nRoom,Basic-1\n1,N\nRoom,Basic-2", 5);
        Invalid(test, "explicit script without inputs", "Version,1\nStart,Basic-1\nRoom,Basic-1", 3);
        Invalid(test, "extra Room field", "Version,1\nStart,Basic-1\nRoom,Basic-1,N\n1,N", 3);
        Invalid(test, "Room scene identifier is case sensitive", "Version,1\nStart,Basic-1\nRoom,basic-1\n1,N", 3);
        Invalid(test, "header after Room", "Version,1\nStart,Basic-1\nRoom,Basic-1\nSeed,2\n1,N", 4);
        Invalid(test, "mixed implicit input before Room", "Version,1\nStart,Basic-1\n1,N\nRoom,Basic-1\n1,N", 4);
        Invalid(test, "cross chapter total input overflow", "Version,1\nStart,Basic-1\nRoom,Basic-1\n2147483647,N\nRoom,Basic-2\n1,N", 6);

        test("composition preserves raw inputs source provenance and original strings", delegate
        {
            ScriptSource first = new ScriptSource("甲.tas", "Version,1\nStart,Basic-1\nSeed,1\n\n# original comment\n2, M=45, A=(-12.5,3.25)\n# keep between inputs\n1,N");
            ScriptSource second = Source("乙.tas", "Basic-2", "4,R,A=0:0");
            string firstBefore = first.Text;
            string composed = ScriptComposer.Compose(new ScriptSource[] { first, second }, Order);
            TasScript script = Parse(composed);
            Equal(script.Start, "Basic-1"); Equal(script.Seed, 1); Equal(script.TotalFrames, 7);
            Equal(script.HasExplicitRooms, true); Equal(script.Chapters.Count, 2);
            True(composed.Contains("2, M=45, A=(-12.5,3.25)\n# keep between inputs\n1,N"));
            Equal(script.Chapters[0].SourceOrigin, "甲.tas (input lines 6-8)");
            Equal(first.Text, firstBefore);
            int line;
            script.Chapters[0].GetInputAtFrame(0, out line);
            Equal(line, 7); Equal(composed.Split('\n')[line - 1], "2, M=45, A=(-12.5,3.25)");
        });
        test("composition accepts family boundary Basic4 to Lasers1", delegate
        {
            TasScript script = Parse(ScriptComposer.Compose(new ScriptSource[] { Source("a", "Basic-4", "1,N"), Source("b", "Lasers-1", "1,N") }, Order));
            Equal(script.Chapters[1].Scene, "Lasers-1");
        });
        test("composition skips extra Teleporters5 in natural order", delegate
        {
            TasScript script = Parse(ScriptComposer.Compose(new ScriptSource[] { Source("a", "Teleporters-4", "1,N"), Source("b", "zapper-1", "1,N") }, Order));
            Equal(script.Chapters[1].Scene, "zapper-1");
        });
        test("one input is converted to an explicit completion chapter", delegate
        {
            TasScript script = Parse(ScriptComposer.Compose(new ScriptSource[] { Source("a", "Basic-2", "2,N") }, Order));
            Equal(script.Start, "Basic-2"); Equal(script.HasExplicitRooms, true);
        });
        test("existing chapter scripts can be composed with subsequent sources", delegate
        {
            ScriptSource first = new ScriptSource("chapters", "Version,1\nStart,Basic-1\nRoom,Basic-1\n2,U\nRoom,Basic-2\n3,D");
            TasScript script = Parse(ScriptComposer.Compose(new ScriptSource[] { first, Source("third", "Basic-3", "4,N") }, Order));
            Equal(script.Chapters.Count, 3); Equal(script.TotalFrames, 9);
            Equal(script.Chapters[2].Segments[0].StartFrame, 0);
        });
        test("default seed and explicit Seed1 agree", delegate
        {
            ScriptSource second = new ScriptSource("b", "Version,1\nStart,Basic-2\nSeed,1\n1,N");
            Equal(Parse(ScriptComposer.Compose(new ScriptSource[] { Source("a", "Basic-1", "1,N"), second }, Order)).Seed, 1);
        });
        test("negative seed remains one process seed", delegate
        {
            ScriptSource a = new ScriptSource("a", "Version,1\nStart,Basic-1\nSeed,-12\n1,N");
            ScriptSource b = new ScriptSource("b", "Version,1\nStart,Basic-2\nSeed,-12\n1,N");
            Equal(Parse(ScriptComposer.Compose(new ScriptSource[] { a, b }, Order)).Seed, -12);
        });
        test("composition reports source parse error at original line column", delegate
        {
            ScriptComposeException exception = ComposeFailure(new ScriptSource[] { new ScriptSource("broken.tas", "Version,1\nStart,Basic-1\n  1, U, M=90") });
            Equal(exception.SourceName, "broken.tas"); Equal(exception.Line, 3); Equal(exception.Column, 9);
            True(exception.InnerException is ScriptParseException); True(exception.Message.Contains("broken.tas"));
        });
        test("different seed refuses composition at source Seed header", delegate
        {
            ScriptComposeException exception = ComposeFailure(new ScriptSource[] { Source("first", "Basic-1", "1,N"), new ScriptSource("second", "Version,1\nStart,Basic-2\nSeed,2\n1,N") });
            Equal(exception.SourceName, "second"); Equal(exception.Line, 3); True(exception.Reason.Contains("Seed"));
        });
        test("default seed mismatch identifies Start when Seed is omitted", delegate
        {
            ScriptComposeException exception = ComposeFailure(new ScriptSource[] { new ScriptSource("first", "Version,1\nStart,Basic-1\nSeed,2\n1,N"), Source("second", "Basic-2", "1,N") });
            Equal(exception.SourceName, "second"); Equal(exception.Line, 2);
        });
        test("source selection order is respected and reverse order is rejected", delegate
        {
            ScriptComposeException exception = ComposeFailure(new ScriptSource[] { Source("second", "Basic-2", "1,N"), Source("first", "Basic-1", "1,N") });
            Equal(exception.SourceName, "first"); Equal(exception.Line, 2); True(exception.Reason.Contains("Basic-3"));
        });
        test("duplicate chapter across files is rejected", delegate
        {
            ScriptComposeException exception = ComposeFailure(new ScriptSource[] { Source("a", "Basic-1", "1,N"), Source("b", "Basic-1", "1,N") });
            Equal(exception.SourceName, "b"); True(exception.Reason.Contains("重复"));
        });
        test("nonadjacent chapter is rejected with expected next room", delegate
        {
            ScriptComposeException exception = ComposeFailure(new ScriptSource[] { Source("a", "Basic-1", "1,N"), Source("b", "Basic-3", "1,N") });
            True(exception.Reason.Contains("Basic-2"));
        });
        test("extra Teleporters5 cannot be composed even alone", delegate
        {
            ScriptComposeException exception = ComposeFailure(new ScriptSource[] { Source("extra", "Teleporters-5", "1,N") });
            True(exception.Reason.Contains("Teleporters-6"));
        });
        test("combined input budget overflow is rejected at original input line", delegate
        {
            ScriptComposeException exception = ComposeFailure(new ScriptSource[] { Source("a", "Basic-1", "2147483647,N"), Source("b", "Basic-2", "1,N") });
            Equal(exception.SourceName, "b"); Equal(exception.Line, 3); True(exception.Reason.Contains("预算"));
        });
        test("source filename line breaks cannot inject a Room directive", delegate
        {
            TasScript script = Parse(ScriptComposer.Compose(new ScriptSource[] { Source("a\nRoom,Basic-3\r\n", "Basic-1", "1,N") }, Order));
            Equal(script.Chapters.Count, 1); True(script.Chapters[0].SourceOrigin.Contains("Room,Basic-3"));
        });
        test("composition rejects missing or invalid caller arguments", delegate
        {
            Throws<ArgumentNullException>(delegate { ScriptComposer.Compose(null, Order); });
            Throws<ArgumentNullException>(delegate { ScriptComposer.Compose(new ScriptSource[0], null); });
            Throws<ArgumentException>(delegate { ScriptComposer.Compose(new ScriptSource[0], Order); });
            Throws<ArgumentException>(delegate { ScriptComposer.Compose(new ScriptSource[] { null }, Order); });
            Throws<ArgumentException>(delegate { ScriptComposer.Compose(new ScriptSource[] { Source("a", "Basic-1", "1,N") }, new string[] { "Basic-1", "Basic-1" }); });
            Throws<ArgumentException>(delegate { ScriptComposer.Compose(new ScriptSource[0], new string[] { null }); });
        });
        test("ScriptSource requires immutable named text", delegate
        {
            Throws<ArgumentNullException>(delegate { new ScriptSource(null, ""); });
            Throws<ArgumentNullException>(delegate { new ScriptSource("a", null); });
            Throws<ArgumentException>(delegate { new ScriptSource(" ", ""); });
        });
    }

    private static TasScript Parse(string text) { return ScriptParser.Parse(text, Starts); }
    private static ScriptSource Source(string name, string scene, string input) { return new ScriptSource(name, "Version,1\nStart," + scene + "\n" + input); }
    private static ScriptComposeException ComposeFailure(ScriptSource[] sources)
    {
        try { ScriptComposer.Compose(sources, Order); }
        catch (ScriptComposeException exception) { return exception; }
        throw new Exception("Expected compose failure.");
    }
    private static void Invalid(Action<string, Action> test, string name, string text, int line)
    {
        test(name, delegate
        {
            try { Parse(text); }
            catch (ScriptParseException exception) { Equal(exception.Line, line); return; }
            throw new Exception("Expected parse failure.");
        });
    }
    private static void Equal<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception("Expected " + expected + ", got " + actual + ".");
    }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Throws<T>(Action body) where T : Exception
    {
        try { body(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }
}
