using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ThothTAS.Core
{
    /// <summary>An in-memory source; composition never writes the original file.</summary>
    public sealed class ScriptSource
    {
        public string Name { get; private set; }
        public string Text { get; private set; }

        public ScriptSource(string name, string text)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (name.Trim().Length == 0) throw new ArgumentException("来源名称不能为空。", "name");
            if (text == null) throw new ArgumentNullException("text");
            Name = name;
            Text = text;
        }
    }

    public sealed class ScriptComposeException : FormatException
    {
        public string SourceName { get; private set; }
        public int Line { get; private set; }
        public int Column { get; private set; }
        public string Reason { get; private set; }

        internal ScriptComposeException(string sourceName, int line, int column, string reason, Exception inner)
            : base(sourceName + "：第 " + line + " 行，第 " + column + " 列：" + reason, inner)
        {
            SourceName = sourceName;
            Line = line;
            Column = column;
            Reason = reason;
        }
    }

    /// <summary>Pure .NET 3.5 compatible composition with a caller-supplied natural scene order.</summary>
    public static class ScriptComposer
    {
        private sealed class ParsedSource
        {
            public ScriptSource Source;
            public TasScript Script;
            public List<string> Lines;
        }

        /// <summary>
        /// Keeps the caller's source order, unifies the seed, and emits explicit Room chapters.
        /// orderedStarts must contain the game's natural order, not an alphabetic sort.
        /// The extra Teleporters-5 resource is accepted by the parser but cannot be composed.
        /// This checks format and adjacency, and does not certify continuous game replay.
        /// </summary>
        public static string Compose(IEnumerable<ScriptSource> sources, IList<string> orderedStarts)
        {
            if (sources == null) throw new ArgumentNullException("sources");
            if (orderedStarts == null) throw new ArgumentNullException("orderedStarts");
            HashSet<string> knownStarts = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, int> naturalPositions = new Dictionary<string, int>(StringComparer.Ordinal);
            List<string> naturalOrder = new List<string>();
            for (int i = 0; i < orderedStarts.Count; i++)
            {
                string scene = orderedStarts[i];
                if (scene == null || scene.Trim().Length == 0)
                    throw new ArgumentException("关卡顺序不能包含空标识。", "orderedStarts");
                if (!knownStarts.Add(scene))
                    throw new ArgumentException("关卡顺序包含重复标识：" + scene + "。", "orderedStarts");
                if (scene == "Teleporters-5") continue;
                naturalPositions.Add(scene, naturalOrder.Count);
                naturalOrder.Add(scene);
            }

            List<ParsedSource> parsed = new List<ParsedSource>();
            foreach (ScriptSource source in sources)
            {
                if (source == null) throw new ArgumentException("来源列表不能包含空项。", "sources");
                TasScript script;
                try { script = ScriptParser.Parse(source.Text, knownStarts); }
                catch (ScriptParseException exception)
                {
                    throw new ScriptComposeException(source.Name, exception.Line, exception.Column, exception.Reason, exception);
                }
                parsed.Add(new ParsedSource { Source = source, Script = script, Lines = ReadLines(source.Text) });
            }
            if (parsed.Count == 0) throw new ArgumentException("至少需要一个输入脚本。", "sources");

            int seed = parsed[0].Script.Seed;
            string previousScene = null;
            HashSet<string> chapterScenes = new HashSet<string>(StringComparer.Ordinal);
            long totalFrames = 0;
            foreach (ParsedSource source in parsed)
            {
                if (source.Script.Seed != seed)
                    Fail(source, FindDirective(source.Lines, "Seed", "Start"), 1,
                        "输入脚本的 Seed 不一致：要求 " + seed.ToString(CultureInfo.InvariantCulture) +
                        "，实际为 " + source.Script.Seed.ToString(CultureInfo.InvariantCulture) + "。请先统一种子，再验证整段回放。");
                foreach (TasChapter chapter in source.Script.Chapters)
                {
                    if (chapter.Scene == "Teleporters-5")
                        Fail(source, chapter.SourceLine, 1, "Teleporters-5 是额外资源，出口 Teleporters-6 不存在，不能加入自然转场拼接。");
                    if (!chapterScenes.Add(chapter.Scene))
                        Fail(source, chapter.SourceLine, 1, "拼接包含重复关卡：" + chapter.Scene + "。");
                    if (previousScene != null)
                    {
                        int previousPosition = naturalPositions[previousScene];
                        if (naturalPositions[chapter.Scene] != previousPosition + 1)
                        {
                            string expected = previousPosition + 1 < naturalOrder.Count
                                ? "自然下一关为 " + naturalOrder[previousPosition + 1] + "。"
                                : "此关之后没有普通自然下一关。";
                            Fail(source, chapter.SourceLine, 1, "无法自然连接 " + previousScene + " → " + chapter.Scene + "；" + expected);
                        }
                    }
                    totalFrames += chapter.TotalFrames;
                    if (totalFrames > int.MaxValue)
                        Fail(source, chapter.Segments[0].SourceLine, 1, "拼接输入预算超出 32 位整数范围。");
                    previousScene = chapter.Scene;
                }
            }

            StringBuilder result = new StringBuilder();
            AppendLine(result, "Version,1");
            AppendLine(result, "Start," + parsed[0].Script.Start);
            AppendLine(result, "Seed," + seed.ToString(CultureInfo.InvariantCulture));
            foreach (ParsedSource source in parsed)
            {
                foreach (TasChapter chapter in source.Script.Chapters)
                {
                    int firstInputLine = chapter.Segments[0].SourceLine;
                    int lastInputLine = chapter.Segments[chapter.Segments.Count - 1].SourceLine;
                    AppendLine(result, "");
                    // Source names remain comments, even when a caller passes an embedded line break.
                    string name = source.Source.Name.Replace('\r', ' ').Replace('\n', ' ');
                    AppendLine(result, "# Source: " + name + " (input lines " + firstInputLine.ToString(CultureInfo.InvariantCulture)
                        + "-" + lastInputLine.ToString(CultureInfo.InvariantCulture) + ")");
                    AppendLine(result, "Room," + chapter.Scene);
                    for (int line = firstInputLine; line <= lastInputLine; line++)
                        AppendLine(result, source.Lines[line - 1]);
                }
            }
            return result.ToString();
        }

        private static List<string> ReadLines(string text)
        {
            List<string> lines = new List<string>();
            using (StringReader reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
            }
            return lines;
        }

        private static int FindDirective(List<string> lines, string preferred, string fallback)
        {
            int fallbackLine = 1;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (i == 0 && line.Length > 0 && line[0] == '\uFEFF') line = line.Substring(1).Trim();
                int comma = line.IndexOf(',');
                if (comma < 0) continue;
                string directive = line.Substring(0, comma).Trim();
                if (directive == preferred) return i + 1;
                if (directive == fallback) fallbackLine = i + 1;
            }
            return fallbackLine;
        }

        private static void AppendLine(StringBuilder result, string line)
        {
            result.Append(line);
            result.Append('\n');
        }

        private static void Fail(ParsedSource source, int line, int column, string reason)
        {
            throw new ScriptComposeException(source.Source.Name, line, column, reason, null);
        }
    }
}
