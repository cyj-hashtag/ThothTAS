using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThothTAS.Host;

namespace ThothTAS.Editor;

/// <summary>Actual-game editor replay test; it is separate from the UI-only rules test.</summary>
internal static class EditorModifiedReplayTest
{
    internal static async Task<bool> RunAsync(EditorForm owner, string root, string input, string output, bool community, CancellationToken cancellation)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>(); var runs = new List<object>();
        var settings = Path.Combine(root, "work", "editor-settings.json");
        var originalSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var inputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));
        string? error = null;
        void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); checks.Add(message); }
        using var form = new EditorForm(root, input, false);
        form.TestOutputParent(Path.Combine(output, "runs"));
        form.Show(owner);
        try
        {
            form.TestLanguage(false); form.TestCommunity(community); form.TestModifiedRules(true);
            var initial = form.DraftText;
            await form.TestClickAsync("播放");
            var cold = form.ActiveSession!;
            var firstPid = cold.ProcessId;
            var firstRun = cold.RunDirectory;
            await cold.WaitForStatusAsync(s => s.Frame >= 20 || s.State == "complete", TimeSpan.FromSeconds(30), cancellation);
            if (cold.Latest.State != "complete")
            {
                await form.TestClickAsync("暂停");
                await cold.WaitForStatusAsync(s => s.State == "paused", TimeSpan.FromSeconds(15), cancellation);
            }
            Require(form.DetailsText.Contains("修改游戏", StringComparison.Ordinal) && form.DetailsText.Contains("锁定物理转动", StringComparison.Ordinal), "实际运行状态明确显示修改规则与物理转动锁定");
            Require(form.ReplayAvailabilityText.Contains("同进程", StringComparison.Ordinal), "实际修改规则会话显示同进程重新回放可用");

            form.TestModifiedRules(false);
            bool denied = false;
            var before = cold.Latest;
            try { await cold.RestartAsync(form.DraftText, cancellation: cancellation); }
            catch (ArgumentException ex) { denied = ex.Message.Contains("切换游戏规则", StringComparison.Ordinal); }
            Require(denied && cold.RunDirectory == firstRun && cold.Latest.Frame == before.Frame, "Host拒绝同进程切换规则且未修改运行目录或帧边界");
            form.TestModifiedRules(true);
            Require(RulesDraftHeader.Set(form.DraftText, ThothTAS.Core.TasRules.Original) == RulesDraftHeader.Set(initial, ThothTAS.Core.TasRules.Original), "选择回修改规则后输入草稿保持一致");
            form.DraftText += "\n# modified replay keeps a frozen snapshot\n";
            var working = Path.Combine(output, "edited-draft.tas");
            form.TestSavePath(working); await form.TestClickAsync("另存为");
            Require(form.SnapshotText == initial && File.ReadAllText(Path.Combine(firstRun, "script.tas")) == initial, "实际运行时修改保存仍保留不可变运行快照");
            if (cold.Latest.State != "complete") await form.TestClickAsync("播放");
            await cold.Completion.WaitAsync(TimeSpan.FromMinutes(3), cancellation);
            runs.Add(new { pid = firstPid, directory = firstRun, coldProcess = true, community });
            for (int reset = 0; reset < 3; reset++)
            {
                await form.TestSpeedAsync(reset == 1 ? 0.5 : reset == 2 ? 2 : 1);
                var watch = Stopwatch.StartNew();
                await form.TestClickAsync("重新回放");
                var current = form.ActiveSession!;
                var directory = current.RunDirectory;
                var elapsed = watch.Elapsed.TotalMilliseconds;
                Require(ReferenceEquals(cold, current) && current.ProcessId == firstPid && directory != firstRun, "正式重新回放按钮复用同一GameSession与PID并建立新运行记录：" + reset);
                Require(elapsed < 1000, $"正式重新回放按钮恢复并放行播放低于1000ms：{elapsed:F3}ms");
                Require(form.SnapshotText == form.DraftText && File.ReadAllText(Path.Combine(directory, "script.tas")) == form.DraftText, "同进程重新回放使用最新不可变草稿快照：" + reset);
                using (var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "host-run.json"))))
                {
                    Require(!metadata.RootElement.GetProperty("coldProcess").GetBoolean() && metadata.RootElement.GetProperty("rules").GetString() == "deterministic-1", "同进程启动记录明确保存修改规则和复用PID：" + reset);
                }
                if (reset == 1)
                {
                    await current.WaitForStatusAsync(s => s.Frame >= 3 || s.State == "complete", TimeSpan.FromSeconds(15), cancellation);
                    if (current.Latest.State != "complete")
                    {
                        await form.TestClickAsync("暂停");
                        await current.WaitForStatusAsync(s => s.State == "paused", TimeSpan.FromSeconds(15), cancellation);
                        var paused = current.Latest;
                        await Task.Delay(650, cancellation); await current.SendAsync("STATUS", cancellation);
                        await Task.Delay(100, cancellation);
                        Require(current.Latest.Frame == paused.Frame && current.Latest.RandomHash == paused.RandomHash, "同进程重新回放暂停期间帧与随机记录保持不变");
                        await form.TestClickAsync("逐帧");
                        Require(current.Latest.Frame == paused.Frame + 1, "同进程重新回放逐帧放行一个完整输入周期");
                        await form.TestClickAsync("播放");
                    }
                }
                await current.Completion.WaitAsync(TimeSpan.FromMinutes(3), cancellation);
                var difference = ReplayVerifier.Compare(firstRun, directory);
                Require(difference == null, $"修改规则冷启动与同进程重新回放全部玩法/随机/阶段精确相同：{reset}；差异 {difference}");
                runs.Add(new { pid = current.ProcessId, directory, coldProcess = false, community, elapsed });
            }
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))) == inputHash, "原始用户脚本SHA256保持不变");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            try { if (form.ActiveSession != null) await form.TestClickAsync("停止"); } catch { }
            form.TestUnsavedChoice(DialogResult.No); form.Close();
            if (originalSettings != null) File.WriteAllBytes(settings, originalSettings); else if (File.Exists(settings)) File.Delete(settings);
        }
        var report = new { version = 1, passed = error == null, error, checks, runs, inputHash, scope = "真实编辑器与实际游戏：修改规则、原版规则切换保护、不可变快照、同进程按钮恢复耗时、暂停/逐帧/速度；精确一致仅限同一修改规则，与原版THOTH不同" };
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return error == null;
    }
}
