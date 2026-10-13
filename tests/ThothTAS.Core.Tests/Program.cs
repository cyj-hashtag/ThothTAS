using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using ThothTAS.Core;

internal static class Program
{
    private static int passed;
    private static int failed;
    private static readonly HashSet<string> Starts = new HashSet<string>(StringComparer.Ordinal) { "Basic-1", "Basic-2" };

    private static int Main()
    {
        Test("canonical four-segment sample", delegate
        {
            TasScript script = ScriptParser.Parse("Version,1\nStart,Basic-1\nSeed,1\n\n60, M=45, A=270\n60, M=45, A=(30,40)\n30, N\n60, M=0.5:0, A=1:0", Starts);
            Equal(script.Version, 1);
            Equal(script.Start, "Basic-1");
            Equal(script.Seed, 1);
            Equal(script.TotalFrames, 210);
            Equal(script.Segments.Count, 4);
            Near(script.Segments[0].Input.MoveX, Math.Sqrt(0.5));
            Near(script.Segments[0].Input.MoveY, Math.Sqrt(0.5));
            Near(script.Segments[0].Input.AimX, 0);
            Near(script.Segments[0].Input.AimY, -1);
            Equal(script.Segments[1].Input.AimKind, AimKind.WorldTarget);
            Near(script.Segments[1].Input.AimX, 30);
            Near(script.Segments[1].Input.AimY, 40);
            Equal(script.Segments[2].Input.AimKind, AimKind.None);
            Near(script.Segments[3].Input.MoveX, 0.5);
        });
        Test("default seed and omitted inputs reset to zero", delegate
        {
            TasScript script = ParseSegments("1, M=90, A=180\n1\n1, N,A=0:0");
            Equal(script.Seed, 1);
            TasFrameInput input = script.Segments[1].Input;
            Near(input.MoveX, 0);
            Near(input.MoveY, 0);
            Equal(input.AimKind, AimKind.None);
            Near(script.Segments[2].Input.AimX, 0);
            Near(script.Segments[2].Input.AimY, 0);
        });
        Test("BOM CRLF whitespace and full-line comments", delegate
        {
            TasScript script = ScriptParser.Parse("\uFEFF# 示例\r\n\r\n Version , 1 \r\n Start , Basic-1 \r\n # input\r\n 1 , M= -90 , A=( -12.5 , 3.25 ) \r\n", Starts);
            Equal(script.Segments[0].SourceLine, 6);
            Near(script.Segments[0].Input.MoveX, 0);
            Near(script.Segments[0].Input.MoveY, -1);
            Near(script.Segments[0].Input.AimX, -12.5);
            Near(script.Segments[0].Input.AimY, 3.25);
        });
        Test("axis angles and angle normalization", delegate
        {
            string[] values = { "0", "90", "180", "270", "360", "-90", "450", "-450", "720.0" };
            double[] xs = { 1, 0, -1, 0, 1, 0, 0, 0, 1 };
            double[] ys = { 0, 1, 0, -1, 0, -1, 1, -1, 0 };
            for (int i = 0; i < values.Length; i++)
            {
                TasFrameInput input = ParseSegments("1,M=" + values[i] + ",A=" + values[i]).Segments[0].Input;
                Near(input.MoveX, xs[i]);
                Near(input.MoveY, ys[i]);
                Near(input.AimX, xs[i]);
                Near(input.AimY, ys[i]);
            }
        });
        Test("decimal and very large finite angles", delegate
        {
            TasFrameInput input = ParseSegments("1,M=22.5,A=1e308").Segments[0].Input;
            Near(input.MoveX, Math.Cos(Math.PI / 8));
            Near(input.MoveY, Math.Sin(Math.PI / 8));
            Near(input.AimX * input.AimX + input.AimY * input.AimY, 1);
        });
        Test("analog magnitude is preserved below one", delegate
        {
            TasFrameInput input = ParseSegments("1,M=0.25:-0.5,A=-0.125:0.375").Segments[0].Input;
            Near(input.MoveX, 0.25);
            Near(input.MoveY, -0.5);
            Near(input.AimX, -0.125);
            Near(input.AimY, 0.375);
        });
        Test("vectors above one are normalized", delegate
        {
            TasFrameInput input = ParseSegments("1,M=3:4,A=-30:40").Segments[0].Input;
            Near(input.MoveX, 0.6);
            Near(input.MoveY, 0.8);
            Near(input.AimX, -0.6);
            Near(input.AimY, 0.8);
        });
        Test("large finite vectors normalize without overflow", delegate
        {
            TasFrameInput input = ParseSegments("1,M=1e308:1e308,A=-1e308:0").Segments[0].Input;
            Near(input.MoveX, Math.Sqrt(0.5));
            Near(input.MoveY, Math.Sqrt(0.5));
            Near(input.AimX, -1);
            Near(input.AimY, 0);
        });
        Test("negative zero and tiny vectors stay finite", delegate
        {
            TasFrameInput input = ParseSegments("1,M=-0:0,A=1e-300:-1e-300").Segments[0].Input;
            Near(input.MoveX, 0);
            True(input.AimX > 0 && input.AimY < 0);
            double x, y;
            input.ResolveAim(10, 20, out x, out y);
            Equal(x, input.AimX);
            Equal(y, input.AimY);
        });
        Test("all direction abbreviations and diagonals", delegate
        {
            string[] values = { "U", "D", "L", "R", "UR", "UL", "DR", "DL", "U,R", "R,U" };
            double d = Math.Sqrt(0.5);
            double[] xs = { 0, 0, -1, 1, d, -d, d, -d, d, d };
            double[] ys = { 1, -1, 0, 0, d, d, -d, -d, d, d };
            for (int i = 0; i < values.Length; i++)
            {
                TasFrameInput input = ParseSegments("1," + values[i] + ",A=0").Segments[0].Input;
                Near(input.MoveX, xs[i]);
                Near(input.MoveY, ys[i]);
            }
        });
        Test("world target resolves from each current firing position", delegate
        {
            TasFrameInput input = ParseSegments("1,M=45,A=(30,40)").Segments[0].Input;
            double x, y;
            input.ResolveAim(0, 0, out x, out y);
            Near(x, 0.6); Near(y, 0.8);
            input.ResolveAim(30, 30, out x, out y);
            Near(x, 0); Near(y, 1);
            input.ResolveAim(40, 40, out x, out y);
            Near(x, -1); Near(y, 0);
            Near(input.AimX, 30); Near(input.AimY, 40);
        });
        Test("coincident and very close world target", delegate
        {
            TasFrameInput input = ParseSegments("1,A=(-12.5,3.25)").Segments[0].Input;
            double x, y;
            input.ResolveAim(-12.5, 3.25, out x, out y);
            Near(x, 0); Near(y, 0);
            input.ResolveAim(-12.5, 3.249999999, out x, out y);
            Near(x, 0); Near(y, 1);
        });
        Test("None resolves to zero", delegate
        {
            double x, y;
            ParseSegments("1,N").Segments[0].Input.ResolveAim(20, -30, out x, out y);
            Near(x, 0); Near(y, 0);
        });
        Test("finite extreme world positions resolve without subtraction overflow", delegate
        {
            double x, y;
            TasFrameInput input = ParseSegments("1,A=(1e308,-1e308)").Segments[0].Input;
            input.ResolveAim(-1e308, 1e308, out x, out y);
            Near(x, Math.Sqrt(0.5)); Near(y, -Math.Sqrt(0.5));
        });
        Test("nonfinite runtime firing positions fail explicitly", delegate
        {
            double x, y;
            TasFrameInput input = ParseSegments("1,A=(0,0)").Segments[0].Input;
            Throws<ArgumentOutOfRangeException>(delegate { input.ResolveAim(double.NaN, 0, out x, out y); });
            Throws<ArgumentOutOfRangeException>(delegate { input.ResolveAim(0, double.PositiveInfinity, out x, out y); });
        });
        Test("seed signed 32-bit boundaries", delegate
        {
            Equal(ScriptParser.Parse("Version,1\nStart,Basic-1\nSeed,-2147483648\n1,N", Starts).Seed, int.MinValue);
            Equal(ScriptParser.Parse("Version,1\nSeed,+2147483647\nStart,Basic-2\n1,N", Starts).Seed, int.MaxValue);
        });
        Test("zero-based frame index and source lines", delegate
        {
            TasScript script = ParseSegments("2,U\n3,D\n1,N");
            int sourceLine;
            Near(script.GetInputAtFrame(0, out sourceLine).MoveY, 1); Equal(sourceLine, 3);
            Near(script.GetInputAtFrame(1, out sourceLine).MoveY, 1); Equal(sourceLine, 3);
            Near(script.GetInputAtFrame(2, out sourceLine).MoveY, -1); Equal(sourceLine, 4);
            Near(script.GetInputAtFrame(4, out sourceLine).MoveY, -1); Equal(sourceLine, 4);
            Near(script.GetInputAtFrame(5, out sourceLine).MoveY, 0); Equal(sourceLine, 5);
            Throws<ArgumentOutOfRangeException>(delegate { script.GetInputAtFrame(-1, out sourceLine); });
            Throws<ArgumentOutOfRangeException>(delegate { script.GetInputAtFrame(6, out sourceLine); });
        });
        Test("maximum total frame index", delegate
        {
            TasScript script = ParseSegments("2147483647,R");
            int sourceLine;
            Equal(script.TotalFrames, int.MaxValue);
            Near(script.GetInputAtFrame(int.MaxValue - 1, out sourceLine).MoveX, 1);
        });
        Test("segment collection is immutable", delegate
        {
            TasScript script = ParseSegments("1,N");
            Throws<NotSupportedException>(delegate { ((IList<TasSegment>)script.Segments).Clear(); });
            Equal(script.Segments.Count, 1);
        });
        Test("parser uses invariant numeric culture", delegate
        {
            CultureInfo old = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
                TasFrameInput input = ParseSegments("1,M=0.25:0,A=(-3.5,4.25)").Segments[0].Input;
                Near(input.MoveX, 0.25);
                Near(input.AimX, -3.5);
                Near(input.AimY, 4.25);
            }
            finally { Thread.CurrentThread.CurrentCulture = old; }
        });
        Test("caller comparer cannot change level case rules", delegate
        {
            HashSet<string> insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Basic-1" };
            Throws<ScriptParseException>(delegate { ScriptParser.Parse("Version,1\nStart,basic-1\n1,N", insensitive); });
        });
        Test("null arguments", delegate
        {
            Throws<ArgumentNullException>(delegate { ScriptParser.Parse(null, Starts); });
            Throws<ArgumentNullException>(delegate { ScriptParser.Parse("Version,1", null); });
        });

        Invalid("empty document", "", 1);
        Invalid("missing Version", "Start,Basic-1\n1,N", 1);
        Invalid("unsupported Version", "Version,2\nStart,Basic-1\n1,N", 1);
        Invalid("duplicate Version", "Version,1\nVersion,1\nStart,Basic-1\n1,N", 2);
        Invalid("missing Start", "Version,1", 1);
        Invalid("missing input", "Version,1\nStart,Basic-1", 2);
        Invalid("input before Start", "Version,1\n1,N\nStart,Basic-1", 2);
        Invalid("unknown start", "Version,1\nStart,Basic-999\n1,N", 2);
        Invalid("duplicate Start", "Version,1\nStart,Basic-1\nStart,Basic-2\n1,N", 3);
        Invalid("duplicate Seed", "Version,1\nStart,Basic-1\nSeed,1\nSeed,2\n1,N", 4);
        Invalid("late header", "Version,1\nStart,Basic-1\n1,N\nSeed,2", 4);
        Invalid("extra header field", "Version,1,2\nStart,Basic-1\n1,N", 1);
        Invalid("empty header value", "Version,1\nStart,\n1,N", 2);
        Invalid("non-integer seed", "Version,1\nStart,Basic-1\nSeed,1.5\n1,N", 3);
        Invalid("overflow seed", "Version,1\nStart,Basic-1\nSeed,2147483648\n1,N", 3);
        InvalidSegment("zero frames", "0,N");
        InvalidSegment("negative frames", "-1,N");
        InvalidSegment("decimal frames", "1.5,N");
        InvalidSegment("overflow frames", "2147483648,N");
        Invalid("overflow total frames", "Version,1\nStart,Basic-1\n2147483647,N\n1,N", 4);
        InvalidSegment("duplicate M", "1,M=0,M=90");
        InvalidSegment("duplicate A", "1,A=0,A=(1,2)");
        InvalidSegment("duplicate N", "1,N,N");
        InvalidSegment("duplicate direction token", "1,U,U");
        InvalidSegment("duplicate direction combined", "1,UU");
        InvalidSegment("opposite direction vertical", "1,U,D");
        InvalidSegment("opposite direction horizontal", "1,RL");
        InvalidSegment("M direction conflict", "1,M=0,U");
        InvalidSegment("direction M conflict", "1,UR,M=0");
        InvalidSegment("M N conflict", "1,M=0,N");
        InvalidSegment("N M conflict", "1,N,M=0");
        InvalidSegment("N direction conflict", "1,N,L");
        InvalidSegment("direction N conflict", "1,L,N");
        InvalidSegment("unknown input", "1,Q");
        InvalidSegment("unknown key", "1,B=90");
        InvalidSegment("lowercase input", "1,u");
        InvalidSegment("empty input field", "1,N,");
        InvalidSegment("NaN angle", "1,M=NaN");
        InvalidSegment("Infinity angle", "1,A=Infinity");
        InvalidSegment("overflow decimal", "1,M=1e309");
        InvalidSegment("NaN vector", "1,M=NaN:1");
        InvalidSegment("infinite world target", "1,A=(1,Infinity)");
        InvalidSegment("missing vector component", "1,M=1:");
        InvalidSegment("extra vector component", "1,A=1:2:3");
        InvalidSegment("empty angle", "1,A=");
        InvalidSegment("invalid target only one coordinate", "1,A=(1)");
        InvalidSegment("invalid target three coordinates", "1,A=(1,2,3)");
        InvalidSegment("missing right parenthesis", "1,A=(1,2");
        InvalidSegment("missing left parenthesis", "1,A=1,2)");
        InvalidSegment("nested target parenthesis", "1,A=((1,2))");
        InvalidSegment("target trailing characters", "1,A=(1,2)x");
        InvalidSegment("target movement syntax rejected", "1,M=(1,2)");
        Test("errors expose accurate token column", delegate
        {
            try { ParseSegments("  1, U, M=90"); }
            catch (ScriptParseException exception)
            {
                Equal(exception.Line, 3); Equal(exception.Column, 9);
                True(exception.Message.Contains("第 3 行"));
                True(exception.Reason.Length > 0);
                return;
            }
            throw new Exception("Expected parse failure.");
        });

        ChapterTests.Run(Test);
        RulesTests.Run(Test);
        Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed.");
        return failed == 0 ? 0 : 1;
    }

    private static TasScript ParseSegments(string input)
    {
        return ScriptParser.Parse("Version,1\nStart,Basic-1\n" + input, Starts);
    }

    private static void InvalidSegment(string name, string input)
    {
        Invalid(name, "Version,1\nStart,Basic-1\n" + input, 3);
    }

    private static void Invalid(string name, string script, int line)
    {
        Test(name, delegate
        {
            try { ScriptParser.Parse(script, Starts); }
            catch (ScriptParseException exception)
            {
                Equal(exception.Line, line);
                True(exception.Column >= 1);
                return;
            }
            throw new Exception("Expected parse failure.");
        });
    }

    private static void Test(string name, Action body)
    {
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception exception) { failed++; Console.WriteLine("FAIL " + name + ": " + exception.Message); }
    }

    private static void Equal<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new Exception("Expected " + expected + ", got " + actual + ".");
    }

    private static void Near(double actual, double expected)
    {
        if (double.IsNaN(actual) || double.IsInfinity(actual) || Math.Abs(actual - expected) > 1e-12)
            throw new Exception("Expected " + expected + ", got " + actual + ".");
    }

    private static void True(bool value)
    {
        if (!value) throw new Exception("Assertion failed.");
    }

    private static void Throws<T>(Action body) where T : Exception
    {
        try { body(); }
        catch (T) { return; }
        throw new Exception("Expected exception " + typeof(T).Name + ".");
    }
}
