using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThothTAS.Core;
using ThothTAS.Host;

namespace ThothTAS.Editor;

/// <summary>Exercises real draft controls without starting the game.</summary>
internal static class EditorRulesTest
{
    internal static async Task<bool> RunAsync(EditorForm owner, string root, string input, string output, CancellationToken cancellation)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        string? error = null;
        var inputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));
        var settings = Path.Combine(root, "work", "editor-settings.json");
        var originalSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); checks.Add(message); }
        var source = RulesDraftHeader.Set(File.ReadAllText(input, Encoding.UTF8), TasRules.Original);
        var original = Path.Combine(output, "original.tas");
        var modified = Path.Combine(output, "modified.tas");
        File.WriteAllText(original, source, new UTF8Encoding(false));
        File.WriteAllText(modified, RulesDraftHeader.Set(source, TasRules.Deterministic1), new UTF8Encoding(false));
        using var form = new EditorForm(root, original, false);
        form.Show(owner);
        try
        {
            using (var fresh = new EditorForm(root, null, false))
            {
                Require(fresh.ModifiedRulesChecked && ProjectFiles.Parse(root, fresh.DraftText).Rules == TasRules.Deterministic1, "新建未命名模板明确使用Deterministic-1修改规则");
            }
            Require(!form.ModifiedRulesChecked && !form.HasUnsavedChanges && ProjectFiles.Parse(root, form.DraftText).Rules == TasRules.Original, "打开旧脚本保留原版规则、不产生未保存修改");
            var normalized = form.DraftText;
            var frames = ProjectFiles.Parse(root, normalized).TotalFrames;
            form.TestModifiedRules(true);
            Require(form.ModifiedRulesChecked && form.HasUnsavedChanges && ProjectFiles.Parse(root, form.DraftText).Rules == TasRules.Deterministic1, "正式规则复选框写入修改规则头并触发未保存标记");
            Require(form.DraftText.Split('\n').Count(line => line.Trim() == "Rules,Deterministic-1") == 1 && ProjectFiles.Parse(root, form.DraftText).TotalFrames == frames, "切换规则仅写一个头部，不改变输入预算");
            await Task.Delay(700, cancellation);
            Require(new DraftRecoveryStore(root).Read(form.RecoveryCheckpointPath).Text == form.DraftText && File.ReadAllText(original) == source, "规则修改进入普通自动恢复备份，不覆盖保存文件");
            form.TestUnsavedChoice(DialogResult.Cancel); form.Close();
            Require(!form.IsDisposed && form.ModifiedRulesChecked && form.UnsavedDialogsShown == 1, "规则转换后退出弹窗取消保留草稿");
            form.TestModifiedRules(false);
            Require(form.DraftText == normalized && !form.HasUnsavedChanges && !form.ModifiedRulesChecked, "取消修改规则恢复原稿及未修改状态");
            form.TestOpenPath(modified); await form.TestClickAsync("打开");
            Require(form.ModifiedRulesChecked && !form.HasUnsavedChanges, "打开修改规则脚本同步复选框，不重写文本");
            form.TestLanguage(true);
            Require(form.ReplayAvailabilityText.Contains("Modified rules", StringComparison.Ordinal), "英文显示修改规则与首次加载说明");
            form.TestLanguage(false);
            Require(form.ReplayAvailabilityText.Contains("确定性规则", StringComparison.Ordinal), "简体中文显示修改规则与首次加载说明");

            var recovery = new DraftRecoveryStore(root);
            recovery.Write(modified, File.ReadAllText(modified) + "\n# recovery rules\n");
            form.TestOpenPath(original); await form.TestClickAsync("打开");
            form.TestRecoverPath(recovery.CheckpointPath); await form.TestClickAsync("恢复草稿");
            Require(form.ModifiedRulesChecked && form.HasUnsavedChanges && form.DraftText.Contains("# recovery rules", StringComparison.Ordinal), "恢复草稿按恢复文本同步修改规则和未保存标记");
            form.TestSavePath(Path.Combine(output, "recovered.tas")); await form.TestClickAsync("另存为");

            var levels = File.ReadAllLines(Path.Combine(root, "docs", "levels.txt")).Where(line => line.Length != 0 && !line.StartsWith('#')).ToList();
            var first = ProjectFiles.Parse(root, source).Start;
            var second = levels[levels.IndexOf(first) + 1];
            var next = Path.Combine(output, "modified-next.tas");
            File.WriteAllText(next, $"Version,1\nRules,Deterministic-1\nStart,{second}\nSeed,{ProjectFiles.Parse(root, source).Seed}\n\n5,N\n", new UTF8Encoding(false));
            form.TestComposePaths(modified, next); await form.TestClickAsync("拼接脚本");
            Require(form.ModifiedRulesChecked && ProjectFiles.Parse(root, form.DraftText).Rules == TasRules.Deterministic1 && ProjectFiles.Parse(root, form.DraftText).Chapters.Count == 2, "拼接同规则文件保留修改规则并同步复选框");
            var composed = form.DraftText;
            form.TestUnsavedChoice(DialogResult.No);
            form.TestComposePaths(original, next);
            bool mixedRejected = false;
            try { await form.TestClickAsync("拼接脚本"); }
            catch (InvalidOperationException) { mixedRejected = true; }
            Require(mixedRejected && form.DraftText == composed, "拼接混合规则被拒绝且当前草稿保持不变");
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))) == inputHash, "原始用户脚本SHA256保持不变");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            form.TestUnsavedChoice(DialogResult.No); form.Close();
            if (originalSettings != null) File.WriteAllBytes(settings, originalSettings); else if (File.Exists(settings)) File.Delete(settings);
        }
        var report = new { version = 1, passed = error == null, error, checks, inputHash, scope = "真实WinForms草稿规则控件、打开、自动恢复、退出弹窗和拼接；本自测不启动游戏，不证明确定性或重置性能" };
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return error == null;
    }
}
