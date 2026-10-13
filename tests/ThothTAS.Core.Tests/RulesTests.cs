using System;
using System.Collections.Generic;
using ThothTAS.Core;

internal static class RulesTests
{
    private static readonly string[] Order = { "Basic-1", "Basic-2", "Basic-3" };
    private static readonly HashSet<string> Starts = new HashSet<string>(Order, StringComparer.Ordinal);

    public static void Run(Action<string, Action> test)
    {
        test("omitted Rules always means Original for legacy and chapter scripts", delegate
        {
            Equal(Parse("Version,1\nStart,Basic-1\n1,N").Rules, TasRules.Original);
            Equal(Parse("Version,1\nStart,Basic-1\nSeed,9\nRoom,Basic-1\n1,N").Rules, TasRules.Original);
            Equal((int)TasRules.Original, 0);
            Equal((int)TasRules.Deterministic1, 1);
        });
        test("explicit Original and modified Deterministic-1 are distinct immutable rules", delegate
        {
            Equal(Parse("Version,1\nStart,Basic-1\nRules,Original\n1,N").Rules, TasRules.Original);
            TasScript script = Parse("Version,1\nStart,Basic-1\nRules,Deterministic-1\n1,U,A=(1,2)");
            Equal(script.Rules, TasRules.Deterministic1);
            Equal(script.Segments[0].SourceLine, 4);
            Equal(script.Segments[0].Input.AimKind, AimKind.WorldTarget);
            Equal(typeof(TasScript).GetProperty("Rules").GetSetMethod(), null);
        });
        test("Rules may appear anywhere among headers after Version", delegate
        {
            TasScript script = Parse("Version,1\nRules,Deterministic-1\nSeed,-7\nStart,Basic-1\nRoom,Basic-1\n2,N");
            Equal(script.Rules, TasRules.Deterministic1);
            Equal(script.Seed, -7);
            Equal(script.Chapters[0].SourceLine, 5);
            Equal(Parse("Version,1\nSeed,1\nStart,Basic-1\nRules,Deterministic-1\n1,N").Rules, TasRules.Deterministic1);
        });
        test("Rules header accepts BOM CRLF whitespace and comments", delegate
        {
            TasScript script = Parse("\uFEFFVersion,1\r\n# rules choice\r\n Rules , Deterministic-1 \r\n Start , Basic-1 \r\n1,N\r\n");
            Equal(script.Rules, TasRules.Deterministic1);
            Equal(script.Segments[0].SourceLine, 5);
        });
        Invalid(test, "Rules must not precede Version", "Rules,Deterministic-1\nVersion,1\nStart,Basic-1\n1,N", 1, 1, "Version");
        Invalid(test, "Rules directive is case sensitive", "Version,1\nStart,Basic-1\nrules,Deterministic-1\n1,N", 3, 1, "持续帧数");
        Invalid(test, "Rules value is case sensitive", "Version,1\nStart,Basic-1\n  Rules, deterministic-1\n1,N", 3, 10, "未知规则");
        Invalid(test, "unknown Rules value is rejected rather than silently original", "Version,1\nStart,Basic-1\nRules,Deterministic-2\n1,N", 3, 7, "未知规则");
        Invalid(test, "enum spelling is not the text grammar", "Version,1\nStart,Basic-1\nRules,Deterministic1\n1,N", 3, 7, "未知规则");
        Invalid(test, "numeric enum value is not a Rules name", "Version,1\nStart,Basic-1\nRules,1\n1,N", 3, 7, "未知规则");
        Invalid(test, "Rules without a value is rejected", "Version,1\nStart,Basic-1\nRules\n1,N", 3, 1, "两个字段");
        Invalid(test, "empty Rules value is rejected", "Version,1\nStart,Basic-1\nRules,\n1,N", 3, 7, "字段不能为空");
        Invalid(test, "extra Rules field is rejected", "Version,1\nStart,Basic-1\nRules,Original,Deterministic-1\n1,N", 3, 1, "两个字段");
        Invalid(test, "duplicate identical Rules header is rejected", "Version,1\nStart,Basic-1\nRules,Original\nRules,Original\n1,N", 4, 1, "重复的 Rules");
        Invalid(test, "duplicate differing Rules header is rejected", "Version,1\nStart,Basic-1\nRules,Original\n  Rules,Deterministic-1\n1,N", 4, 3, "重复的 Rules");
        Invalid(test, "Rules after input is rejected", "Version,1\nStart,Basic-1\n1,N\nRules,Deterministic-1", 4, 1, "头部指令");
        Invalid(test, "Rules after Room is rejected even before any input", "Version,1\nStart,Basic-1\nRoom,Basic-1\nRules,Deterministic-1\n1,N", 4, 1, "头部指令");

        test("modified composition keeps its explicit Rules and unchanged raw inputs", delegate
        {
            ScriptSource first = Source("first.tas", "Basic-1", "Deterministic-1", "2, U, A=(1,2)\n# between inputs\n1,N");
            ScriptSource second = Source("second.tas", "Basic-2", "Deterministic-1", "4,R,A=0:0");
            string originalFirst = first.Text;
            string composed = ScriptComposer.Compose(new ScriptSource[] { first, second }, Order);
            TasScript script = Parse(composed);
            Equal(script.Rules, TasRules.Deterministic1);
            Equal(script.TotalFrames, 7);
            Equal(script.Chapters.Count, 2);
            Equal(script.Chapters[0].SourceOrigin, "first.tas (input lines 4-6)");
            True(composed.StartsWith("Version,1\nStart,Basic-1\nSeed,1\nRules,Deterministic-1\n", StringComparison.Ordinal));
            Equal(Count(composed, "Rules,Deterministic-1"), 1);
            True(composed.Contains("2, U, A=(1,2)\n# between inputs\n1,N"));
            Equal(first.Text, originalFirst);
            int line;
            script.GetInputAtChapterFrame(1, 0, out line);
            Equal(composed.Split('\n')[line - 1], "4,R,A=0:0");
        });
        test("one modified source remains modified after composition", delegate
        {
            string composed = ScriptComposer.Compose(new ScriptSource[] { Source("only", "Basic-1", "Deterministic-1", "2,N") }, Order);
            Equal(Parse(composed).Rules, TasRules.Deterministic1);
            Equal(Parse(composed).HasExplicitRooms, true);
        });
        test("already composed modified chapters can compose with another modified source", delegate
        {
            ScriptSource chapters = new ScriptSource("chapters", "Version,1\nStart,Basic-1\nRules,Deterministic-1\nRoom,Basic-1\n2,U\nRoom,Basic-2\n3,D");
            TasScript script = Parse(ScriptComposer.Compose(new ScriptSource[] { chapters, Source("third", "Basic-3", "Deterministic-1", "4,N") }, Order));
            Equal(script.Rules, TasRules.Deterministic1);
            Equal(script.Chapters.Count, 3);
        });
        test("legacy Original composition output remains byte compatible", delegate
        {
            string composed = ScriptComposer.Compose(new ScriptSource[] { Source("a", "Basic-1", null, "1,N") }, Order);
            Equal(composed, "Version,1\nStart,Basic-1\nSeed,1\n\n# Source: a (input lines 3-3)\nRoom,Basic-1\n1,N\n");
            Equal(Parse(composed).Rules, TasRules.Original);
        });
        test("explicit Original agrees with omitted Original without a new output header", delegate
        {
            string composed = ScriptComposer.Compose(new ScriptSource[] { Source("a", "Basic-1", null, "1,N"), Source("b", "Basic-2", "Original", "1,N") }, Order);
            Equal(Parse(composed).Rules, TasRules.Original);
            True(!composed.Contains("Rules,"));
        });
        test("mixed rules point to the modified source's Rules header", delegate
        {
            ScriptComposeException exception = ComposeFailure(new ScriptSource[] { Source("original.tas", "Basic-1", null, "1,N"), Source("modified.tas", "Basic-2", "Deterministic-1", "1,N") });
            Equal(exception.SourceName, "modified.tas");
            Equal(exception.Line, 3);
            Equal(exception.Column, 1);
            True(exception.Reason.Contains("Rules"));
            True(exception.Reason.Contains("不能混合"));
        });
        test("mixed rules point to Start when the default Original header is omitted", delegate
        {
            ScriptComposeException exception = ComposeFailure(new ScriptSource[] { Source("modified.tas", "Basic-1", "Deterministic-1", "1,N"), Source("original.tas", "Basic-2", null, "1,N") });
            Equal(exception.SourceName, "original.tas");
            Equal(exception.Line, 2);
        });
        test("mixed rules point to an explicit Original Rules header", delegate
        {
            ScriptComposeException exception = ComposeFailure(new ScriptSource[] { Source("modified", "Basic-1", "Deterministic-1", "1,N"), Source("original", "Basic-2", "Original", "1,N") });
            Equal(exception.SourceName, "original");
            Equal(exception.Line, 3);
        });
        test("invalid source Rules preserves its original parse error line and column", delegate
        {
            ScriptComposeException exception = ComposeFailure(new ScriptSource[] { new ScriptSource("broken.tas", "Version,1\nStart,Basic-1\n Rules, Unknown\n1,N") });
            Equal(exception.SourceName, "broken.tas");
            Equal(exception.Line, 3);
            Equal(exception.Column, 9);
            True(exception.InnerException is ScriptParseException);
        });
        test("modified composition still checks the seed contract", delegate
        {
            ScriptSource differentSeed = new ScriptSource("b", "Version,1\nStart,Basic-2\nRules,Deterministic-1\nSeed,2\n1,N");
            ScriptComposeException exception = ComposeFailure(new ScriptSource[] { Source("a", "Basic-1", "Deterministic-1", "1,N"), differentSeed });
            Equal(exception.Line, 4);
            True(exception.Reason.Contains("Seed"));
        });
    }

    private static TasScript Parse(string text) { return ScriptParser.Parse(text, Starts); }
    private static ScriptSource Source(string name, string scene, string rules, string input)
    {
        return new ScriptSource(name, "Version,1\nStart," + scene + "\n" + (rules == null ? "" : "Rules," + rules + "\n") + input);
    }
    private static ScriptComposeException ComposeFailure(ScriptSource[] sources)
    {
        try { ScriptComposer.Compose(sources, Order); }
        catch (ScriptComposeException exception) { return exception; }
        throw new Exception("Expected compose failure.");
    }
    private static void Invalid(Action<string, Action> test, string name, string text, int line, int column, string reason)
    {
        test(name, delegate
        {
            try { Parse(text); }
            catch (ScriptParseException exception)
            {
                Equal(exception.Line, line);
                Equal(exception.Column, column);
                True(exception.Reason.Contains(reason));
                return;
            }
            throw new Exception("Expected parse failure.");
        });
    }
    private static int Count(string text, string needle)
    {
        int count = 0;
        int start = 0;
        int found;
        while ((found = text.IndexOf(needle, start, StringComparison.Ordinal)) >= 0) { count++; start = found + needle.Length; }
        return count;
    }
    private static void Equal<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception("Expected " + expected + ", got " + actual + ".");
    }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
}
