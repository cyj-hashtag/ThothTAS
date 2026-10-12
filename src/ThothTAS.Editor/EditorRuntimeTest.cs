using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ThothTAS.Host;

namespace ThothTAS.Editor;

/// <summary>Runs only when explicitly requested with --self-test-runtime.</summary>
internal static class EditorRuntimeTest
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachInput);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr processHandle, int flags, StringBuilder exeName, ref int size);
    private static readonly HashSet<int> OwnedPids = [];

    private sealed record WindowEvidence(int ProcessId, string Path, long Handle, string Style, int ClientWidth, int ClientHeight, int Left, int Top, int Width, int Height, bool HasCaption, bool CanResize, bool CanMaximize);
    private sealed class Report
    {
        public int Version { get; init; } = 1;
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
        public string Scope { get; init; } = "真实显示的 WinForms 编辑器按钮、独立游戏进程、焦点切换及逐帧玩法/随机日志";
        public bool Passed { get; set; }
        public string? Error { get; set; }
        public string FilePickerCoverage { get; init; } = "打开按钮使用自测注入文件路径，调用正式加载逻辑；不覆盖系统文件选择对话框";
        public List<string> Checks { get; } = [];
        public List<string> Runs { get; } = [];
        public List<object> Events { get; } = [];
        public List<WindowEvidence> Windows { get; } = [];
        public ReplayDifference? Difference { get; set; }
    }

    public static async Task<bool> RunAsync(EditorForm form, string root, string scriptPath, string output, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(output);
        OwnedPids.Clear();
        OwnedPids.Add(Environment.ProcessId);
        var report = new Report();
        await using var events = new StreamWriter(Path.Combine(output, "events.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
        void Event(string operation, GameSession? session = null, object? extra = null)
        {
            var value = new { utc = DateTime.UtcNow, operation, pid = session?.ProcessId, frame = session?.Latest.Frame, scene = session?.Latest.Scene, randomCount = session?.Latest.RandomCount, randomHash = session?.Latest.RandomHash, extra };
            report.Events.Add(value);
            events.WriteLine(JsonSerializer.Serialize(value));
        }
        void Require(bool value, string check)
        {
            if (!value) throw new InvalidDataException(check);
            report.Checks.Add(check);
        }
        async Task Click(string text)
        {
            cancellation.ThrowIfCancellationRequested();
            Event("button:" + text, form.ActiveSession);
            await form.TestClickAsync(text);
        }
        GameSession Session() => form.ActiveSession ?? throw new InvalidOperationException("编辑器未建立游戏会话。");
        try
        {
            var source = await File.ReadAllTextAsync(Path.GetFullPath(scriptPath));
            var parsed = ProjectFiles.Parse(root, source);
            Require(parsed.TotalFrames >= 360, "自测脚本至少 360 帧，为持续焦点/草稿操作留出运行时间");
            var draftPath = Path.Combine(output, "editor-draft.tas");
            await File.WriteAllTextAsync(draftPath, source, new UTF8Encoding(false));
            form.TestOutputParent(Path.Combine(output, "runs"));
            form.TestOpenPath(draftPath);
            await Click("打开");
            await Click("校验");
            Require(form.DraftText.Replace("\r\n", "\n") == source.Replace("\r\n", "\n"), "打开按钮加载原始 UTF-8 脚本内容（编辑器规范化换行）");
            source = form.DraftText;

            // First complete independent process supplies the baseline.
            await Click("播放");
            var baseline = Session();
            OwnedPids.Add(baseline.ProcessId);
            Event("baseline-start", baseline);
            report.Runs.Add(baseline.RunDirectory);
            var timeout = TimeSpan.FromSeconds(Math.Max(180, parsed.TotalFrames / 60.0 * 5 + 90));
            await baseline.Completion.WaitAsync(timeout, cancellation);
            ReplayVerifier.ValidateLogs(baseline.RunDirectory, parsed.TotalFrames);
            var baselineDirectory = baseline.RunDirectory;
            Event("baseline-complete", baseline);
            await Click("停止");

            // Use the real step button to create a fresh paused game and release one frame.
            await Click("逐帧");
            var controlled = Session();
            OwnedPids.Add(controlled.ProcessId);
            report.Runs.Add(controlled.RunDirectory);
            Require(controlled.Latest.Frame == 1, "逐帧按钮首次放行一个完整输入帧");
            var gameWindow = await WaitForGameWindowAsync(controlled);
            var window = InspectWindow(controlled, gameWindow);
            report.Windows.Add(window);
            Require(window.ClientWidth == 1280 && window.ClientHeight == 720, "实际游戏 client 尺寸 1280×720");
            Require(window.HasCaption, "实际游戏窗口有标题边框，未切为全屏");
            Require(!window.CanResize && !window.CanMaximize, "实际游戏窗口不能拖动改变尺寸或最大化");
            await FocusAsync(gameWindow, controlled.ProcessId, cancellation);
            Event("focus:game-paused", controlled, window);
            await CaptureAsync(root, gameWindow, Path.Combine(output, "game-paused.png"));
            await FocusAsync(form.Handle, Environment.ProcessId, cancellation);
            CaptureEditor(form, Path.Combine(output, "editor-paused.png"));

            await Click("暂停");
            await controlled.SendAsync("STATUS");
            await Task.Delay(150, cancellation);
            var paused = controlled.Latest;
            await Task.Delay(600, cancellation);
            await controlled.SendAsync("STATUS");
            await Task.Delay(150, cancellation);
            Require(controlled.Latest.Frame == paused.Frame && controlled.Latest.RandomCount == paused.RandomCount && controlled.Latest.RandomHash == paused.RandomHash, "暂停 600ms 不推进帧或随机序列");
            Event("pause-stable", controlled);

            await form.TestSpeedAsync(0.5);
            Event("speed-combobox:0.5", controlled);
            for (int i = 0; i < 4; i++)
            {
                int before = controlled.Latest.Frame;
                await Click("逐帧");
                Require(controlled.Latest.Frame == before + 1, "逐帧按钮精确增加一帧：" + (before + 1));
                Event("step-complete", controlled);
            }
            await Click("播放");
            await controlled.WaitForStatusAsync(s => s.Frame >= 10 || s.State == "complete", TimeSpan.FromSeconds(30), cancellation);
            Require(controlled.Latest.State != "complete", "开始焦点/草稿操作时回放仍在运行");

            int changedSeed = parsed.Seed == int.MaxValue ? 1 : parsed.Seed + 1;
            var changedDraft = Regex.IsMatch(source, @"(?m)^\s*Seed\s*,")
                ? Regex.Replace(source, @"(?m)^\s*Seed\s*,[^\r\n]*", "Seed," + changedSeed.ToString(CultureInfo.InvariantCulture))
                : Regex.Replace(source, @"(?m)^(\s*Start\s*,[^\r\n]*)", "$1\nSeed," + changedSeed.ToString(CultureInfo.InvariantCulture));
            var changedPath = Path.Combine(output, "changed-draft.tas");
            await File.WriteAllTextAsync(changedPath, changedDraft, new UTF8Encoding(false));
            for (int i = 0; i < 6; i++)
            {
                Require(controlled.Latest.State != "complete", "焦点切换期间回放仍在运行：" + i);
                await FocusAsync(gameWindow, controlled.ProcessId, cancellation);
                Event("focus:game", controlled, new { iteration = i, foreground = GetForegroundWindow().ToInt64() });
                // These are physical key events directed only at the owned foreground game.
                SendKeys.SendWait(i % 2 == 0 ? "{RIGHT}" : "{UP}");
                Event("physical-key-injected", controlled, new { key = i % 2 == 0 ? "RIGHT" : "UP" });
                await Task.Delay(120, cancellation);
                await FocusAsync(form.Handle, Environment.ProcessId, cancellation);
                Event("focus:editor", controlled, new { iteration = i, foreground = GetForegroundWindow().ToInt64() });
                if (i == 0)
                {
                    form.TestOpenPath(changedPath);
                    await Click("打开");
                    form.DraftText += "\n# Editor runtime self-test saved draft while replay active\n";
                    await Click("保存");
                    Require(await File.ReadAllTextAsync(changedPath) == form.DraftText, "播放期间保存按钮确实更新草稿文件");
                    Require(controlled.Script.Seed == parsed.Seed && await File.ReadAllTextAsync(Path.Combine(controlled.RunDirectory, "script.tas")) == source, "打开/编辑/保存草稿未改变当前不可变回放快照");
                    Event("draft-open-edit-save", controlled, new { originalSeed = parsed.Seed, draftSeed = changedSeed, path = changedPath });
                }
                var currentWindow = InspectWindow(controlled, gameWindow);
                report.Windows.Add(currentWindow);
                Require(currentWindow.ClientWidth == 1280 && currentWindow.ClientHeight == 720 && currentWindow.HasCaption && !currentWindow.CanResize && !currentWindow.CanMaximize, "切换焦点后仍为固定尺寸有边框 1280×720 窗口：" + i);
                await Task.Delay(120, cancellation);
            }
            await Click("暂停");
            await controlled.WaitForStatusAsync(s => s.State == "paused" || s.State == "complete", TimeSpan.FromSeconds(30), cancellation);
            Require(controlled.Latest.State == "paused", "播放中暂停按钮在帧边界暂停");
            var secondPause = controlled.Latest;
            await Task.Delay(600, cancellation);
            await controlled.SendAsync("STATUS");
            await Task.Delay(100, cancellation);
            Require(controlled.Latest.Frame == secondPause.Frame && controlled.Latest.RandomCount == secondPause.RandomCount && controlled.Latest.RandomHash == secondPause.RandomHash, "播放中暂停不推进随机序列");
            await form.TestSpeedAsync(2);
            Event("speed-combobox:2", controlled);
            await Click("播放");
            await controlled.WaitForStatusAsync(s => s.Frame >= secondPause.Frame + 8 || s.State == "complete", TimeSpan.FromSeconds(30), cancellation);
            await form.TestSpeedAsync(1);
            Event("speed-combobox:1", controlled);
            await controlled.Completion.WaitAsync(timeout, cancellation);
            ReplayVerifier.ValidateLogs(controlled.RunDirectory, parsed.TotalFrames);
            report.Difference = ReplayVerifier.Compare(baselineDirectory, controlled.RunDirectory);
            Require(report.Difference == null, "连续基线与编辑器暂停/逐帧/速度/焦点/物理键/草稿干扰回放逐帧及完整随机记录一致");
            Event("controlled-complete-compared", controlled);
            CaptureEditor(form, Path.Combine(output, "editor-controlled-complete.png"));

            // Replay must pick up the saved changed draft, not reuse the old snapshot.
            await Click("重新回放");
            var replayed = Session();
            report.Runs.Add(replayed.RunDirectory);
            Require(replayed.Script.Seed == changedSeed, "重新回放按钮应用修改后的草稿种子");
            Require(await File.ReadAllTextAsync(Path.Combine(replayed.RunDirectory, "script.tas")) == form.DraftText, "重新回放冻结当前草稿的新快照");
            await replayed.Completion.WaitAsync(timeout, cancellation);
            ReplayVerifier.ValidateLogs(replayed.RunDirectory, parsed.TotalFrames);
            Event("changed-draft-replay-complete", replayed);
            await Click("停止");
            report.Passed = true;
        }
        catch (Exception ex)
        {
            report.Error = ex.ToString();
            report.Passed = false;
            Event("failed", form.ActiveSession, new { error = ex.Message });
            try { if (form.ActiveSession != null) await form.TestClickAsync("停止"); } catch (Exception stopError) { Event("stop-failed", null, stopError.Message); }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "editor-runtime-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        var md = new StringBuilder("# 编辑器实际运行验证\n\n");
        md.AppendLine("结果：" + (report.Passed ? "通过" : "未通过")).AppendLine();
        md.AppendLine("范围：" + report.Scope).AppendLine();
        md.AppendLine(report.FilePickerCoverage).AppendLine();
        foreach (var check in report.Checks) md.AppendLine("- " + check);
        md.AppendLine();
        foreach (var run in report.Runs) md.AppendLine("运行日志目录：`" + run + "`\n");
        if (report.Difference != null) md.AppendLine("首个分歧：" + JsonSerializer.Serialize(report.Difference));
        if (report.Error != null) md.AppendLine("```text\n" + report.Error + "\n```");
        md.AppendLine("\n此结果只覆盖本次自测脚本与上列实际操作；其他关卡、路径及跨电脑一致性未由此验证。截图应结合逐帧日志读取，画面相似本身不证明确定性。");
        await File.WriteAllTextAsync(Path.Combine(output, "editor-runtime-report.md"), md.ToString(), new UTF8Encoding(false));
        return report.Passed;
    }

    private static async Task<IntPtr> WaitForGameWindowAsync(GameSession session)
    {
        using var process = Process.GetProcessById(session.ProcessId);
        for (int i = 0; i < 200; i++)
        {
            if (process.HasExited) throw new InvalidOperationException("游戏在窗口检查前退出。");
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
            await Task.Delay(50);
        }
        throw new TimeoutException("未找到本工具启动的游戏可见窗口。");
    }

    private static WindowEvidence InspectWindow(GameSession session, IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);
        if (pid != session.ProcessId) throw new InvalidOperationException("窗口不属于本工具启动的游戏进程。");
        if (!GetClientRect(window, out var client) || !GetWindowRect(window, out var rect)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        using var process = Process.GetProcessById(session.ProcessId);
        var path = new StringBuilder(32768); int size = path.Capacity;
        if (!QueryFullProcessImageName(process.Handle, 0, path, ref size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var actual = Path.GetFullPath(path.ToString());
        var expected = Path.GetFullPath(Path.Combine(session.Root, "game", "THOTH.exe"));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("游戏窗口进程路径不匹配：" + actual);
        var style = GetWindowLongPtr(window, -16).ToInt64();
        return new(session.ProcessId, actual, window.ToInt64(), "0x" + style.ToString("X"), client.Right - client.Left, client.Bottom - client.Top, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, (style & 0x00C00000) == 0x00C00000, (style & 0x00040000) != 0, (style & 0x00010000) != 0);
    }

    internal static void PrepareFocusOwners(params int[] pids) { OwnedPids.Clear(); foreach (var pid in pids) OwnedPids.Add(pid); }

    internal static async Task FocusAsync(IntPtr window, int expectedPid, CancellationToken cancellation = default)
    {
        GetWindowThreadProcessId(window, out var targetPid);
        if (targetPid != expectedPid || !OwnedPids.Contains(expectedPid)) throw new InvalidOperationException("拒绝操作本测试未启动的窗口。");
        SetForegroundWindow(window);
        // A background launch may need one user click before Windows allows foreground changes.
        // Wait without sending input to another application; owned-window failures stay bounded.
        var activationDeadline = DateTime.UtcNow.AddMinutes(3);
        int ownedAttempts = 0;
        while (DateTime.UtcNow < activationDeadline)
        {
            cancellation.ThrowIfCancellationRequested();
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid == expectedPid) return;
            // Join only the input queues of the two owned applications. This does not synthesize Alt-Tab
            // into another app or bypass integrity/elevation boundaries.
            if (OwnedPids.Contains((int)pid))
            {
                if (++ownedAttempts > 20) break;
                uint currentThread = GetCurrentThreadId();
                bool attached = currentThread != foregroundThread && AttachThreadInput(currentThread, foregroundThread, true);
                try { SetForegroundWindow(window); }
                finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
            }
            await Task.Delay(50, cancellation);
        }
        GetWindowThreadProcessId(GetForegroundWindow(), out var actualPid);
        throw new InvalidOperationException("Windows 未将焦点切到预期的本任务进程：" + expectedPid + "，实际foreground PID=" + actualPid);
    }

    private static void CaptureEditor(EditorForm form, string path)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
        bitmap.Save(path);
    }

    internal static async Task CaptureAsync(string root, IntPtr window, string path)
    {
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "scripts", "capture-window.ps1"), "-WindowHandle", window.ToInt64().ToString(CultureInfo.InvariantCulture), "-Path", path }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动本机截图辅助程序。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await stdout;
        if (process.ExitCode != 0 || !File.Exists(path)) throw new InvalidOperationException("游戏窗口截图失败：" + await stderr);
    }
}
