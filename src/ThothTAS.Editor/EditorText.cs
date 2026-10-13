using System.Text.Json;
using ThothTAS.Core;

namespace ThothTAS.Editor;

internal sealed class EditorText
{
    internal bool English { get; set; }
    internal string Pick(string chinese, string english) => English ? english : chinese;
    internal static string ControlEnglish(string chinese) => Controls.GetValueOrDefault(chinese, chinese);
    private static readonly Dictionary<string, string> Controls = new()
    {
        ["打开"] = "Open", ["保存"] = "Save", ["另存为"] = "Save as", ["校验"] = "Validate",
        ["拼接脚本"] = "Compose TAS…", ["播放"] = "Play", ["暂停"] = "Pause", ["逐帧"] = "Step",
        ["停止"] = "Stop", ["重新回放"] = "Replay", ["重复验证 ×5"] = "Verify ×5",
        ["速度"] = "Speed", ["启动尺寸"] = "Window size", ["打开日志目录"] = "Open logs",
        ["草稿"] = "Draft", ["运行快照（只读）"] = "Run snapshot (read only)",
        ["同进程快速重置"] = "Quick reset (same process)", ["同时验证暂停 / 逐帧 / 速度"] = "Include pause / step / speed",
        ["社区计时器"] = "Community timers",
        ["上移"] = "Move up", ["下移"] = "Move down", ["添加脚本"] = "Add scripts…", ["移除"] = "Remove",
        ["生成拼接草稿"] = "Create composed draft", ["取消"] = "Cancel",
    };
    internal string Error(Exception error)
    {
        if (error is ScriptParseException parse)
            return $"Line {parse.Line}, column {parse.Column}: {Reason(parse.Reason)}";
        if (error is ScriptComposeException compose)
            return $"{compose.SourceName}: line {compose.Line}, column {compose.Column}: {Reason(compose.Reason)}";
        return Reason(error.Message);
    }
    internal string Reason(string message)
    {
        foreach (var (zh, en) in Errors.OrderByDescending(x => x.Key.Length)) message = message.Replace(zh, en, StringComparison.Ordinal);
        return message.Replace("。", ".").Replace("：", ": ").Replace("；", "; ").Replace("，", ", ");
    }
    // Presentation-only translation; parser behavior and recorded source diagnostics remain unchanged.
    private static readonly Dictionary<string, string> Errors = new()
    {
        ["第一个有效指令必须为 Version,1"] = "The first directive must be Version,1",
        ["头部指令必须在 Room 和输入帧之前"] = "Headers must precede Room and input frames",
        ["头部指令必须恰有两个字段"] = "Headers require exactly two fields",
        ["重复的 Version 指令"] = "Duplicate Version directive", ["只支持 Version,1"] = "Only Version,1 is supported",
        ["重复的 Start 指令"] = "Duplicate Start directive", ["未知关卡"] = "Unknown level",
        ["重复的 Seed 指令"] = "Duplicate Seed directive", ["Seed 必须为 32 位有符号整数"] = "Seed must be a signed 32-bit integer",
        ["输入帧之前必须指定 Start"] = "Specify Start before input frames",
        ["Room 指令必须恰有两个字段"] = "Room requires exactly two fields",
        ["显式章节的所有输入必须位于首个 Room 之后"] = "All chapter inputs must follow the first Room",
        ["首个 Room 必须与 Start 相同"] = "The first Room must match Start",
        ["重复的 Room 关卡"] = "Duplicate Room level", ["持续帧数必须为正整数"] = "Duration must be a positive integer",
        ["脚本总帧数超出 32 位整数范围"] = "Total frames exceed the 32-bit integer range",
        ["缺少 Version,1"] = "Missing Version,1", ["缺少 Start 指令"] = "Missing Start directive",
        ["脚本至少需要一段输入帧"] = "The script needs at least one input segment",
        ["章节至少需要一段输入帧"] = "Each chapter needs at least one input segment",
        ["重复的移动输入"] = "Duplicate movement input", ["重复的射击输入"] = "Duplicate aim input",
        ["方向简写不能与 M 或 N 同时使用"] = "Direction shorthand cannot be combined with M or N",
        ["N 与其他移动输入重复或冲突"] = "N duplicates or conflicts with another movement input",
        ["M 与其他移动输入重复或冲突"] = "M duplicates or conflicts with another movement input",
        ["重复的 A 输入"] = "Duplicate A input",
        ["方向简写与 M 或 N 冲突"] = "Direction shorthand conflicts with M or N",
        ["重复或相反的上下方向输入"] = "Duplicate or opposing vertical directions",
        ["重复或相反的左右方向输入"] = "Duplicate or opposing horizontal directions",
        ["模拟向量必须采用 x:y 格式"] = "Analog vectors must use x:y",
        ["世界目标必须采用 A=(x,y) 格式"] = "World targets must use A=(x,y)",
        ["世界目标必须恰有两个坐标"] = "World targets need exactly two coordinates",
        ["输入必须为有限数值，使用小数点而非逗号"] = "Inputs must be finite numbers using a decimal point",
        ["右括号没有匹配的左括号"] = "Closing parenthesis has no matching opening parenthesis",
        ["括号未闭合"] = "Unclosed parenthesis", ["存在空字段"] = "Empty field",
        ["非法数值"] = "Invalid number", ["未知输入"] = "Unknown input",
        ["至少需要一个输入脚本"] = "Select at least one input script",
        ["拼接包含重复关卡"] = "Composition contains a duplicate level",
        ["无法自然连接"] = "Levels are not naturally adjacent",
        ["拼接输入预算超出 32 位整数范围"] = "Composed frame budget exceeds the 32-bit integer range",
        ["所有来源必须使用相同 Seed"] = "All sources must use the same Seed",
        ["输入脚本的 Seed 不一致"] = "Input scripts use different Seeds",
        ["请先统一种子，再验证整段回放"] = "Use the same seed, then verify the entire continuous replay",
        ["自然下一关为"] = "The natural next level is",
        ["此关之后没有普通自然下一关"] = "There is no ordinary next level",
        ["要求"] = "expected", ["实际为"] = "actual",
        ["Teleporters-5 是额外资源，出口 Teleporters-6 不存在，不能加入自然转场拼接"] = "Teleporters-5 has a missing Teleporters-6 exit and cannot be composed",
        ["游戏版本指纹不匹配，拒绝回放"] = "Game fingerprint mismatch; replay rejected",
        ["实际游戏进程"] = "Actual game process", ["记录目录"] = "Log directory",
        ["运行日志"] = "Run logs", ["实际回放超时"] = "Actual replay timed out",
        ["暂停期间帧数或随机序列仍在推进"] = "Frames or random calls advanced while paused",
        ["未得到帧边界暂停确认"] = "No frame-boundary pause acknowledgment",
    };
    internal static bool LoadEnglish(string root)
    {
        try
        {
            var path = Path.Combine(root, "work", "editor-settings.json");
            return File.Exists(path) && JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path))?.Language == "en";
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    internal void SaveLanguage(string root)
    {
        var directory = Path.Combine(root, "work");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "editor-settings.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Preferences { Language = English ? "en" : "zh-Hans" }));
        File.Move(temporary, path, true);
    }
    private sealed class Preferences { public Preferences() { } public string Language { get; set; } = "zh-Hans"; }
}
