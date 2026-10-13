using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThothTAS.Host;

public sealed record ReplayDifference(string File, int Record, int? Frame, string Field, string Expected, string Actual);
public sealed record ReplayResult(string Mode, double Speed, string Directory, int Frames, bool PauseStable);
public sealed record FailedReplayResult(string Mode, double Speed, string Directory, int? Frame, string Error);
public sealed class ReplayRunException(string message, string runDirectory, GameStatus status, Exception inner) : Exception(message + "\n运行日志：" + runDirectory, inner)
{
    public string RunDirectory { get; } = runDirectory;
    public GameStatus LastStatus { get; } = status;
}
public sealed class VerificationReport
{
    public int Version { get; init; } = 1;
    public string Scope { get; init; } = "当前电脑、当前游戏指纹；实际独立游戏进程逐帧验证";
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public bool Passed { get; set; }
    public string? Error { get; set; }
    public List<ReplayResult> Runs { get; } = [];
    public List<FailedReplayResult> FailedRuns { get; } = [];
    public List<ReplayDifference> Differences { get; } = [];
}

public static class ReplayVerifier
{
    public static async Task<ReplayResult> RunAsync(string root, string scriptText, SessionOptions options, string mode, Action<string>? progress = null, CancellationToken cancellation = default)
    {
        if (mode is not ("play" or "step" or "pause")) throw new ArgumentException("运行模式必须是 play、step 或 pause。", nameof(mode));
        await using var session = await GameSession.StartAsync(root, scriptText, options with { Autoplay = false }, cancellation);
        progress?.Invoke("实际游戏进程 " + session.ProcessId + "，记录目录 " + session.RunDirectory);
        bool pauseStable = false;
        var transitionBudget = session.Script.HasExplicitRooms ? Math.Max(0, session.Script.Chapters.Count - 1) * 3600.0 : 0;
        var timeout = TimeSpan.FromSeconds(Math.Min(72 * 3600, Math.Max(120, session.Script.TotalFrames / (60.0 * options.Speed) * 5 + 90 + transitionBudget)));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            await session.WaitForStatusAsync(s => s.State == "paused" || s.State == "complete", TimeSpan.FromSeconds(90), limit.Token);
            if (mode == "step")
            {
                while (session.Latest.State != "complete") await session.StepAsync(limit.Token);
            }
            else
            {
                if (mode == "pause")
                {
                    // Pause at the baseline first: also works for a one-frame script.
                    await session.SendAsync("PAUSE", limit.Token);
                    await session.SendAsync("STATUS", limit.Token);
                    await Task.Delay(150, limit.Token);
                    var a = session.Latest;
                    if (a.State != "paused") throw new InvalidDataException("PAUSE 未得到帧边界暂停确认。");
                    await Task.Delay(400, limit.Token);
                    await session.SendAsync("STATUS", limit.Token);
                    await Task.Delay(150, limit.Token);
                    var b = session.Latest;
                    pauseStable = a.Frame == b.Frame && a.RandomCount == b.RandomCount && a.RandomHash == b.RandomHash;
                    if (!pauseStable) throw new InvalidDataException("暂停期间帧数或随机序列仍在推进。");
                    // Exercise pause/resume during play for scripts long enough to reach it.
                    if (session.Script.TotalFrames > 10)
                    {
                        for (int i = 0; i < 5; i++) await session.StepAsync(limit.Token);
                        await session.SendAsync("PLAY", limit.Token);
                        await session.WaitForStatusAsync(s => s.Frame >= 8 || s.State == "complete", TimeSpan.FromSeconds(15), limit.Token);
                        if (session.Latest.State != "complete")
                        {
                            await session.SendAsync("PAUSE", limit.Token);
                            await session.WaitForStatusAsync(s => s.State == "paused" || s.State == "complete", TimeSpan.FromSeconds(15), limit.Token);
                            var paused = session.Latest;
                            await Task.Delay(400, limit.Token);
                            await session.SendAsync("STATUS", limit.Token);
                            await Task.Delay(150, limit.Token);
                            var after = session.Latest;
                            if (paused.Frame != after.Frame || paused.RandomCount != after.RandomCount || paused.RandomHash != after.RandomHash)
                                throw new InvalidDataException("播放中暂停仍消耗帧或随机数。");
                        }
                    }
                }
                if (session.Latest.State != "complete") await session.SendAsync("PLAY", limit.Token);
            }
            await session.Completion.WaitAsync(limit.Token);
            var actualFrames = session.Latest.Frame;
            if (actualFrames < 0) throw new InvalidDataException("完成消息没有有效的实际回放帧数。");
            if (!session.Script.HasExplicitRooms && actualFrames != session.Script.TotalFrames) throw new InvalidDataException("完成消息的帧数与单段脚本总输入帧数不一致。");
            ValidateLogs(session.RunDirectory, actualFrames);
            return new(mode, options.Speed, session.RunDirectory, actualFrames, pauseStable);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            var error = new TimeoutException($"实际回放超时（{timeout.TotalSeconds:0} 秒）。");
            throw new ReplayRunException(error.Message, session.RunDirectory, session.Latest, error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new ReplayRunException(ex.Message, session.RunDirectory, session.Latest, ex); }
    }

    public static void ValidateLogs(string directory, int totalFrames)
    {
        var framePath = Path.Combine(directory, "frames.jsonl");
        var randomPath = Path.Combine(directory, "random.tsv");
        if (!File.Exists(framePath) || !File.Exists(randomPath)) throw new InvalidDataException("实际游戏未输出 frames.jsonl / random.tsv；拒绝将此次运行计为验证通过。");
        int expectedFrame = 0;
        using var frames = OpenLog(framePath);
        string? line;
        while ((line = frames.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("frames.jsonl 存在空记录。");
            using var doc = JsonDocument.Parse(line);
            var record = doc.RootElement;
            if (record.GetProperty("frame").GetInt32() != expectedFrame) throw new InvalidDataException($"帧日志不连续：期望帧 {expectedFrame}。");
            if (!record.TryGetProperty("fields", out _) || !record.TryGetProperty("randomCount", out _) || !record.TryGetProperty("randomHash", out _) || !record.TryGetProperty("phases", out _))
                throw new InvalidDataException($"帧 {expectedFrame} 缺少玩法状态、随机摘要或更新阶段，不能进行确定性验证。");
            expectedFrame++;
        }
        if (expectedFrame != totalFrames + 1) throw new InvalidDataException($"帧日志不完整：应有基线及 {totalFrames} 个输入帧，实际 {expectedFrame} 条。");
        if (new FileInfo(randomPath).Length == 0) throw new InvalidDataException("随机日志为空，缺少记录格式或调用内容。");
        var nativeLog = Path.Combine(directory, "unity.log");
        var metadata = Path.Combine(directory, "host-run.json");
        if (File.Exists(metadata))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadata));
            if (document.RootElement.TryGetProperty("initialUnityLogPath", out var log)) nativeLog = log.GetString() ?? nativeLog;
        }
        if (File.Exists(nativeLog))
        {
            using var log = OpenLog(nativeLog);
            while ((line = log.ReadLine()) != null)
                if (Regex.IsMatch(line, "NullReferenceException|MissingReferenceException|MissingMethodException|InvalidProgramException|Crash!!!|THOTH TAS:", RegexOptions.CultureInvariant))
                    throw new InvalidDataException("实际游戏日志含异常，不能将相同但损坏的回放计为通过：" + line + "；日志 " + nativeLog);
        }
    }

    public static ReplayDifference? Compare(string expectedDirectory, string actualDirectory)
    {
        foreach (var filename in new[] { "frames.jsonl", "random.tsv", "phases.tsv" })
        {
            var expectedPath = Path.Combine(expectedDirectory, filename);
            var actualPath = Path.Combine(actualDirectory, filename);
            if (!File.Exists(expectedPath) || !File.Exists(actualPath)) return new(filename, 0, null, "$file", File.Exists(expectedPath) ? "存在" : "缺失", File.Exists(actualPath) ? "存在" : "缺失");
            using var a = OpenLog(expectedPath);
            using var b = OpenLog(actualPath);
            int record = 0;
            while (true)
            {
                var left = a.ReadLine();
                var right = b.ReadLine();
                if (left == null && right == null) break;
                record++;
                if (left == null || right == null) return new(filename, record, null, "$length", left == null ? "EOF" : left, right == null ? "EOF" : right);
                if (filename.EndsWith("jsonl", StringComparison.Ordinal))
                {
                    try
                    {
                        using var dl = JsonDocument.Parse(left);
                        using var dr = JsonDocument.Parse(right);
                        int? frame = dl.RootElement.TryGetProperty("frame", out var f) && f.TryGetInt32(out var n) ? n : null;
                        var difference = FirstJsonDifference(dl.RootElement, dr.RootElement, "$");
                        if (difference != null) return new(filename, record, frame, difference.Value.Field, difference.Value.Left, difference.Value.Right);
                    }
                    catch (JsonException ex) { return new(filename, record, null, "$json", "合法 JSON", ex.Message); }
                }
                else if (!string.Equals(left, right, StringComparison.Ordinal))
                {
                    var column = left.Split('\t');
                    var frameColumn = column.Length >= 3 ? column[1] : column[0];
                    int? frame = int.TryParse(frameColumn, CultureInfo.InvariantCulture, out var f) ? f : null;
                    return new(filename, record, frame, filename == "phases.tsv" ? "$phase-callback" : "$random-call", left, right);
                }
            }
        }
        return null;
    }

    private static StreamReader OpenLog(string path) => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);

    private static (string Field, string Left, string Right)? FirstJsonDifference(JsonElement left, JsonElement right, string path)
    {
        if (left.ValueKind != right.ValueKind) return (path, left.GetRawText(), right.GetRawText());
        if (left.ValueKind == JsonValueKind.Object)
        {
            var l = left.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            var r = right.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            foreach (var key in l.Keys.Union(r.Keys).Order(StringComparer.Ordinal))
            {
                var child = path + "." + key;
                if (!l.TryGetValue(key, out var lv)) return (child, "<缺失>", r[key].GetRawText());
                if (!r.TryGetValue(key, out var rv)) return (child, lv.GetRawText(), "<缺失>");
                var found = FirstJsonDifference(lv, rv, child);
                if (found != null) return found;
            }
            return null;
        }
        if (left.ValueKind == JsonValueKind.Array)
        {
            var l = left.EnumerateArray().ToArray();
            var r = right.EnumerateArray().ToArray();
            for (int i = 0; i < Math.Min(l.Length, r.Length); i++)
            {
                var found = FirstJsonDifference(l[i], r[i], path + "[" + i + "]");
                if (found != null) return found;
            }
            return l.Length == r.Length ? null : (path + ".length", l.Length.ToString(CultureInfo.InvariantCulture), r.Length.ToString(CultureInfo.InvariantCulture));
        }
        // Preserve float bit-level serialization and string contents. No tolerance is applied.
        return left.GetRawText() == right.GetRawText() ? null : (path, left.GetRawText(), right.GetRawText());
    }

    public static async Task<VerificationReport> VerifyAsync(string root, string scriptText, SessionOptions options, int count, bool includeModes, Action<string>? progress = null, CancellationToken cancellation = default)
    {
        if (count < 2 || count > 100) throw new ArgumentOutOfRangeException(nameof(count), "重复验证次数须为 2 至 100。");
        var report = new VerificationReport();
        var directory = Path.GetFullPath(options.OutputParent ?? Path.Combine(root, "work", "verification", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        options = options with { OutputParent = directory, Speed = 1 };
        string attemptedMode = "play";
        double attemptedSpeed = 1;
        try
        {
            var plan = Enumerable.Range(0, count).Select(_ => (Mode: "play", Speed: 1.0)).ToList();
            if (includeModes) plan.AddRange([("pause", 1.0), ("step", 1.0), ("play", 0.5), ("play", 2.0)]);
            foreach (var (mode, speed) in plan)
            {
                attemptedMode = mode;
                attemptedSpeed = speed;
                progress?.Invoke($"回放 {report.Runs.Count + 1}/{plan.Count}：{mode}，{speed}×");
                var reference = report.Runs.Count == 0 ? null : Path.Combine(report.Runs[0].Directory, "frames.jsonl");
                var run = await RunAsync(root, scriptText, options with { Speed = speed, ReferenceFrames = reference }, mode, progress, cancellation);
                report.Runs.Add(run);
                if (report.Runs.Count > 1)
                {
                    var difference = Compare(report.Runs[0].Directory, run.Directory);
                    if (difference != null) report.Differences.Add(difference);
                }
                await WriteReportAsync(directory, report, cancellation);
            }
            report.Passed = report.Differences.Count == 0;
        }
        catch (Exception ex)
        {
            report.Error = ex.ToString(); report.Passed = false;
            if (ex is ReplayRunException replay)
                report.FailedRuns.Add(new(attemptedMode, attemptedSpeed, replay.RunDirectory, replay.LastStatus.Frame, replay.LastStatus.Error ?? replay.Message));
            else if ((ex.Data["runDirectory"] ?? ex.InnerException?.Data["runDirectory"]) is string failedDirectory && failedDirectory.Length != 0)
                report.FailedRuns.Add(new(attemptedMode, attemptedSpeed, failedDirectory, null, ex.Message));
        }
        await WriteReportAsync(directory, report, CancellationToken.None);
        progress?.Invoke("报告：" + Path.Combine(directory, "report.md"));
        return report;
    }

    private static async Task WriteReportAsync(string directory, VerificationReport report, CancellationToken cancellation)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellation);
        var text = new StringBuilder("# THOTH TAS 实际回放验证报告\n\n");
        text.AppendLine("范围：" + report.Scope).AppendLine();
        text.AppendLine("结果：" + (report.Error != null ? "运行失败" : report.Passed ? "已通过" : "尚未通过 / 执行中")).AppendLine();
        text.AppendLine($"已完成独立进程回放：{report.Runs.Count} 次。比较包括初始基线、所有输入帧的玩法字段、阶段与随机摘要，以及完整 random.tsv。浮点数据不使用容差。").AppendLine();
        foreach (var run in report.Runs) text.AppendLine($"- {run.Mode}，{run.Speed}×，{run.Frames} 帧，暂停稳定={run.PauseStable}；目录 `{run.Directory}`");
        foreach (var run in report.FailedRuns) text.AppendLine($"- 失败：{run.Mode}，{run.Speed}×，最后帧 {run.Frame}；目录 `{run.Directory}`；{run.Error}");
        text.AppendLine();
        foreach (var diff in report.Differences)
            text.AppendLine($"首个分歧：{diff.File}，记录 {diff.Record}，帧 {diff.Frame?.ToString() ?? "未知"}，字段 `{diff.Field}`\n\n期望：`{diff.Expected}`\n\n实际：`{diff.Actual}`\n");
        if (report.Error != null) text.AppendLine("```text\n" + report.Error + "\n```");
        text.AppendLine("\n本报告只覆盖上列脚本及运行方式。未执行的关卡、敌人、输入与转场路径，以及跨电脑一致性，均未由此报告验证。");
        await File.WriteAllTextAsync(Path.Combine(directory, "report.md"), text.ToString(), new UTF8Encoding(false), cancellation);
    }
}
