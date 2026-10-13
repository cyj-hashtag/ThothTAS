using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ThothTAS.Host;

/// <summary>Measures actual scene rebuilds; it does not treat warm runs as cold-process proofs.</summary>
public static class ResetPerformanceVerifier
{
    public sealed record FieldDifference(string Field, string? Cold, string? Warm);
    public sealed record FrameFieldDifference(string Field, int Count, int FirstFrame, string? Cold, string? Warm, double? MaximumAbsoluteNumericDifference);
    public sealed record Sample(int Reset, int Pid, string Directory, double Milliseconds, int Frames,
        string RandomSha256, IReadOnlyList<FieldDifference> BaselineDifferences, string Resources, string Timing,
        IReadOnlyList<FrameFieldDifference> FrameDifferences);
    public sealed record Result(bool Passed, string? Error, IReadOnlyList<Sample> Samples, double? MedianWarmMilliseconds,
        bool AllWarmRandomRecordsEqual, bool ColdEquivalent, IReadOnlyList<string> Checks);

    public static async Task<Result> VerifyAsync(string root, string scriptText, SessionOptions options, int count = 5,
        Action<string>? progress = null, CancellationToken cancellation = default)
    {
        if (count is < 2 or > 100) throw new ArgumentOutOfRangeException(nameof(count), "重置测量次数须在 2 至 100 之间。");
        var output = Path.GetFullPath(options.OutputParent ?? Path.Combine(root, "work", "reset-verification", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(output);
        options = options with { Autoplay = false, OutputParent = Path.Combine(output, "runs") };
        var samples = new List<Sample>();
        var checks = new List<string>();
        string? error = null;
        void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidDataException(description);
            checks.Add(description);
        }
        var pinned = new[] { "docs/game-fingerprint.json", "docs/game-fingerprint.tsv", "game/THOTH_Data/Managed/Assembly-CSharp.dll" }
            .Select(relative => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))).ToList();
        if (options.CommunityMod) pinned.Add(CommunityModProfile.AssemblyPath(root));
        try
        {
            var watch = Stopwatch.StartNew();
            await using (var game = await GameSession.StartAsync(root, scriptText, options, cancellation))
            {
                int pid = game.ProcessId;
                Dictionary<string, string>? coldFields = null;
                for (int reset = 0; reset <= count; reset++)
                {
                    if (reset != 0) { watch.Restart(); await game.RestartAsync(scriptText, options, cancellation); }
                    await game.WaitForStatusAsync(s => s.Frame == 0 && s.State == "paused", TimeSpan.FromSeconds(90), cancellation);
                    double milliseconds = watch.Elapsed.TotalMilliseconds;
                    Require(game.ProcessId == pid, $"重置 {reset} 保留实际游戏 PID。");
                    var fields = ReadBaseline(game.RunDirectory);
                    coldFields ??= fields;
                    Require(fields["Input.moveX"] == "0" && fields["Input.moveY"] == "0", $"重置 {reset} 的基线输入为零。");
                    var playerPositions = coldFields.Keys.Where(key => key.Contains("/Player.", StringComparison.Ordinal) && key.EndsWith(".position", StringComparison.Ordinal)).ToArray();
                    Require(playerPositions.Length != 0 && playerPositions.All(key => fields.GetValueOrDefault(key) == coldFields[key]), $"重置 {reset} 恢复实际玩家初始位置。");
                    Require(!fields.Any(pair => pair.Key.EndsWith(".Player.dead", StringComparison.Ordinal) && pair.Value == "True"), $"重置 {reset} 的玩家存活。");
                    Require(!fields.Any(pair => pair.Key.EndsWith(".Room.roomCompleted", StringComparison.Ordinal) && pair.Value == "True"), $"重置 {reset} 的关卡尚未完成。");
                    if (reset == 0)
                    {
                        foreach (var path in pinned) Require(WriteOpenBlocked(path), "运行期间已校验文件禁止写入：" + Path.GetRelativePath(root, path));
                        var probe = Path.Combine(root, "game", "THOTH_Data", "sharedassets-tas-version-probe-" + Guid.NewGuid().ToString("N"));
                        bool created = false, blocked = false;
                        try
                        {
                            using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { created = true; }
                            try { await game.RestartAsync(scriptText, options, cancellation); }
                            catch (InvalidDataException ex) when (ex.Message.Contains("新资源", StringComparison.Ordinal)) { blocked = true; }
                        }
                        finally { if (created) File.Delete(probe); }
                        Require(blocked, "暖重置仍拒绝清单以外的新关卡/共享资源；已删除自己创建的空探针文件。");
                    }
                    var differences = coldFields.Keys.Union(fields.Keys, StringComparer.Ordinal)
                        .Where(key => coldFields.GetValueOrDefault(key) != fields.GetValueOrDefault(key))
                        .Select(key => new FieldDifference(key, coldFields.GetValueOrDefault(key), fields.GetValueOrDefault(key))).ToArray();
                    await game.SendAsync("PLAY", cancellation);
                    var completed = await game.Completion.WaitAsync(TimeSpan.FromMinutes(10), cancellation);
                    ReplayVerifier.ValidateLogs(game.RunDirectory, completed.Frame);
                    var diagnostics = await ReadSharedText(Path.Combine(game.RunDirectory, "runtime-events.txt"), cancellation);
                    string resources = diagnostics.Split('\n').Single(line => line.StartsWith("resource baseline;", StringComparison.Ordinal)).Trim();
                    string timing = diagnostics.Split('\n').SingleOrDefault(line => line.StartsWith("reset timing;", StringComparison.Ordinal))?.Trim() ?? "cold startup";
                    await using var randomFile = new FileStream(Path.Combine(game.RunDirectory, "random.tsv"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    string randomHash = Convert.ToHexString(await SHA256.HashDataAsync(randomFile, cancellation));
                    var frameDifferences = reset == 0 ? [] : CompareFrameFields(samples[0].Directory, game.RunDirectory);
                    samples.Add(new(reset, pid, game.RunDirectory, milliseconds, completed.Frame, randomHash, differences, resources, timing, frameDifferences));
                    progress?.Invoke($"重置 {reset}：{milliseconds:F1} ms，实际回放 {completed.Frame} 帧。");
                }
                var resourcesByReset = samples.Skip(1).Select(sample => sample.Resources.Split(';').Where(item => item.Contains('=')).Select(item => item.Split('=', 2)).ToDictionary(item => item[0], item => item[1])).ToArray();
                foreach (var name in new[] { "gameObjects", "meshes", "materials", "textures", "persistentRoots" })
                {
                    var values = resourcesByReset.Select(sample => int.Parse(sample[name])).ToArray();
                    Require(values.Max() - values.Min() <= (name == "persistentRoots" ? 0 : 10), "本次连续暖重置的 " + name + " 计数没有逐次累积（资源波动最多 10）。");
                }
                Require(samples.All(sample => sample.Frames == samples[0].Frames), "冷启动与全部暖重置的实际完成帧数一致。");
                Require(samples.All(sample => sample.RandomSha256 == samples[0].RandomSha256), "冷启动与全部暖重置的完整随机记录SHA256一致。");
            }
            foreach (var path in pinned)
            {
                // Request write access only; never write a byte to the game or manifests.
                using var released = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                Require(true, "本工具游戏退出后释放文件锁：" + Path.GetRelativePath(root, path));
            }
        }
        catch (Exception ex) { error = ex.ToString(); progress?.Invoke(ex.Message); }
        var warm = samples.Where(sample => sample.Reset != 0).ToArray();
        var times = warm.Select(sample => sample.Milliseconds).Order().ToArray();
        double? median = times.Length == 0 ? null : times.Length % 2 == 1 ? times[times.Length / 2] : (times[times.Length / 2 - 1] + times[times.Length / 2]) / 2;
        var result = new Result(error == null, error, samples, median, warm.Length > 1 && warm.All(sample => sample.RandomSha256 == warm[0].RandomSha256), false, checks);
        await File.WriteAllTextAsync(Path.Combine(output, "reset-report.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellation);
        await File.WriteAllTextAsync(Path.Combine(output, "reset-report.md"), "# 同进程重置实际测量\n\n结果：" + (result.Passed ? "通过" : "未通过") + "。暖重置中位耗时：" + (median?.ToString("F1") ?? "无") + " ms。\n\n在同一实际游戏进程中完成 " + warm.Length + " 次重建；首次仍做完整 SHA256 校验，运行期间持有只读文件锁，暖重置复用已校验且禁止改写的文件身份。声音库恢复初始层级、选择器和音源状态；玩法场景、其他持久根、对象池和自建资源重建。\n\nJSON 报告保留所有冷暖基线字段差异、整个回放每个差异字段的首次值/次数/数值最大差异、每次 PID/耗时/完整随机记录 SHA256/资源计数/加载阶段。没有隐藏原生时间或微小浮点差异。coldEquivalent=false；这里的通过表示编辑重建功能及本次资源计数检查通过，不表示与冷启动逐帧精确相同、跨电脑一致、瞬时重载或长期资源永不增长。正式精确验证继续使用 verify 的独立进程回放。\n", new UTF8Encoding(false), cancellation);
        return result;
    }

    private static IReadOnlyList<FrameFieldDifference> CompareFrameFields(string coldDirectory, string warmDirectory)
    {
        using var coldFile = new FileStream(Path.Combine(coldDirectory, "frames.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var warmFile = new FileStream(Path.Combine(warmDirectory, "frames.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var cold = new StreamReader(coldFile); using var warm = new StreamReader(warmFile);
        var differences = new Dictionary<string, FrameFieldDifference>(StringComparer.Ordinal);
        while (true)
        {
            string? a = cold.ReadLine(), b = warm.ReadLine();
            if (a == null && b == null) break;
            if (a == null || b == null) throw new InvalidDataException("冷暖回放的逐帧记录长度不同。");
            using var expected = JsonDocument.Parse(a); using var actual = JsonDocument.Parse(b);
            int frame = expected.RootElement.GetProperty("frame").GetInt32();
            if (frame != actual.RootElement.GetProperty("frame").GetInt32()) throw new InvalidDataException("冷暖回放的逐帧编号不同。");
            var left = expected.RootElement.GetProperty("fields").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString());
            var right = actual.RootElement.GetProperty("fields").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString());
            foreach (string field in left.Keys.Union(right.Keys, StringComparer.Ordinal))
            {
                string? x = left.GetValueOrDefault(field), y = right.GetValueOrDefault(field);
                if (x == y) continue;
                var delta = NumericDifference(x, y);
                if (differences.TryGetValue(field, out var old))
                    differences[field] = old with { Count = old.Count + 1, MaximumAbsoluteNumericDifference = old.MaximumAbsoluteNumericDifference.HasValue && delta.HasValue ? Math.Max(old.MaximumAbsoluteNumericDifference.Value, delta.Value) : null };
                else differences.Add(field, new(field, 1, frame, x, y, delta));
            }
        }
        return differences.Values.OrderBy(x => x.FirstFrame).ThenBy(x => x.Field, StringComparer.Ordinal).ToArray();
    }
    private static double? NumericDifference(string? left, string? right)
    {
        if (left == null || right == null) return null;
        var a = left.Split(','); var b = right.Split(','); if (a.Length != b.Length) return null;
        double maximum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (!double.TryParse(a[i].Split('@')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(b[i].Split('@')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y)
                || !double.IsFinite(x) || !double.IsFinite(y)) return null;
            maximum = Math.Max(maximum, Math.Abs(x - y));
        }
        return maximum;
    }

    private static Dictionary<string, string> ReadBaseline(string directory)
    {
        using var file = new FileStream(Path.Combine(directory, "frames.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        using var json = JsonDocument.Parse(reader.ReadLine() ?? throw new InvalidDataException("缺少实际基线。"));
        return json.RootElement.GetProperty("fields").EnumerateObject().ToDictionary(field => field.Name, field => field.Value.GetString()!, StringComparer.Ordinal);
    }
    private static bool WriteOpenBlocked(string path)
    {
        try { using var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); return false; }
        catch (IOException) { return true; }
    }
    private static async Task<string> ReadSharedText(string path, CancellationToken cancellation)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return await reader.ReadToEndAsync(cancellation);
    }
}
