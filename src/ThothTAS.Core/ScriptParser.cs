using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ThothTAS.Core
{
    /// <summary>Pure .NET 3.5 compatible parser; no game, UI, filesystem, or culture dependencies.</summary>
    public static class ScriptParser
    {
        private sealed class Token
        {
            public string Value;
            public int Column;
        }

        public static TasScript Parse(string text, HashSet<string> knownStarts)
        {
            if (text == null) throw new ArgumentNullException("text");
            if (knownStarts == null) throw new ArgumentNullException("knownStarts");
            // Always use an ordinal identifier check, even if the caller's set ignores case.
            HashSet<string> starts = new HashSet<string>(knownStarts, StringComparer.Ordinal);
            bool hasVersion = false;
            bool hasStart = false;
            bool hasSeed = false;
            string start = null;
            int seed = 1;
            int startSourceLine = 0;
            int totalFrames = 0;
            int lineNumber = 0;
            List<TasSegment> segments = new List<TasSegment>();
            List<TasChapter> chapters = new List<TasChapter>();
            List<TasSegment> chapterSegments = new List<TasSegment>();
            HashSet<string> chapterScenes = new HashSet<string>(StringComparer.Ordinal);
            bool hasExplicitRooms = false;
            string chapterScene = null;
            int chapterSourceLine = 0;
            string chapterSourceOrigin = null;
            string pendingSourceOrigin = null;
            int chapterFrames = 0;
            using (StringReader reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (lineNumber == 1 && line.Length > 0 && line[0] == '\uFEFF') line = line.Substring(1);
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    if (trimmed.StartsWith("#", StringComparison.Ordinal))
                    {
                        if (trimmed.StartsWith("# Source:", StringComparison.Ordinal))
                            pendingSourceOrigin = trimmed.Substring("# Source:".Length).Trim();
                        continue;
                    }
                    List<Token> tokens = SplitTokens(line, lineNumber);
                    string first = tokens[0].Value;
                    if (!hasVersion && first != "Version") Fail(lineNumber, tokens[0], "第一个有效指令必须为 Version,1。");
                    if (first == "Version" || first == "Start" || first == "Seed")
                    {
                        if (segments.Count > 0 || hasExplicitRooms) Fail(lineNumber, tokens[0], "头部指令必须在 Room 和输入帧之前。");
                        if (tokens.Count != 2) Fail(lineNumber, tokens[0], "头部指令必须恰有两个字段。");
                        if (first == "Version")
                        {
                            if (hasVersion) Fail(lineNumber, tokens[0], "重复的 Version 指令。");
                            if (tokens[1].Value != "1") Fail(lineNumber, tokens[1], "只支持 Version,1。");
                            hasVersion = true;
                        }
                        else if (first == "Start")
                        {
                            if (hasStart) Fail(lineNumber, tokens[0], "重复的 Start 指令。");
                            if (!starts.Contains(tokens[1].Value)) Fail(lineNumber, tokens[1], "未知关卡：" + tokens[1].Value + "。");
                            start = tokens[1].Value;
                            startSourceLine = lineNumber;
                            hasStart = true;
                        }
                        else
                        {
                            if (hasSeed) Fail(lineNumber, tokens[0], "重复的 Seed 指令。");
                            if (!int.TryParse(tokens[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seed))
                                Fail(lineNumber, tokens[1], "Seed 必须为 32 位有符号整数。");
                            hasSeed = true;
                        }
                        continue;
                    }
                    if (!hasStart) Fail(lineNumber, tokens[0], "输入帧之前必须指定 Start。");
                    if (first == "Room")
                    {
                        if (tokens.Count != 2) Fail(lineNumber, tokens[0], "Room 指令必须恰有两个字段。");
                        if (!starts.Contains(tokens[1].Value)) Fail(lineNumber, tokens[1], "未知关卡：" + tokens[1].Value + "。");
                        if (!hasExplicitRooms)
                        {
                            if (segments.Count != 0) Fail(lineNumber, tokens[0], "显式章节的所有输入必须位于首个 Room 之后。");
                            if (tokens[1].Value != start) Fail(lineNumber, tokens[1], "首个 Room 必须与 Start 相同。");
                            hasExplicitRooms = true;
                        }
                        else
                        {
                            AddChapter(chapters, chapterScene, chapterSourceLine, chapterSourceOrigin, chapterFrames, chapterSegments);
                            chapterSegments = new List<TasSegment>();
                            chapterFrames = 0;
                        }
                        if (!chapterScenes.Add(tokens[1].Value)) Fail(lineNumber, tokens[1], "重复的 Room 关卡：" + tokens[1].Value + "。");
                        chapterScene = tokens[1].Value;
                        chapterSourceLine = lineNumber;
                        chapterSourceOrigin = pendingSourceOrigin;
                        pendingSourceOrigin = null;
                        continue;
                    }
                    int frames;
                    if (!int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out frames) || frames < 1)
                        Fail(lineNumber, tokens[0], "持续帧数必须为正整数。");
                    if (frames > int.MaxValue - totalFrames) Fail(lineNumber, tokens[0], "脚本总帧数超出 32 位整数范围。");
                    TasFrameInput input = ParseInput(tokens, lineNumber);
                    segments.Add(new TasSegment(frames, totalFrames, lineNumber, input));
                    chapterSegments.Add(new TasSegment(frames, chapterFrames, lineNumber, input));
                    totalFrames += frames;
                    chapterFrames += frames;
                }
            }
            int endLine = Math.Max(1, lineNumber);
            if (!hasVersion) throw new ScriptParseException(endLine, 1, "缺少 Version,1。");
            if (!hasStart) throw new ScriptParseException(endLine, 1, "缺少 Start 指令。");
            if (hasExplicitRooms)
                AddChapter(chapters, chapterScene, chapterSourceLine, chapterSourceOrigin, chapterFrames, chapterSegments);
            else
            {
                if (segments.Count == 0) throw new ScriptParseException(endLine, 1, "脚本至少需要一段输入帧。");
                chapters.Add(new TasChapter(0, start, startSourceLine, null, totalFrames, chapterSegments));
            }
            return new TasScript(start, seed, totalFrames, segments, chapters, hasExplicitRooms);
        }

        private static void AddChapter(List<TasChapter> chapters, string scene, int sourceLine, string sourceOrigin, int frames, List<TasSegment> segments)
        {
            if (segments.Count == 0) throw new ScriptParseException(sourceLine, 1, "Room 章节至少需要一段输入帧。");
            chapters.Add(new TasChapter(chapters.Count, scene, sourceLine, sourceOrigin, frames, segments));
        }

        private static TasFrameInput ParseInput(List<Token> tokens, int line)
        {
            double moveX = 0;
            double moveY = 0;
            double aimX = 0;
            double aimY = 0;
            AimKind aimKind = AimKind.None;
            bool hasMove = false;
            bool hasNeutral = false;
            bool hasAim = false;
            bool up = false, down = false, left = false, right = false;
            for (int index = 1; index < tokens.Count; index++)
            {
                Token token = tokens[index];
                string value = token.Value;
                if (value == "N")
                {
                    if (hasNeutral || hasMove || up || down || left || right) Fail(line, token, "N 与其他移动输入重复或冲突。");
                    hasNeutral = true;
                }
                else if (value.StartsWith("M=", StringComparison.Ordinal))
                {
                    if (hasMove || hasNeutral || up || down || left || right) Fail(line, token, "M 与其他移动输入重复或冲突。");
                    ParseVectorOrAngle(value.Substring(2).Trim(), line, token, out moveX, out moveY);
                    hasMove = true;
                }
                else if (value.StartsWith("A=", StringComparison.Ordinal))
                {
                    if (hasAim) Fail(line, token, "重复的 A 输入。");
                    string aim = value.Substring(2).Trim();
                    if (aim.StartsWith("(", StringComparison.Ordinal))
                    {
                        ParseTarget(aim, line, token, out aimX, out aimY);
                        aimKind = AimKind.WorldTarget;
                    }
                    else
                    {
                        ParseVectorOrAngle(aim, line, token, out aimX, out aimY);
                        aimKind = AimKind.Vector;
                    }
                    hasAim = true;
                }
                else if (IsDirections(value))
                {
                    if (hasMove || hasNeutral) Fail(line, token, "方向简写与 M 或 N 冲突。");
                    for (int i = 0; i < value.Length; i++)
                    {
                        switch (value[i])
                        {
                            case 'U':
                                if (up || down) Fail(line, token, "重复或相反的上下方向输入。");
                                up = true;
                                break;
                            case 'D':
                                if (down || up) Fail(line, token, "重复或相反的上下方向输入。");
                                down = true;
                                break;
                            case 'L':
                                if (left || right) Fail(line, token, "重复或相反的左右方向输入。");
                                left = true;
                                break;
                            case 'R':
                                if (right || left) Fail(line, token, "重复或相反的左右方向输入。");
                                right = true;
                                break;
                        }
                    }
                }
                else Fail(line, token, "未知输入：" + value + "。");
            }
            if (up || down || left || right)
                TasFrameInput.Normalize((right ? 1 : 0) - (left ? 1 : 0), (up ? 1 : 0) - (down ? 1 : 0), false, out moveX, out moveY);
            return new TasFrameInput(moveX, moveY, aimKind, aimX, aimY);
        }

        private static bool IsDirections(string value)
        {
            if (value.Length == 0) return false;
            for (int i = 0; i < value.Length; i++)
                if (value[i] != 'U' && value[i] != 'D' && value[i] != 'L' && value[i] != 'R') return false;
            return true;
        }

        private static void ParseVectorOrAngle(string value, int line, Token token, out double x, out double y)
        {
            int colon = value.IndexOf(':');
            if (colon >= 0)
            {
                if (value.IndexOf(':', colon + 1) >= 0) Fail(line, token, "模拟向量必须采用 x:y 格式。");
                x = ParseNumber(value.Substring(0, colon), line, token);
                y = ParseNumber(value.Substring(colon + 1), line, token);
                TasFrameInput.Normalize(x, y, false, out x, out y);
                return;
            }
            double angle = ParseNumber(value, line, token) % 360.0;
            if (angle < 0) angle += 360.0;
            double radians = angle * Math.PI / 180.0;
            x = Math.Cos(radians);
            y = Math.Sin(radians);
            if (Math.Abs(x) < 1e-15) x = 0;
            if (Math.Abs(y) < 1e-15) y = 0;
        }

        private static void ParseTarget(string value, int line, Token token, out double x, out double y)
        {
            if (value.Length < 3 || value[value.Length - 1] != ')') Fail(line, token, "世界目标必须采用 A=(x,y) 格式。");
            string body = value.Substring(1, value.Length - 2);
            int comma = body.IndexOf(',');
            if (comma < 0 || body.IndexOf(',', comma + 1) >= 0) Fail(line, token, "世界目标必须恰有两个坐标。");
            x = ParseNumber(body.Substring(0, comma), line, token);
            y = ParseNumber(body.Substring(comma + 1), line, token);
        }

        private static double ParseNumber(string value, int line, Token token)
        {
            double number;
            if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                || double.IsNaN(number) || double.IsInfinity(number))
                Fail(line, token, "输入必须为有限数值，使用小数点作为小数分隔符。");
            return number;
        }

        private static List<Token> SplitTokens(string line, int lineNumber)
        {
            List<Token> tokens = new List<Token>();
            int start = 0;
            int depth = 0;
            for (int i = 0; i <= line.Length; i++)
            {
                if (i < line.Length)
                {
                    if (line[i] == '(')
                    {
                        depth++;
                        if (depth > 1) throw new ScriptParseException(lineNumber, i + 1, "不支持嵌套括号。");
                    }
                    else if (line[i] == ')')
                    {
                        depth--;
                        if (depth < 0) throw new ScriptParseException(lineNumber, i + 1, "缺少左括号。");
                    }
                }
                if (i == line.Length || (line[i] == ',' && depth == 0))
                {
                    if (i == line.Length && depth != 0) throw new ScriptParseException(lineNumber, line.Length + 1, "缺少右括号。");
                    string raw = line.Substring(start, i - start);
                    int whitespace = raw.Length - raw.TrimStart().Length;
                    string value = raw.Trim();
                    if (value.Length == 0) throw new ScriptParseException(lineNumber, start + whitespace + 1, "字段不能为空。");
                    tokens.Add(new Token { Value = value, Column = start + whitespace + 1 });
                    start = i + 1;
                }
            }
            return tokens;
        }

        private static void Fail(int line, Token token, string message)
        {
            throw new ScriptParseException(line, token.Column, message);
        }
    }
}
