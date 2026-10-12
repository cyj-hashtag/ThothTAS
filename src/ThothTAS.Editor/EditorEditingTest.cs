using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThothTAS.Host;

namespace ThothTAS.Editor;

internal static class EditorEditingTest
{
    public static async Task<bool> RunAsync(EditorForm form, string root, string firstPath, string secondPath, string output, CancellationToken cancellation)
    {
        Directory.CreateDirectory(output);
        var utf8 = new UTF8Encoding(false);
        var checks = new List<string>();
        var runs = new List<object>();
        var events = new List<object>();
        string? error = null;
        bool passed = false;
        void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); checks.Add(message); }
        void Event(string operation, GameSession? owned = null) => events.Add(new { utc = DateTime.UtcNow, operation, pid = owned?.ProcessId, run = owned?.RunDirectory, frame = owned?.Latest.Frame, line = owned?.Latest.Line });
        async Task Click(string text) { cancellation.ThrowIfCancellationRequested(); Event("button:" + text, form.ActiveSession); await form.TestClickAsync(text); }
        GameSession Session() => form.ActiveSession ?? throw new InvalidOperationException("编辑器没有活动会话。");
        async Task ShowAndCapture(string name, GameSession owned)
        {
            form.TestShowSnapshot();
            await Task.Delay(100, cancellation);
            try
            {
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(output, name + ".png"));
                events.Add(new { utc = DateTime.UtcNow, operation = "optional-editor-capture", captureKind = "WinForms-control-render", path = Path.Combine(output, name + ".png"), pid = owned.ProcessId });
            }
            catch (Exception ex)
            {
                events.Add(new { utc = DateTime.UtcNow, operation = "optional-editor-capture-skipped", captureKind = "WinForms-control-render", error = ex.Message, pid = owned.ProcessId });
            }
        }
        void CheckDisplayedInput(GameSession owned)
        {
            var s = owned.Latest;
            Require(s.Line > 0 && form.SnapshotHighlightedLine == s.Line, "运行快照实时高亮实际 SourceLine：" + s.Line);
            Require(s.SegmentFrames > 0 && s.SegmentFrame > 0 && form.SegmentText.Contains($"{s.SegmentFrame} / {s.SegmentFrames}"), "输入段进度与实际 segmentFrame/segmentFrames 一致");
        }
        var inputPaths = new[] { Path.GetFullPath(firstPath), Path.GetFullPath(secondPath) };
        var originalHashes = inputPaths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        try
        {
            var source = await File.ReadAllTextAsync(inputPaths[0], cancellation);
            var workingDraft = Path.Combine(output, "editor-draft.tas");
            await File.WriteAllTextAsync(workingDraft, source, utf8, cancellation);
            form.TestOutputParent(Path.Combine(output, "runs"));
            form.TestOpenPath(workingDraft);
            await Click("打开");
            await Click("校验");
            await Click("逐帧");
            var cold = Session();
            runs.Add(new { cold.ProcessId, cold.RunDirectory, coldProcess = true });
            await Task.Delay(100, cancellation);
            CheckDisplayedInput(cold);
            Require(form.DraftHighlightedLine == cold.Latest.Line, "未改草稿同步显示当前执行行");
            var frozen = form.SnapshotText;
            form.DraftText += "\n# 编辑专项自测：播放草稿修改不改变运行快照\n";
            await Click("保存");
            Require(form.SnapshotText == frozen && form.DraftHighlightedLine == 0 && form.SnapshotHighlightedLine == cold.Latest.Line, "改稿后仅运行快照保持准确高亮，草稿取消执行行标记");
            await ShowAndCapture("snapshot-after-draft-edit", cold);
            await Click("播放");
            await cold.Completion.WaitAsync(TimeSpan.FromSeconds(120), cancellation);
            await Task.Delay(150, cancellation);
            Require(cold.Latest.RoomEvents.Any(e => e.Type == "completed" && e.Scene == "Basic-1" && e.Frame == 147), "实际过关事件 Basic-1 / frame147 可见");
            Require(cold.Latest.RoomEvents.Any(e => e.Type == "entered" && e.Scene == "Basic-2" && e.Frame == 273), "实际玩家激活入场事件 Basic-2 / frame273 可见");
            Require(form.RoomEventItems.Any(text => text.Contains("过关 · 帧 147 · Basic-1")) && form.RoomEventItems.Any(text => text.Contains("入场 · 帧 273 · Basic-2")), "编辑器过关/入场列表展示实际事件");
            Event("cold-complete", cold);

            var pid = cold.ProcessId;
            var previousRun = cold.RunDirectory;
            var resetStates = new ConcurrentQueue<GameStatus>();
            cold.StatusChanged += resetStates.Enqueue;
            form.TestFastReset(true);
            await Click("重新回放");
            var warm = Session();
            runs.Add(new { warm.ProcessId, warm.RunDirectory, coldProcess = false });
            Require(warm.ProcessId == pid && warm.RunDirectory != previousRun, "同进程快速重置保留 PID 并创建新 run 目录");
            Require(resetStates.Any(s => s.Run == warm.RunDirectory && s.State == "paused" && s.Frame == 0), "快速重置实际回到新 run 的 frame0 暂停基线后放行播放");
            Require(await File.ReadAllTextAsync(Path.Combine(warm.RunDirectory, "script.tas"), cancellation) == form.DraftText, "快速重置使用新的不可变草稿快照");
            await warm.Completion.WaitAsync(TimeSpan.FromSeconds(120), cancellation);
            cold.StatusChanged -= resetStates.Enqueue;
            await ShowAndCapture("quick-reset-complete", warm);
            await Click("停止");
            form.TestFastReset(false);

            form.TestComposePaths(inputPaths);
            await Click("拼接脚本");
            var combined = ProjectFiles.Parse(root, form.DraftText);
            Require(combined.HasExplicitRooms && combined.Chapters.Count == 2 && combined.Chapters[0].Scene == "Basic-1" && combined.Chapters[1].Scene == "Basic-2", "真实拼接按钮按选定顺序生成两关 Room 章节");
            var composedPath = Path.Combine(output, "editor-combined.tas");
            form.TestSavePath(composedPath);
            await Click("保存");
            await Click("校验");
            await Click("播放");
            var joined = Session();
            runs.Add(new { joined.ProcessId, joined.RunDirectory, coldProcess = true });
            await joined.WaitForStatusAsync(s => s.Chapter == 1 && s.ChapterFrame >= 3 && s.Line > 0, TimeSpan.FromSeconds(120), cancellation);
            await Click("暂停");
            await joined.WaitForStatusAsync(s => s.State == "paused", TimeSpan.FromSeconds(20), cancellation);
            await Task.Delay(100, cancellation);
            CheckDisplayedInput(joined);
            Require(joined.Script.GetInputAtChapterFrame(joined.Latest.Chapter, joined.Latest.ChapterFrame - 1, out var chapterLine) != null && chapterLine == joined.Latest.Line, "第二章节高亮源行按章节本地帧索引定位");
            await ShowAndCapture("joined-chapter2-paused", joined);
            await Click("播放");
            await joined.Completion.WaitAsync(TimeSpan.FromSeconds(120), cancellation);
            ReplayVerifier.ValidateLogs(joined.RunDirectory, joined.Latest.Frame);
            Event("joined-complete", joined);
            await Click("停止");

            // A separate active session exercises the real disconnected TCP and UI fault path.
            form.DraftText = "Version,1\nStart,Basic-1\nSeed,1\n\n180,N\n";
            await Click("播放");
            var disconnected = Session();
            runs.Add(new { disconnected.ProcessId, disconnected.RunDirectory, coldProcess = true });
            await disconnected.WaitForStatusAsync(s => s.Frame >= 5 && s.State == "running", TimeSpan.FromSeconds(30), cancellation);
            var tcp = typeof(GameSession).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(disconnected) as TcpClient ?? throw new MissingFieldException("GameSession.client");
            tcp.Dispose();
            Event("actual-owned-tcp-disconnected", disconnected);
            var faultDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!form.ControlFaulted && DateTime.UtcNow < faultDeadline) await Task.Delay(30, cancellation);
            Require(form.ControlFaulted, "实际 TCP 断线即时显示控制连接错误，取消播放/逐帧操作");
            await Task.Delay(500, cancellation);
            var before = ReadLastFrame(disconnected.RunDirectory);
            await Task.Delay(650, cancellation);
            Require(ReadLastFrame(disconnected.RunDirectory) == before, "断线稳定边界后实际帧和随机序列停止推进");
            await ShowAndCapture("actual-disconnection-ui", disconnected);
            await Click("停止");
            Require(inputPaths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).SequenceEqual(originalHashes), "用户两个原始脚本 SHA256 保持不变");
            passed = true;
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { if (form.ActiveSession is { } remaining) { try { await form.TestClickAsync("停止"); } catch { await remaining.DisposeAsync(); } } }
        var report = new { version = 1, passed, error, captureKind = "WinForms-control-render", captureScope = "已显示编辑器的Control.DrawToBitmap控件渲染，不依赖前台焦点；不是桌面像素截图，不验证窗口遮挡或焦点。截图可选，失败只记录。实际焦点切换证据单独见editor-runtime-final-v5。", scope = "实际显示的编辑器高亮、段进度、过关事件、暖重置、拼接和TCP断线。暖重置仅验证练习功能，不保证与冷进程等价。", seams = "打开/保存/拼接选择路径由测试入口注入，仍点击正式按钮；不覆盖系统文件选择或顺序对话框。断线只反射关闭当前拥有的TcpClient。", checks, runs, events, inputPaths, originalHashes };
        await File.WriteAllTextAsync(Path.Combine(output, "editor-editing-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), utf8);
        var markdown = new StringBuilder("# 编辑器新增功能实际自测\n\n结果：" + (passed ? "通过" : "未通过") + "\n\n");
        foreach (var check in checks) markdown.AppendLine("- " + check);
        markdown.AppendLine("\n选择路径为注入接缝；所有正式按钮、加载和保存逻辑执行。暖重置不证明冷进程等价。\n");
        markdown.AppendLine("截图类型：`WinForms-control-render`，通过已显示表单的 Control.DrawToBitmap 渲染控件，不要求前台焦点，不证明桌面遮挡或焦点状态。截图可选，失败只记录；实际焦点切换另见 v5 独立证据。\n");
        if (error != null) markdown.AppendLine("```text\n" + error + "\n```");
        await File.WriteAllTextAsync(Path.Combine(output, "editor-editing-report.md"), markdown.ToString(), utf8);
        return passed;
    }

    private static string ReadLastFrame(string run)
    {
        using var reader = new StreamReader(new FileStream(Path.Combine(run, "frames.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string? last = null, line;
        while ((line = reader.ReadLine()) != null) last = line;
        if (last == null) throw new InvalidDataException("实际帧日志为空。");
        using var doc = JsonDocument.Parse(last);
        return doc.RootElement.GetProperty("frame").GetRawText() + "|" + doc.RootElement.GetProperty("randomCount").GetRawText() + "|" + doc.RootElement.GetProperty("randomHash").GetRawText();
    }
}
