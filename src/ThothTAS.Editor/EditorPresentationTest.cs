using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThothTAS.Host;
using ThothTAS.Core;

namespace ThothTAS.Editor;

internal static class EditorPresentationTest
{
    internal static async Task<bool> RunAsync(EditorForm form, string root, string first, string second, string output, CancellationToken cancellation)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var runs = new List<string>();
        string? error = null;
        var settingsPath = Path.Combine(root, "work", "editor-settings.json");
        var oldSettings = File.Exists(settingsPath) ? await File.ReadAllBytesAsync(settingsPath, cancellation) : null;
        var sources = new[] { Path.GetFullPath(first), Path.GetFullPath(second) };
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var sourceHashes = sources.Select(Hash).ToArray();
        void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); checks.Add(message); }
        void Capture(string name)
        {
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(Path.Combine(output, name));
        }
        try
        {
            form.TestOutputParent(Path.Combine(output, "runs"));
            form.TestLanguage(false);
            var practiceCopy = Path.Combine(output, "practice-source.tas");
            File.Copy(sources[0], practiceCopy);
            form.TestOpenPath(practiceCopy);
            await form.TestClickAsync("打开");
            await form.TestClickAsync("逐帧");
            var session = form.ActiveSession!;
            runs.Add(session.RunDirectory);
            form.TestShowSnapshot();
            for (int i = 1; i < 34; i++) await form.TestClickAsync("逐帧");
            await Task.Delay(150, cancellation);
            Require(session.Latest.Frame == 34 && session.Latest.SegmentFrame == 34, "实际完整逐帧到34帧，当前输入段本地帧为34");
            Require(form.SnapshotAnnotation == " --- 34", "高亮行末旁显示 --- 34");
            Require(form.SnapshotAnnotationVisible, "实际运行快照页的行旁帧标记可见");
            form.TestShowDraft();
            await Task.Delay(80, cancellation);
            Require(form.DraftAnnotation == " --- 34" && form.DraftAnnotationVisible, "实际草稿页在未改稿时可见同一行旁标记");
            form.TestShowSnapshot();
            Require(form.AnnotationColor == Color.FromArgb(25, 99, 190), "段内帧标记使用蓝色RGB(25,99,190)，区别于黑色脚本与黄色高亮");
            Require(form.SnapshotText == form.DraftText && form.SnapshotText == File.ReadAllText(Path.Combine(session.RunDirectory, "script.tas")), "显示标记没有插入脚本文本、运行快照或磁盘script.tas");
            Capture("inline-frame-zh.png");
            var before = session.Latest;
            form.TestLanguage(true);
            Require(form.EnglishLanguage && form.Text.Contains("THOTH TAS Editor") && form.StatusText.Contains("Frame 34") && form.DetailsText.Contains("Player world XY"), "实际语言下拉框切到English并刷新标题、状态与详情");
            Require(EditorText.LoadEnglish(root), "English选项实际持久化到编辑器设置");
            using (var reopened = new EditorForm(root, sources[0])) Require(reopened.EnglishLanguage, "重新构造编辑器读取持久化语言English");
            Require(form.SnapshotAnnotation == " --- 34", "英文切换保留同一行帧标记和运行快照");
            Capture("inline-frame-en.png");
            await Task.Delay(400, cancellation);
            await session.SendAsync("STATUS", cancellation);
            await Task.Delay(100, cancellation);
            Require(session.Latest.Frame == before.Frame && session.Latest.RandomCount == before.RandomCount && session.Latest.RandomHash == before.RandomHash, "语言切换期间实际暂停帧与随机序列保持不变");
            form.TestLanguage(false);
            Require(form.StatusText.Contains("帧 34") && form.DetailsText.Contains("玩家世界 XY"), "切回简体中文无需重启游戏");
            form.DraftText += "\n# presentation-only edited draft\n";
            Require(form.DraftAnnotation.Length == 0 && form.SnapshotAnnotation == " --- 34", "修改草稿清除草稿帧标记；冻结运行快照保留当前标记");
            var savedDraft = Path.Combine(output, "edited-draft.tas");
            form.TestSavePath(savedDraft);
            await form.TestClickAsync("另存为");
            Require(!File.ReadAllText(savedDraft).Contains("--- 34"), "保存草稿不写入可视帧标记");
            await form.TestClickAsync("播放");
            await session.Completion.WaitAsync(TimeSpan.FromMinutes(3), cancellation);
            ReplayVerifier.ValidateLogs(session.RunDirectory, session.Latest.Frame);
            await form.TestClickAsync("停止");
            Require(form.SnapshotAnnotation.Length == 0, "停止后清除运行行旁标记");

            form.TestLanguage(true);
            using (var ordering = new ComposeOrderForm(sources, new EditorText { English = true }))
            {
                ordering.Shown += async (_, _) =>
                {
                    try
                    {
                        ordering.TestMove(1);
                        Require(ordering.OrderedPaths.SequenceEqual(sources.Reverse()), "真实拼接窗口下移按钮改变来源顺序");
                        ordering.TestMove(-1);
                        ordering.TestRemove();
                        ordering.TestAdd(sources[0]);
                        ordering.TestMove(1);
                        Require(ordering.OrderedPaths.SequenceEqual(sources), "拼接窗口上移、移除、添加恢复自然关卡顺序");
                        await Task.Delay(100, cancellation);
                        using var image = new Bitmap(ordering.Width, ordering.Height);
                        ordering.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                        image.Save(Path.Combine(output, "compose-window-en.png"));
                        ordering.TestAccept();
                    }
                    catch (Exception ex) { error = ex.ToString(); ordering.DialogResult = DialogResult.Cancel; ordering.Close(); }
                };
                Require(ordering.ShowDialog(form) == DialogResult.OK && error == null, "实际显示的英文拼接窗口通过生成按钮确认顺序");
            }
            form.TestComposePaths(sources);
            await form.TestClickAsync("拼接脚本");
            var composed = form.DraftText;
            Require(ProjectFiles.Parse(root, composed).Chapters.Count == 2 && form.StatusText.Contains("Composed draft"), "正式Compose按钮生成两关Room草稿并显示英文提示");
            for (int i = 0; i < 2; i++)
            {
                if (i != 0) await form.TestClickAsync("停止");
                await form.TestClickAsync("播放");
                var joined = form.ActiveSession!;
                runs.Add(joined.RunDirectory);
                if (i == 0)
                {
                    await joined.WaitForStatusAsync(s => s.Frame >= 10, TimeSpan.FromSeconds(30), cancellation);
                    form.TestLanguage(false);
                    form.TestLanguage(true);
                }
                await joined.Completion.WaitAsync(TimeSpan.FromMinutes(3), cancellation);
                ReplayVerifier.ValidateLogs(joined.RunDirectory, joined.Latest.Frame);
                Require(joined.Latest.Frame == 495 && form.RoomEventItems.Any(x => x.Contains("Completed") && x.Contains("495")), "新版编辑器实际拼接完成495帧，英文过关信息正确：" + i);
                if (i == 1) Require(ReplayVerifier.Compare(runs[^2], runs[^1]) == null, "播放中切换语言与连续播放两冷进程逐帧、完整随机记录精确一致");
            }
            await form.TestClickAsync("停止");
            Require(sourceHashes.SequenceEqual(sources.Select(Hash)), "用户两个原始TAS文件SHA256保持不变");
            var probeSource = form.DraftText;
            form.DraftText = "Version,1\nStart,UnknownLevel\n1,N\n";
            try { await form.TestClickAsync("校验"); } catch (InvalidOperationException) { }
            Require(form.StatusText.StartsWith("Error:") && form.StatusText.Contains("Unknown level") && form.StatusText.Contains("Line 2"), "英文校验错误包含英文原因与准确行号");
            form.DraftText = probeSource;
        }
        catch (Exception ex) { error = ex.ToString(); try { if (form.ActiveSession != null) await form.TestClickAsync("停止"); } catch { } }
        finally
        {
            if (oldSettings != null) await File.WriteAllBytesAsync(settingsPath, oldSettings, CancellationToken.None);
            else if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
        var report = new { version = 1, passed = error == null, error, checks, runs, sourceHashes,
            captureKind = "WinForms-control-render", scope = "实际游戏段内帧标记、双语实时切换/保存设置、真实拼接窗口与两冷进程完整回放", seams = "文件选择路径注入；正式按钮与真实排序窗执行；不覆盖系统文件选择对话框。控件图片不证明桌面焦点。" };
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), "# 行旁帧标记、拼接窗口和双语实际验证\n\n结果：" + (error == null ? "通过" : "未通过") + "\n\n" + string.Join("\n", checks.Select(x => "- " + x)) + "\n\n原始运行目录：\n\n" + string.Join("\n", runs.Select(x => "- `" + x + "`")) + "\n\n截图为WinForms控件渲染，不证明桌面焦点；文件选择路径由测试注入。\n\n" + error, new UTF8Encoding(false));
        return error == null;
    }
}
