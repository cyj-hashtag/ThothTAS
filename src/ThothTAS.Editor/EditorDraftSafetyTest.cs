using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ThothTAS.Editor;

internal static class EditorDraftSafetyTest
{
    internal static async Task<bool> RunAsync(EditorForm owner, string root, string input, string output, CancellationToken cancellation)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        string? error = null;
        var settings = Path.Combine(root, "work", "editor-settings.json");
        var originalSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        void Require(bool value, string label) { if (!value) throw new InvalidDataException(label); checks.Add(label); }
        async Task WaitClosed(EditorForm form)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!form.IsDisposed && DateTime.UtcNow < deadline) await Task.Delay(20, cancellation);
            Require(form.IsDisposed, "确认保存或不保存后窗口实际关闭");
        }
        try
        {
            var source = File.ReadAllText(input, Encoding.UTF8);
            var file = Path.Combine(output, "working-copy.tas");
            File.WriteAllText(file, source, new UTF8Encoding(false));
            using (var form = new EditorForm(root, file, false))
            {
                form.Show(owner);
                form.DraftText += "\n# unsaved Chinese 草稿恢复\n";
                var changed = form.DraftText;
                await Task.Delay(700, cancellation);
                Require(File.Exists(form.RecoveryCheckpointPath), "实际键入后350ms自动保存独立恢复文件");
                var stored = new DraftRecoveryStore(root).Read(form.RecoveryCheckpointPath);
                Require(stored.Text == changed && stored.SourcePath == file && stored.ActiveWindow, "检查点保存完整中文文本、源路径和活动窗口身份");
                Require(File.ReadAllText(file) == source, "自动备份不覆盖用户的已保存文档");
                form.TestUnsavedChoice(DialogResult.Cancel);
                form.Close();
                Require(!form.IsDisposed && form.HasUnsavedChanges && form.DraftText == changed && form.UnsavedDialogsShown == 1, "真实退出弹窗点击取消，窗口与未保存内容保留");
                var other = Path.Combine(output, "other.tas");
                File.WriteAllText(other, source.Replace("Seed,1", "Seed,2"));
                form.TestOpenPath(other); form.TestUnsavedChoice(DialogResult.Cancel);
                await form.TestClickAsync("打开");
                Require(form.DraftText == changed && form.UnsavedDialogsShown == 2, "打开其他文档前也显示真实弹窗，取消不丢失草稿");
                form.TestLanguage(true); form.TestUnsavedChoice(DialogResult.Cancel); form.Close();
                Require(!form.IsDisposed && form.HasUnsavedChanges && form.UnsavedDialogsShown == 3, "英文退出弹窗取消同样保留编辑器");
                form.TestUnsavedChoice(DialogResult.Yes); form.Close();
                await WaitClosed(form);
                Require(File.ReadAllText(file) == changed && !File.Exists(form.RecoveryCheckpointPath), "真实退出弹窗保存完整草稿，成功保存后清理本窗口检查点");
            }
            using (var form = new EditorForm(root, file, false))
            {
                form.Show(owner); form.DraftText += "\n# deliberately discard\n";
                form.TestUnsavedChoice(DialogResult.No); form.Close(); await WaitClosed(form);
                Require(File.ReadAllText(file).EndsWith("草稿恢复\n") && !File.Exists(form.RecoveryCheckpointPath), "退出选择不保存不覆盖文档，同时保留手动恢复的历史TAS备份");
            }
            using (var form = new EditorForm(root, null, false))
            {
                form.Show(owner); form.DraftText = source + "\n# untitled save failure\n";
                var checkpoint = form.RecoveryCheckpointPath;
                form.TestSavePath(Path.Combine(output, "missing-directory", "failed.tas"));
                form.TestUnsavedChoice(DialogResult.Yes); form.Close();
                Require(!form.IsDisposed && form.HasUnsavedChanges && File.Exists(checkpoint), "退出选择保存但磁盘写入失败时窗口保持打开，草稿与备份保留");
                var target = Path.Combine(output, "untitled-saved.tas");
                form.TestSavePath(target); form.TestUnsavedChoice(DialogResult.Yes); form.Close(); await WaitClosed(form);
                Require(File.ReadAllText(target).Contains("untitled save failure"), "无路径新草稿退出保存走另存为，成功写入后才关闭");
            }

            // Kill an owned editor process after its ordinary timer has persisted a draft.
            // No FormClosing callback runs, exercising real crash recovery.
            var probeDirectory = Path.Combine(output, "crash-probe");
            Directory.CreateDirectory(probeDirectory);
            var textPath = Path.Combine(probeDirectory, "text.txt");
            var crashText = source + "\n# killed process 恢复负坐标 A=(-3.5,2.25)\n";
            File.WriteAllText(textPath, crashText, new UTF8Encoding(false));
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = root, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "--root", root, "--draft-crash-probe", file, "--draft-textfile", textPath, "--out", probeDirectory }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Crash probe did not start.");
            var proofPath = Path.Combine(probeDirectory, "checkpoint.txt");
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!File.Exists(proofPath) && !process.HasExited && DateTime.UtcNow < deadline) await Task.Delay(50, cancellation);
                Require(File.Exists(proofPath), "独立真实编辑器进程按普通自动备份路径写出草稿");
                process.Kill(); await process.WaitForExitAsync(cancellation);
            }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None); } }
            var checkpointPath = File.ReadAllText(proofPath);
            var recovery = new DraftRecoveryStore(root);
            var recovered = recovery.Read(checkpointPath);
            Require(recovered.Text == crashText && DraftRecoveryStore.Pending(recovered), "强制终止后草稿文本精确恢复，启动恢复检测识别失去进程的检查点");
            using (var form = new EditorForm(root, file, false))
            {
                form.Show(owner); form.TestRecoverPath(checkpointPath); await form.TestClickAsync("恢复草稿");
                Require(form.DraftText == crashText && form.HasUnsavedChanges, "真实恢复按钮恢复完整草稿并标记为未保存");
                Require(!File.Exists(checkpointPath) && File.Exists(Path.Combine(Path.GetDirectoryName(checkpointPath)!, "recovered-" + Path.GetFileName(checkpointPath))), "恢复后的旧检查点归档，避免每次启动重复提醒");
                var saved = Path.Combine(output, "crash-recovered.tas");
                form.TestSavePath(saved); await form.TestClickAsync("另存为");
                Require(File.ReadAllText(saved) == crashText && !File.Exists(form.RecoveryCheckpointPath), "恢复后另存为保存精确文本并清理新检查点");
                form.Close(); await WaitClosed(form);
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            foreach (var form in owner.OwnedForms.OfType<EditorForm>().ToArray()) { form.TestUnsavedChoice(DialogResult.No); form.Close(); }
            if (originalSettings != null) File.WriteAllBytes(settings, originalSettings); else if (File.Exists(settings)) File.Delete(settings);
        }
        var report = new { version = 1, passed = error == null, checks, error, scope = "真实WinForms未保存弹窗与按钮；普通计时器备份；强制终止独立自测编辑器后恢复", seams = "对话框显示后自动点击正式按钮；文件选择路径注入；不操作用户编辑器或原始TAS文件" };
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return error == null;
    }
}
