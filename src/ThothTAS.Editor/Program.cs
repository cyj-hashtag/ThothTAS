using ThothTAS.Host;

namespace ThothTAS.Editor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var rootIndex = Array.IndexOf(args, "--root");
            var root = ProjectFiles.FindRoot(rootIndex >= 0 && rootIndex + 1 < args.Length ? args[rootIndex + 1] : null);
            var scriptIndex = Array.IndexOf(args, "--script");
            var draftProbeIndex = Array.IndexOf(args, "--draft-crash-probe");
            if (draftProbeIndex >= 0)
            {
                var textIndex = Array.IndexOf(args, "--draft-textfile");
                var outputIndex = Array.IndexOf(args, "--out");
                using var probe = new EditorForm(root, args[draftProbeIndex + 1], false);
                using var timer = new System.Windows.Forms.Timer { Interval = 900 };
                probe.Shown += (_, _) => { probe.DraftText = File.ReadAllText(args[textIndex + 1]); timer.Start(); };
                timer.Tick += (_, _) =>
                {
                    if (!File.Exists(probe.RecoveryCheckpointPath)) return;
                    timer.Stop(); File.WriteAllText(Path.Combine(args[outputIndex + 1], "checkpoint.txt"), probe.RecoveryCheckpointPath);
                };
                Application.Run(probe); return;
            }
            var safetyIndex = Array.IndexOf(args, "--self-test-draft-safety");
            var rulesIndex = Array.IndexOf(args, "--self-test-rules");
            var modifiedReplayIndex = Array.IndexOf(args, "--self-test-modified-replay");
            var replaySafetyIndex = Array.IndexOf(args, "--self-test-replay-safety");
            var testIndex = Array.IndexOf(args, "--self-test-runtime");
            var editingIndex = Array.IndexOf(args, "--self-test-editing");
            var communityIndex = Array.IndexOf(args, "--self-test-community");
            var presentationIndex = Array.IndexOf(args, "--self-test-presentation");
            if (testIndex >= 0 || editingIndex >= 0 || presentationIndex >= 0 || communityIndex >= 0 || safetyIndex >= 0 || replaySafetyIndex >= 0 || rulesIndex >= 0 || modifiedReplayIndex >= 0)
            {
                var selectedTest = modifiedReplayIndex >= 0 ? modifiedReplayIndex : rulesIndex >= 0 ? rulesIndex : replaySafetyIndex >= 0 ? replaySafetyIndex : safetyIndex >= 0 ? safetyIndex : communityIndex >= 0 ? communityIndex : presentationIndex >= 0 ? presentationIndex : editingIndex >= 0 ? editingIndex : testIndex;
                if (selectedTest + 1 >= args.Length) throw new ArgumentException("自测缺少脚本路径。");
                if ((editingIndex >= 0 || presentationIndex >= 0 || communityIndex >= 0) && (selectedTest + 2 >= args.Length || args[selectedTest + 2].StartsWith("--"))) throw new ArgumentException("此自测需要两个按关卡顺序排列的脚本路径。");
                var outIndex = Array.IndexOf(args, "--out");
                var output = Path.GetFullPath(outIndex >= 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : Path.Combine(root, "work", "editor-runtime-test", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
                using var form = new EditorForm(root, args[selectedTest + 1], false);
                using var testCancellation = new CancellationTokenSource();
                bool testEnded = false;
                Environment.ExitCode = 2;
                form.FormClosing += (_, e) => { if (!testEnded) { e.Cancel = true; testCancellation.Cancel(); } };
                form.Shown += async (_, _) =>
                {
                    var passed = modifiedReplayIndex >= 0
                        ? await EditorModifiedReplayTest.RunAsync(form, root, args[modifiedReplayIndex + 1], output, args.Contains("--community-mod"), testCancellation.Token)
                        : rulesIndex >= 0
                        ? await EditorRulesTest.RunAsync(form, root, args[rulesIndex + 1], output, testCancellation.Token)
                        : replaySafetyIndex >= 0
                        ? await EditorReplaySafetyTest.RunAsync(form, root, args[replaySafetyIndex + 1], output, testCancellation.Token)
                        : safetyIndex >= 0
                        ? await EditorDraftSafetyTest.RunAsync(form, root, args[safetyIndex + 1], output, testCancellation.Token)
                        : communityIndex >= 0
                        ? await EditorCommunityTest.RunAsync(form, root, args[communityIndex + 1], args[communityIndex + 2], output, testCancellation.Token)
                        : presentationIndex >= 0
                        ? await EditorPresentationTest.RunAsync(form, root, args[presentationIndex + 1], args[presentationIndex + 2], output, testCancellation.Token)
                        : editingIndex >= 0
                        ? await EditorEditingTest.RunAsync(form, root, args[editingIndex + 1], args[editingIndex + 2], output, testCancellation.Token)
                        : await EditorRuntimeTest.RunAsync(form, root, args[testIndex + 1], output, testCancellation.Token);
                    Environment.ExitCode = passed ? 0 : 2;
                    testEnded = true;
                    form.TestUnsavedChoice(DialogResult.No);
                    form.Close();
                };
                Application.Run(form);
            }
            else Application.Run(new EditorForm(root, scriptIndex >= 0 && scriptIndex + 1 < args.Length ? args[scriptIndex + 1] : null));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "THOTH TAS 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
