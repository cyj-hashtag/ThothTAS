using System.Text;
using System.Text.Json;
using ThothTAS.Host;

namespace ThothTAS.Editor;

internal static class EditorReplaySafetyTest
{
    internal static async Task<bool> RunAsync(EditorForm owner, string root, string input, string output, CancellationToken cancellation)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>(); var runs = new List<object>();
        string? error = null;
        void Require(bool value, string label) { if (!value) throw new InvalidDataException(label); checks.Add(label); }
        using var form = new EditorForm(root, input, false);
        form.TestOutputParent(Path.Combine(output, "runs"));
        form.Show(owner);
        try
        {
            var text = form.DraftText;
            await form.TestClickAsync("播放");
            var first = form.ActiveSession!;
            await first.WaitForStatusAsync(s => s.Frame >= 20, TimeSpan.FromSeconds(30), cancellation);
            await form.TestClickAsync("暂停");
            await first.WaitForStatusAsync(s => s.State == "paused", TimeSpan.FromSeconds(15), cancellation);
            var before = first.Latest;
            var firstDirectory = first.RunDirectory;
            bool denied = false;
            try { await first.RestartAsync(text, cancellation: cancellation); }
            catch (InvalidOperationException ex) { denied = ex.Message.Contains("已停用"); }
            Require(denied && first.RunDirectory == firstDirectory, "普通Host会话拒绝不安全同进程重置，未创建新运行或改变输入");
            form.DraftText += "\n# unsaved while real game paused\n";
            form.TestUnsavedChoice(DialogResult.Cancel); form.Close();
            Require(!form.IsDisposed && form.ActiveSession == first && form.UnsavedDialogsShown == 1, "真实游戏运行时退出弹窗取消保留编辑器和控制连接");
            await Task.Delay(500, cancellation); await first.SendAsync("STATUS", cancellation); await Task.Delay(100, cancellation);
            Require(first.Latest.Frame == before.Frame && first.Latest.RandomHash == before.RandomHash, "退出取消不改变暂停边界或随机序列");
            form.DraftText = text;
            await form.TestClickAsync("播放"); await first.Completion.WaitAsync(TimeSpan.FromMinutes(3), cancellation);
            runs.Add(new { first.ProcessId, first.RunDirectory, profile = "stock" });
            for (int i = 0; i < 2; i++)
            {
                var previous = form.ActiveSession!;
                var previousPid = previous.ProcessId;
                await form.TestClickAsync("重新回放");
                var next = form.ActiveSession!;
                Require(next.ProcessId != previousPid && next.RunDirectory != previous.RunDirectory, "重新回放实际启动新的独立游戏PID：" + i);
                Require(File.ReadAllText(Path.Combine(next.RunDirectory, "host-run.json")).Contains("\"coldProcess\": true"), "启动记录明确标记独立冷进程：" + i);
                await next.Completion.WaitAsync(TimeSpan.FromMinutes(3), cancellation);
                runs.Add(new { next.ProcessId, next.RunDirectory, profile = "stock" });
                Require(ReplayVerifier.Compare(first.RunDirectory, next.RunDirectory) == null, "Basic-3编辑器暂停取消退出及两次重新回放逐帧字段和完整随机记录精确一致：" + i);
            }
            form.TestCommunity(true);
            await form.TestClickAsync("重新回放");
            var community = form.ActiveSession!;
            var communityPid = community.ProcessId;
            await community.Completion.WaitAsync(TimeSpan.FromMinutes(3), cancellation);
            runs.Add(new { community.ProcessId, community.RunDirectory, profile = "community-v2.8" });
            await form.TestClickAsync("重新回放");
            var repeat = form.ActiveSession!;
            Require(repeat.ProcessId != communityPid, "社区计时器模式的重新回放也使用独立PID");
            await repeat.Completion.WaitAsync(TimeSpan.FromMinutes(3), cancellation);
            runs.Add(new { repeat.ProcessId, repeat.RunDirectory, profile = "community-v2.8" });
            Require(ReplayVerifier.Compare(community.RunDirectory, repeat.RunDirectory) == null, "Basic-3社区计时器模式编辑器重复回放逐帧与随机记录精确一致");
            await repeat.SendAsync("RESTART\t" + Path.Combine(repeat.RunDirectory, "session.cfg"), cancellation);
            await repeat.WaitForStatusAsync(s => s.State == "error", TimeSpan.FromSeconds(10), cancellation);
            Require(repeat.Latest.Error?.Contains("物理失步") == true && repeat.Latest.Run == repeat.RunDirectory, "原始TCP重置命令也被运行模块拒绝，不能绕过普通会话保护");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            try { if (form.ActiveSession != null) await form.TestClickAsync("停止"); } catch { }
            form.TestUnsavedChoice(DialogResult.No); form.Close();
        }
        var report = new { version = 1, passed = error == null, error, checks, runs, scope = "真实编辑器和实际游戏进程；退出取消控制连接；原版及社区Mod重新回放；不代表该脚本成功过关" };
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return error == null;
    }
}
