using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThothTAS.Core;
using ThothTAS.Host;

namespace ThothTAS.Editor;

internal static class EditorCommunityTest
{
    internal static async Task<bool> RunAsync(EditorForm form, string root, string first, string second, string output, CancellationToken cancellation)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var runs = new List<string>();
        var windows = new List<EditorRuntimeTest.WindowEvidence>();
        string? error = null;
        var sources = new[] { Path.GetFullPath(first), Path.GetFullPath(second) };
        var settingsPath = Path.Combine(root, "work", "editor-settings.json");
        var savedLanguage = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        var protectedFiles = sources.Concat(new[] { Path.Combine(root, "game", "settings.txt"), Path.Combine(root, "game", "THOTH_Data", "Managed", "Assembly-CSharp.dll") }).ToArray();
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var beforeHashes = protectedFiles.Select(Hash).ToArray();
        void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); checks.Add(message); }
        Dictionary<string, string> Fields(string run, bool firstRecord = false)
        {
            using var file = new FileStream(Path.Combine(run, "frames.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(file);
            string? last = null, line;
            while ((line = reader.ReadLine()) != null) { last = line; if (firstRecord) break; }
            using var json = JsonDocument.Parse(last ?? throw new InvalidDataException("缺少实际帧日志。"));
            return json.RootElement.GetProperty("fields").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        }
        void CaptureEditor(string name)
        {
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(output, name));
        }
        try
        {
            var starts = File.ReadAllLines(Path.Combine(root, "docs", "levels.txt")).Where(s => s.Length > 0 && !s.StartsWith('#')).ToArray();
            var combined = ScriptComposer.Compose(sources.Select(p => new ScriptSource(p, File.ReadAllText(p))).ToArray(), starts);
            var practice = Path.Combine(output, "composed-practice.tas");
            await File.WriteAllTextAsync(practice, combined, new UTF8Encoding(false), cancellation);
            form.TestOutputParent(Path.Combine(output, "runs"));
            form.TestOpenPath(practice);
            await form.TestClickAsync("打开");
            form.TestCommunity(true);
            form.TestFastReset(false);
            await form.TestClickAsync("播放");
            var baseline = form.ActiveSession!;
            runs.Add(baseline.RunDirectory);
            await baseline.Completion.WaitAsync(TimeSpan.FromMinutes(3), cancellation);
            Require(baseline.Latest.Frame == 495, "社区模式实际两关自然转场并完成495输入帧");
            Require(Fields(baseline.RunDirectory)["CommunityMod.profile"] == "rmadlal/v2.8", "实际加载社区v2.8程序集，计时字段进入逐帧记录");
            await form.TestClickAsync("停止");
            await form.TestClickAsync("逐帧");
            var game = form.ActiveSession!;
            runs.Add(game.RunDirectory);
            for (int i = 1; i < 34; i++) await form.TestClickAsync("逐帧");
            var pausedFields = Fields(game.RunDirectory);
            Require(pausedFields["CommunityMod.inGameTime.frames"] == "34" && pausedFields["CommunityMod.realTimeTimer.frames"] == "34", "实际34帧的社区计时器均按完整输入周期计数");
            Require(pausedFields["CommunityMod.ILVisible"] == "True" && form.SnapshotAnnotation == " --- 34", "四计时显示开启，原行旁帧标记继续正常显示");
            var handle = await EditorRuntimeTest.WaitForGameWindowAsync(game);
            var window = EditorRuntimeTest.InspectWindow(game, handle);
            windows.Add(window);
            Require(window.ClientWidth == 1280 && window.ClientHeight == 720 && window.HasCaption && !window.CanResize && !window.CanMaximize, "社区模式实际窗口1280×720、有标题边框且尺寸固定");
            EditorRuntimeTest.PrepareFocusOwners(Environment.ProcessId, game.ProcessId);
            await EditorRuntimeTest.FocusAsync(handle, game.ProcessId, cancellation);
            await EditorRuntimeTest.CaptureAsync(root, handle, Path.Combine(output, "community-game-frame34.png"));
            await Task.Delay(650, cancellation);
            await game.SendAsync("STATUS", cancellation);
            await Task.Delay(100, cancellation);
            Require(game.Latest.Frame == 34 && pausedFields.OrderBy(x => x.Key).SequenceEqual(Fields(game.RunDirectory).OrderBy(x => x.Key)), "真实暂停650ms时输入帧、计时字段及玩法状态不变");
            await EditorRuntimeTest.FocusAsync(form.Handle, Environment.ProcessId, cancellation);
            form.TestLanguage(true);
            form.TestShowSnapshot();
            CaptureEditor("community-editor-en.png");
            form.DraftText += "\n# community test draft only\n";
            form.TestSavePath(Path.Combine(output, "edited-draft.tas"));
            await form.TestClickAsync("另存为");
            await form.TestSpeedAsync(0.5);
            await form.TestClickAsync("播放");
            await game.WaitForStatusAsync(s => s.Frame >= 45, TimeSpan.FromSeconds(15), cancellation);
            await EditorRuntimeTest.FocusAsync(handle, game.ProcessId, cancellation);
            await EditorRuntimeTest.FocusAsync(form.Handle, Environment.ProcessId, cancellation);
            await form.TestSpeedAsync(2);
            await game.Completion.WaitAsync(TimeSpan.FromMinutes(3), cancellation);
            Require(ReplayVerifier.Compare(runs[0], runs[1]) == null, "社区模式连续与逐帧/暂停/焦点切换/编辑另存/变速的两冷进程逐帧状态及完整随机记录精确一致");
            form.TestFastReset(true);
            var pid = game.ProcessId;
            var watch = Stopwatch.StartNew();
            await form.TestClickAsync("重新回放");
            Require(form.ActiveSession!.ProcessId == pid, "勾选同进程快速重置后社区模式保留实际PID");
            Require(Fields(form.ActiveSession.RunDirectory, true)["CommunityMod.realTimeTimer.frames"] == "0", "社区暖重置恢复计时零基线");
            checks.Add("编辑器实际社区暖重置返回耗时ms=" + watch.Elapsed.TotalMilliseconds.ToString("F1"));
            runs.Add(form.ActiveSession.RunDirectory);
            await form.ActiveSession.Completion.WaitAsync(TimeSpan.FromMinutes(3), cancellation);
            form.TestCommunity(false);
            await form.TestClickAsync("重新回放");
            Require(form.ActiveSession!.ProcessId != pid, "播放中修改社区选项后重新回放自动新建进程，拒绝同进程切换程序集");
            runs.Add(form.ActiveSession.RunDirectory);
            await form.ActiveSession.Completion.WaitAsync(TimeSpan.FromMinutes(3), cancellation);
            Require(!Fields(form.ActiveSession.RunDirectory).ContainsKey("CommunityMod.profile"), "取消社区选项后实际回到原版程序集模式");
            await form.TestClickAsync("停止");
            Require(beforeHashes.SequenceEqual(protectedFiles.Select(Hash)), "用户两份输入、原版程序集及游戏设置的SHA256全部保持不变");
        }
        catch (Exception ex) { error = ex.ToString(); try { if (form.ActiveSession != null) await form.TestClickAsync("停止"); } catch { } }
        finally
        {
            if (savedLanguage != null) File.WriteAllBytes(settingsPath, savedLanguage);
            else if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
        var report = new { version = 1, passed = error == null, error, checks, runs, windows, beforeHashes,
            scope = "实际社区程序集、编辑器按钮、两关自然转场、计时记录、暂停/逐帧/变速/焦点切换/编辑另存/暖重置及冷进程切换配置",
            limitations = "文件选择路径由自测注入；未验证所有关卡、社区作弊功能、排行榜RTA或跨电脑一致性；暖重置coldEquivalent=false。" };
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), "# 社区Mod实际编辑器验证\n\n结果：" + (error == null ? "通过" : "未通过") + "\n\n" + string.Join("\n", checks.Select(s => "- " + s)) + "\n\n" + error, new UTF8Encoding(false));
        return error == null;
    }
}
