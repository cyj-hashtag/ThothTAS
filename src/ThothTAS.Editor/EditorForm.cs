using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ThothTAS.Core;
using ThothTAS.Host;

namespace ThothTAS.Editor;

public sealed class EditorForm : Form
{
    private readonly string root;
    private readonly FrameAnnotatedRichTextBox draft = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 12), AcceptsTab = true, WordWrap = false, DetectUrls = false, BorderStyle = BorderStyle.None };
    private readonly FrameAnnotatedRichTextBox runningSnapshot = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 12), ReadOnly = true, WordWrap = false, DetectUrls = false, BorderStyle = BorderStyle.None, BackColor = Color.White };
    private readonly EditorText words = new();
    private readonly Dictionary<Control, string> localizedControls = [];
    private readonly ComboBox language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly ToolTip hints = new();
    private (string Chinese, string English) statusWords = ("尚未启动游戏副本", "Game copy has not started");
    private (string Chinese, string English) detailsWords = ("", "");
    private (string Chinese, string English) segmentWords = ("输入段：尚未开始", "Input segment: not started");
    private readonly List<GameRoomEvent> renderedRoomEvents = [];
    private readonly Label segmentProgress = new() { Dock = DockStyle.Top, Height = 52, Padding = new Padding(8, 4, 8, 4), Text = "输入段：尚未开始" };
    private readonly ListBox roomEvents = new() { Dock = DockStyle.Bottom, Height = 124, HorizontalScrollbar = true };
    private readonly HashSet<string> seenRoomEvents = new(StringComparer.Ordinal);
    private readonly Label reliableReplay = new() { AutoSize = true, Margin = new Padding(6, 8, 6, 0) };
    private readonly CheckBox communityMod = new() { AutoSize = true, Text = "社区计时器", Checked = false, Margin = new Padding(8, 8, 8, 0) };
    private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Microsoft YaHei UI", 10), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    private readonly Label status = new() { AutoSize = false, Dock = DockStyle.Top, Height = 36, Padding = new Padding(8), Text = "尚未启动游戏副本" };
    private readonly Label snapshot = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8), Text = "启动时冻结脚本快照；编辑与保存更新草稿，重新回放应用草稿。" };
    private readonly Label fileLabel = new() { Dock = DockStyle.Top, Height = 28, Padding = new Padding(8, 4, 8, 4), Text = "未保存草稿" };
    private readonly NumericUpDown width = new() { Minimum = 640, Maximum = 7680, Value = 1280, Increment = 80, Width = 74 };
    private readonly NumericUpDown height = new() { Minimum = 360, Maximum = 4320, Value = 720, Increment = 45, Width = 74 };
    private readonly ComboBox speed = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 72 };
    private readonly CheckBox modes = new() { AutoSize = true, Text = "同时验证暂停 / 逐帧 / 速度", Checked = true, Margin = new Padding(8, 8, 8, 0) };
    private readonly List<Button> sessionButtons = [];
    private readonly Button play;
    private readonly Button replay;
    private readonly Button repeat;
    private readonly Button pause;
    private readonly Button step;
    private readonly Button stop;
    private readonly CancellationTokenSource lifetime = new();
    private GameSession? session;
    private Action<GameStatus>? sessionStatusHandler;
    private bool controlFaulted;
    private bool formattingInputLine;
    private int draftHighlightedLine = -1;
    private int snapshotHighlightedLine = -1;
    private GameStatus? displayedStatus;
    [StructLayout(LayoutKind.Sequential)] private struct ScrollPoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref ScrollPoint point);
    private string? scriptPath;
    private string? launchedText;
    private string? latestDirectory;
    private bool dirty;
    private string savedDraftText = "";
    private bool loadingDraft;
    private readonly DraftRecoveryStore recovery;
    private readonly System.Windows.Forms.Timer autosave = new() { Interval = 350 };
    private readonly Queue<DialogResult> automationUnsavedChoices = new();
    private string? automationRecoveryPath;
    internal int UnsavedDialogsShown { get; private set; }
    internal string RecoveryCheckpointPath => recovery.CheckpointPath;
    internal bool HasUnsavedChanges => dirty;
    internal void TestUnsavedChoice(DialogResult value) => automationUnsavedChoices.Enqueue(value);
    internal void TestRecoverPath(string path) => automationRecoveryPath = Path.GetFullPath(path);
    private bool busy;
    private bool closing;
    private bool canClose;
    private TaskCompletionSource operationEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? automationOpenPath;
    private string? automationSavePath;
    private string[]? automationComposePaths;
    private string? automationOutputParent;
    internal GameSession? ActiveSession => session;
    internal string? LastRunDirectory => latestDirectory;
    internal string SnapshotText => runningSnapshot.Text;
    internal int SnapshotHighlightedLine => snapshotHighlightedLine + 1;
    internal int DraftHighlightedLine => draftHighlightedLine + 1;
    internal string SegmentText => segmentProgress.Text;
    internal string[] RoomEventItems => roomEvents.Items.Cast<string>().ToArray();
    internal bool ControlFaulted => controlFaulted;
    internal string SnapshotAnnotation => runningSnapshot.AnnotationText;
    internal string DraftAnnotation => draft.AnnotationText;
    internal Color AnnotationColor => runningSnapshot.AnnotationColor;
    internal Rectangle AnnotationBounds => runningSnapshot.AnnotationBounds;
    internal bool SnapshotAnnotationVisible => runningSnapshot.AnnotationVisible;
    internal bool DraftAnnotationVisible => draft.AnnotationVisible;
    internal bool EnglishLanguage => words.English;
    internal string StatusText => status.Text;
    internal string DetailsText => details.Text;
    internal void TestLanguage(bool english) => language.SelectedIndex = english ? 1 : 0;
    internal void TestShowSnapshot() => ((TabControl)runningSnapshot.Parent!.Parent!).SelectedTab = (TabPage)runningSnapshot.Parent;
    internal void TestShowDraft() => ((TabControl)draft.Parent!.Parent!).SelectedTab = (TabPage)draft.Parent;
    internal void TestCommunity(bool value) => communityMod.Checked = value;
    internal void TestComposePaths(params string[] paths) => automationComposePaths = paths.Select(Path.GetFullPath).ToArray();
    internal void TestSavePath(string path) => automationSavePath = Path.GetFullPath(path);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string DraftText { get => draft.Text; set => draft.Text = value; }
    internal void TestOpenPath(string path) => automationOpenPath = Path.GetFullPath(path);
    internal void TestOutputParent(string path) => automationOutputParent = Path.GetFullPath(path);
    internal async Task TestClickAsync(string text)
    {
        Button? Find(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is Button button && (button.Tag as string == text || button.Text == text)) return button;
                var child = Find(control);
                if (child != null) return child;
            }
            return null;
        }
        var target = Find(this) ?? throw new InvalidOperationException("未找到编辑器按钮：" + text);
        if (!target.Enabled) throw new InvalidOperationException("编辑器按钮未启用：" + text);
        target.PerformClick();
        if (busy) await operationEnded.Task.WaitAsync(TimeSpan.FromMinutes(3));
        if (status.Text.StartsWith("错误：", StringComparison.Ordinal) || status.Text.StartsWith("Error:", StringComparison.Ordinal)) throw new InvalidOperationException(status.Text);
    }
    internal async Task TestSpeedAsync(double value)
    {
        speed.SelectedIndex = value == 0.5 ? 0 : value == 2 ? 2 : 1;
        if (busy) await operationEnded.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (status.Text.StartsWith("错误：", StringComparison.Ordinal) || status.Text.StartsWith("Error:", StringComparison.Ordinal)) throw new InvalidOperationException(status.Text);
    }

    public EditorForm(string projectRoot, string? initialScript, bool recoveryPrompt = true)
    {
        root = projectRoot;
        recovery = new DraftRecoveryStore(root);
        words.English = EditorText.LoadEnglish(root);
        Text = T("THOTH TAS 编辑器", "THOTH TAS Editor");
        ClientSize = new Size(1260, 820);
        MinimumSize = new Size(1020, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9);
        BackColor = Color.FromArgb(242, 242, 242);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 96), Padding = new Padding(7), WrapContents = true, BackColor = Color.FromArgb(232, 234, 238) };
        toolbar.Controls.Add(Button("打开", OpenDraft));
        toolbar.Controls.Add(Button("保存", SaveDraft));
        toolbar.Controls.Add(Button("另存为", SaveDraftAs));
        toolbar.Controls.Add(Button("恢复草稿", RestoreDraft));
        toolbar.Controls.Add(Button("校验", ValidateDraft));
        toolbar.Controls.Add(Button("拼接脚本", ComposeDraft));
        Localize(reliableReplay, "可靠重启：独立进程");
        Localize(modes, "同时验证暂停 / 逐帧 / 速度");
        Localize(communityMod, "社区计时器");
        play = AsyncButton("播放", () => RunGuardedAsync(PlayAsync));
        pause = AsyncButton("暂停", () => RunGuardedAsync(() => CommandAsync("PAUSE")));
        step = AsyncButton("逐帧", () => RunGuardedAsync(StepAsync));
        stop = AsyncButton("停止", () => RunGuardedAsync(StopAsync));
        replay = AsyncButton("重新回放", () => RunGuardedAsync(ReplayAsync));
        repeat = AsyncButton("重复验证 ×5", () => RunGuardedAsync(VerifyAsync));
        sessionButtons.AddRange([play, pause, step, stop, replay, repeat]);
        foreach (var button in sessionButtons) { toolbar.Controls.Add(button); if (button == replay) toolbar.Controls.Add(reliableReplay); }
        speed.Items.AddRange(["0.5×", "1×", "2×"]); speed.SelectedIndex = 1;
        toolbar.Controls.Add(InlineLabel("速度")); toolbar.Controls.Add(speed);
        var sizePicker = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        sizePicker.Controls.Add(InlineLabel("启动尺寸")); sizePicker.Controls.Add(width); sizePicker.Controls.Add(InlineLabel("×")); sizePicker.Controls.Add(height);
        toolbar.Controls.Add(sizePicker);
        toolbar.Controls.Add(modes);
        toolbar.Controls.Add(communityMod);
        toolbar.Controls.Add(Button("打开日志目录", OpenLogs));
        var languagePicker = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        languagePicker.Controls.Add(InlineLabel("语言 / Language"));
        language.Items.AddRange(["简体中文", "English"]);
        language.SelectedIndex = words.English ? 1 : 0;
        languagePicker.Controls.Add(language);
        toolbar.Controls.Add(languagePicker);
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1244, 730), SplitterWidth = 6, Panel1MinSize = 440, Panel2MinSize = 340, Padding = new Padding(8) };
        split.SplitterDistance = 860;
        split.FixedPanel = FixedPanel.Panel2;
        var textTabs = new TabControl { Dock = DockStyle.Fill };
        var draftTab = Localize(new TabPage { BackColor = Color.White }, "草稿");
        var snapshotTab = Localize(new TabPage { BackColor = Color.White }, "运行快照（只读）");
        draftTab.Controls.Add(draft);
        snapshotTab.Controls.Add(runningSnapshot);
        textTabs.TabPages.AddRange([draftTab, snapshotTab]);
        split.Panel1.Controls.Add(textTabs);
        split.Panel1.Controls.Add(fileLabel);
        split.Panel1.Controls.Add(snapshot);
        split.Panel2.Controls.Add(details);
        split.Panel2.Controls.Add(roomEvents);
        split.Panel2.Controls.Add(segmentProgress);
        split.Panel2.Controls.Add(status);
        Controls.Add(split);
        Controls.Add(toolbar);
        SetDetails("游戏副本：" + Path.Combine(root, "game", "THOTH.exe") + Environment.NewLine + Environment.NewLine +
            "此工具每次启动新进程，恢复固定存档，核对游戏指纹。" + Environment.NewLine +
            "回放中可切换焦点编辑。分辨率在启动前设置。" + Environment.NewLine +
            "重复验证运行实际游戏，并比较基线、逐帧玩法与随机记录。" + Environment.NewLine + Environment.NewLine +
            "语法：右 0°、上 90°、左 180°、下 270°。" + Environment.NewLine +
            "M=x:y：模拟移动；A=x:y：射击向量。" + Environment.NewLine +
            "A=(x,y)：世界坐标目标；N：不移动。",
            "Game copy: " + Path.Combine(root, "game", "THOTH.exe") + "\r\n\r\n" +
            "Cold replays restore the fixed save and validate the game fingerprint.\r\n" +
            "Replay uses a fresh process to restore native physics and random state.\r\n" +
            "You can edit while playback continues in the background.\r\n" +
            "Verification compares the actual baseline, frames and random calls.\r\n\r\n" +
            "Angles: right 0°, up 90°, left 180°, down 270°.\r\n" +
            "M=x:y: analog movement; A=x:y: aim vector.\r\n" +
            "A=(x,y): world target; N: no movement.\r\n" +
            "The blue --- frame marker is display-only, not part of the script.");
        draft.TextChanged += (_, _) =>
        {
            if (formattingInputLine || loadingDraft) return;
            dirty = draft.Text != savedDraftText;
            autosave.Stop();
            if (dirty) autosave.Start(); else ClearCheckpoint();
            UpdateDraftLabel();
            if (draftHighlightedLine >= 0 && draft.Text != launchedText) ClearDraftHighlight();
            SynchronizeHighlight();
        };
        autosave.Tick += (_, _) => { autosave.Stop(); CheckpointDraft(); };
        Deactivate += (_, _) => { if (!closing) CheckpointDraft(); };
        speed.SelectedIndexChanged += async (_, _) =>
        {
            if (session != null && !busy) await RunGuardedAsync(() => session.SetSpeedAsync(CurrentSpeed, lifetime.Token));
        };
        if (initialScript != null) LoadDraft(Path.GetFullPath(initialScript));
        else
        {
            draft.Text = "Version,1\nStart,Basic-1\nSeed,1\n\n60, M=45, A=270\n60, M=45, A=(30,40)\n30, N\n60, M=0.5:0, A=1:0\n";
            dirty = false;
            savedDraftText = draft.Text;
            autosave.Stop();
            UpdateDraftLabel();
        }
        UpdateButtons();
        ApplyLanguage();
        language.SelectedIndexChanged += (_, _) =>
        {
            words.English = language.SelectedIndex == 1;
            ApplyLanguage();
            try { words.SaveLanguage(root); } catch (Exception ex) { ShowError(ex); }
        };
        if (recoveryPrompt) Shown += (_, _) =>
        {
            if (recovery.List().Any(DraftRecoveryStore.Pending)) RestoreDraft();
        };
    }

    private Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(2, 2, 2, 4), Padding = new Padding(4, 0, 4, 0) };
        button.Click += (_, _) => action();
        return Localize(button, text);
    }
    private Button AsyncButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(2, 2, 2, 4), Padding = new Padding(4, 0, 4, 0) };
        button.Click += async (_, _) => await action();
        return Localize(button, text);
    }
    private Label InlineLabel(string text) => Localize(new Label { AutoSize = true, Margin = new Padding(8, 8, 0, 0) }, text);
    private C Localize<C>(C control, string key) where C : Control
    { localizedControls[control] = key; control.Tag = key; control.Text = T(key, EditorText.ControlEnglish(key)); return control; }
    private string T(string chinese, string english) => words.Pick(chinese, english);
    private void SetStatus(string chinese, string english) { statusWords = (chinese, english); status.Text = T(chinese, english); }
    private void SetDetails(string chinese, string english) { detailsWords = (chinese, english); details.Text = T(chinese, english); }
    private void SetSegment(string chinese, string english) { segmentWords = (chinese, english); segmentProgress.Text = T(chinese, english); }
    private void ApplyLanguage()
    {
        SuspendLayout();
        foreach (var (control, key) in localizedControls) control.Text = T(key, EditorText.ControlEnglish(key));
        hints.SetToolTip(reliableReplay, T("重新回放启动独立进程，恢复物理初始状态。旧同进程重置会使撞墙反弹失步，已从编辑器停用。", "Replay starts a fresh process to restore native physics. The old same-process reset caused divergent collisions and is disabled in the editor."));
        hints.SetToolTip(communityMod, T("加载社区Speedrun Mod v2.8计时器和窗口功能；TAS按固定帧计时。切换在下次重新回放生效，并启动新进程。首次使用请先准备社区Mod。", "Community Speedrun Mod v2.8 timers and window behavior; TAS clocks use fixed frames. A changed choice applies on Replay with a new process. Prepare the community Mod before first use."));
        status.Text = T(statusWords.Chinese, statusWords.English);
        details.Text = T(detailsWords.Chinese, detailsWords.English);
        UpdateDraftLabel();
        RefreshRoomEventList();
        if (displayedStatus != null && !controlFaulted) RenderStatus(displayedStatus);
        else segmentProgress.Text = T(segmentWords.Chinese, segmentWords.English);
        ResumeLayout(true);
    }
    private double CurrentSpeed => speed.SelectedIndex switch { 0 => 0.5, 2 => 2, _ => 1 };

    private void OpenDraft()
    {
        try
        {
            if (automationOpenPath != null)
            {
                var path = automationOpenPath;
                automationOpenPath = null;
                if (!ConfirmUnsaved()) return;
                LoadDraft(path);
                return;
            }
            using var dialog = new OpenFileDialog { Filter = T("TAS 脚本 (*.tas)|*.tas|UTF-8 文本 (*.txt)|*.txt|所有文件|*.*", "TAS scripts (*.tas)|*.tas|UTF-8 text (*.txt)|*.txt|All files|*.*"), Title = T("打开 TAS 脚本", "Open TAS script"), InitialDirectory = Path.Combine(root, "examples") };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                if (!ConfirmUnsaved()) return;
                LoadDraft(dialog.FileName);
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void LoadDraft(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        loadingDraft = true;
        try { draft.Text = text; } finally { loadingDraft = false; }
        scriptPath = path;
        savedDraftText = draft.Text;
        dirty = false;
        ClearCheckpoint();
        UpdateDraftLabel();
        SynchronizeHighlight();
    }

    private void ComposeDraft()
    {
        try
        {
            string[] ordered;
            if (automationComposePaths != null) { ordered = automationComposePaths; automationComposePaths = null; }
            else
            {
                using var picker = new OpenFileDialog { Filter = T("TAS 脚本 (*.tas)|*.tas|UTF-8 文本 (*.txt)|*.txt", "TAS scripts (*.tas)|*.tas|UTF-8 text (*.txt)|*.txt"), Multiselect = true, InitialDirectory = Path.Combine(root, "examples"), Title = T("选择要按自然关卡顺序拼接的脚本", "Select scripts to compose in natural level order") };
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                using var ordering = new ComposeOrderForm(picker.FileNames, words);
                if (ordering.ShowDialog(this) != DialogResult.OK) return;
                ordered = ordering.OrderedPaths;
            }
            var sources = ordered.Select(path => new ScriptSource(path, File.ReadAllText(path, Encoding.UTF8))).ToArray();
            var starts = File.ReadAllLines(Path.Combine(root, "docs", "levels.txt")).Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith('#')).ToList();
            var composed = ScriptComposer.Compose(sources, starts);
            if (!ConfirmUnsaved()) return;
            scriptPath = null;
            savedDraftText = "";
            draft.Text = composed;
            dirty = true;
            CheckpointDraft();
            UpdateDraftLabel();
            SetStatus("已生成拼接草稿。请另存为新文件；单关通过不代表整段连续回放已通过。", "Composed draft created. Save as a new file, then verify the complete continuous replay.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SaveDraft() { if (scriptPath == null) SaveDraftAs(); else WriteDraft(scriptPath); }
    private void SaveDraftAs()
    {
        if (automationSavePath != null) { var path = automationSavePath; automationSavePath = null; WriteDraft(path); return; }
        using var dialog = new SaveFileDialog { Filter = T("TAS 脚本 (*.tas)|*.tas", "TAS scripts (*.tas)|*.tas"), Title = T("另存 TAS 脚本", "Save TAS script as"), DefaultExt = "tas", FileName = scriptPath == null ? "replay.tas" : Path.GetFileName(scriptPath), InitialDirectory = scriptPath == null ? Path.Combine(root, "examples") : Path.GetDirectoryName(scriptPath) };
        if (dialog.ShowDialog(this) == DialogResult.OK) WriteDraft(dialog.FileName);
    }

    private void WriteDraft(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            var temporary = path + ".tas-saving-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(draft.Text);
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes); file.Flush(true); }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            scriptPath = path;
            dirty = false;
            savedDraftText = draft.Text;
            ClearCheckpoint();
            UpdateDraftLabel();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RecoverDraft()
    {
        var directory = Path.Combine(root, "work", "draft-recovery");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".tas"), draft.Text, new UTF8Encoding(false));
    }

    private void ClearCheckpoint()
    {
        autosave.Stop();
        try { recovery.Clear(); } catch (Exception ex) { ShowError(ex); }
    }
    private bool CheckpointDraft()
    {
        if (!dirty) return true;
        try { recovery.Write(scriptPath, draft.Text); return true; }
        catch (Exception ex) { ShowError(ex); return false; }
    }
    private bool ConfirmUnsaved()
    {
        if (!dirty) return true;
        // Persist before displaying a modal window or stopping the game.
        CheckpointDraft();
        using var dialog = new UnsavedDraftForm(words, scriptPath ?? T("未保存草稿", "Unsaved draft"));
        UnsavedDialogsShown++;
        if (automationUnsavedChoices.Count > 0)
        {
            var choice = automationUnsavedChoices.Dequeue();
            dialog.Shown += (_, _) => BeginInvoke(() => dialog.TestChoose(choice));
        }
        var result = dialog.ShowDialog(this);
        if (result == DialogResult.Yes) { SaveDraft(); return !dirty; }
        if (result != DialogResult.No) return false;
        try { RecoverDraft(); recovery.Clear(); autosave.Stop(); }
        catch (Exception ex) { ShowError(ex); return false; }
        return true;
    }
    private void RestoreDraft()
    {
        try
        {
            RecoveredDraft? selected;
            if (automationRecoveryPath != null) { selected = recovery.Read(automationRecoveryPath); automationRecoveryPath = null; }
            else
            {
                using var picker = new DraftRecoveryForm(recovery.List(), words);
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                selected = picker.Selected;
            }
            if (selected == null || !ConfirmUnsaved()) return;
            loadingDraft = true;
            try { draft.Text = selected.Text; } finally { loadingDraft = false; }
            scriptPath = selected.SourcePath;
            savedDraftText = ""; dirty = true;
            if (CheckpointDraft()) recovery.MarkRecovered(selected);
            UpdateDraftLabel(); SynchronizeHighlight();
            SetStatus("已恢复草稿，请保存或另存为。备份文件仍保留。", "Draft recovered. Save or Save as to keep it. The original backup is preserved.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void UpdateDraftLabel()
    {
        fileLabel.Text = (scriptPath ?? T("未保存草稿", "Unsaved draft")) + (dirty ? T("  • 已修改", "  • modified") : "");
        Text = T("THOTH TAS 编辑器", "THOTH TAS Editor") + (scriptPath == null ? "" : " — " + Path.GetFileName(scriptPath)) + (dirty ? " *" : "");
        snapshot.Text = launchedText == null ? T("启动时冻结脚本快照；编辑与保存更新草稿，重新回放应用草稿。", "Playback freezes a snapshot. Edit/save changes the draft; Replay applies changes.") :
            launchedText == draft.Text ? T("当前回放使用启动快照，与草稿一致。", "Playback uses the frozen snapshot, matching the draft.") : T("草稿已修改；当前回放继续使用启动快照。点击“重新回放”应用修改。", "Draft modified; playback keeps its frozen snapshot. Click Replay to apply changes.");
    }

    private void ValidateDraft()
    {
        try
        {
            var parsed = ProjectFiles.Parse(root, draft.Text);
            SetStatus($"草稿校验通过：{parsed.Start} / 种子 {parsed.Seed} / {parsed.TotalFrames} 帧", $"Draft valid: {parsed.Start} / Seed {parsed.Seed} / {parsed.TotalFrames} frames");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task StartAsync()
    {
        var immutable = draft.Text;
        var options = new SessionOptions { Width = (int)width.Value, Height = (int)height.Value, Speed = CurrentSpeed, Autoplay = false, OutputParent = automationOutputParent, CommunityMod = communityMod.Checked };
        SetStatus("校验指纹、恢复固定存档并启动独立游戏副本…", "Checking fingerprints, restoring the fixed save and starting a cold game copy…");
        session = await GameSession.StartAsync(root, immutable, options, lifetime.Token);
        var owned = session;
        controlFaulted = false;
        sessionStatusHandler = value => UI(() => { if (!closing && ReferenceEquals(session, owned)) RenderStatus(value); });
        owned.StatusChanged += sessionStatusHandler;
        _ = ObserveCompletionAsync(owned);
        launchedText = immutable;
        runningSnapshot.Text = immutable;
        snapshotHighlightedLine = -1;
        seenRoomEvents.Clear();
        renderedRoomEvents.Clear();
        roomEvents.Items.Clear();
        latestDirectory = session.RunDirectory;
        UpdateDraftLabel();
        RenderStatus(session.Latest);
        UpdateButtons();
    }

    private async Task PlayAsync()
    {
        if (session?.Latest.State == "complete") await StopAsync();
        if (session == null) await StartAsync();
        await session!.SendAsync("PLAY", lifetime.Token);
    }

    private async Task StepAsync()
    {
        if (session == null) await StartAsync();
        if (session!.Latest.State == "complete") return;
        await session.SendAsync("PAUSE", lifetime.Token);
        await session.WaitForStatusAsync(s => s.State == "paused" || s.State == "complete", TimeSpan.FromSeconds(15), lifetime.Token);
        if (session.Latest.State != "complete") await session.StepAsync(lifetime.Token);
    }

    private async Task CommandAsync(string command)
    {
        if (session != null) await session.SendAsync(command, lifetime.Token);
    }

    private async Task StopAsync()
    {
        var owned = session;
        session = null;
        if (owned != null) { if (sessionStatusHandler != null) owned.StatusChanged -= sessionStatusHandler; await owned.DisposeAsync(); }
        sessionStatusHandler = null;
        controlFaulted = false;
        launchedText = null;
        displayedStatus = null;
        SynchronizeHighlight();
        SetSegment("输入段：已停止", "Input segment: stopped");
        SetStatus("已停止本工具启动的游戏副本", "Stopped the game copy owned by this tool");
        UpdateDraftLabel();
        UpdateButtons();
    }

    private async Task ReplayAsync()
    {
        // PhysX's native accumulator/contact state survives a scene reload.
        // A fresh process is required for reliable authoring, including after death.
        await StopAsync(); await StartAsync();
        await session!.SendAsync("PLAY", lifetime.Token);
    }

    private async Task VerifyAsync()
    {
        await StopAsync();
        var immutable = draft.Text;
        ProjectFiles.Parse(root, immutable);
        var directory = Path.Combine(root, "work", "verification", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        latestDirectory = directory;
        var options = new SessionOptions { Width = (int)width.Value, Height = (int)height.Value, OutputParent = directory, CommunityMod = communityMod.Checked };
        var report = await ReplayVerifier.VerifyAsync(root, immutable, options, 5, modes.Checked, message => UI(() => { SetStatus(message, words.Reason(message)); SetDetails(detailsWords.Chinese + message + Environment.NewLine, detailsWords.English + words.Reason(message) + Environment.NewLine); }), lifetime.Token);
        SetStatus(report.Passed ? "实际重复回放验证通过" : "实际重复回放验证未通过", report.Passed ? "Actual replay verification passed" : "Actual replay verification failed");
        SetDetails(File.ReadAllText(Path.Combine(directory, "report.md")), $"Actual-game verification: {(report.Passed ? "PASSED" : "FAILED")}\r\nCompleted runs: {report.Runs.Count}\r\nFailed runs: {report.FailedRuns.Count}\r\nDifferences: {report.Differences.Count}\r\n\r\n{words.Reason(report.Error ?? "")}\r\n\r\nFull report and logs:\r\n{directory}");
    }

    private async Task ObserveCompletionAsync(GameSession owned)
    {
        var watchedCompletion = owned.Completion;
        try { await watchedCompletion; }
        catch (Exception ex)
        {
            UI(() =>
            {
                if (closing || !ReferenceEquals(session, owned) || !ReferenceEquals(owned.Completion, watchedCompletion)) return;
                controlFaulted = true;
                if (owned.Latest.Type == "error" || owned.Latest.State == "error") RenderStatus(owned.Latest);
                else
                {
                    SetStatus("控制连接断开 / TAS 模块按帧边界暂停", "Control connection lost / TAS paused at a frame boundary");
                    SetDetails("控制连接发生错误，无法继续接收帧状态。最后确认帧：" + owned.Latest.Frame + "\r\n\r\n" + ex.Message + "\r\n\r\n可停止副本或重新回放。日志目录：\r\n" + owned.RunDirectory, "Cannot receive frame status. Last confirmed frame: " + owned.Latest.Frame + "\r\n\r\n" + words.Error(ex) + "\r\n\r\nStop the copy or Replay. Logs:\r\n" + owned.RunDirectory);
                }
                UpdateButtons();
            });
        }
    }
    private void RenderStatus(GameStatus s)
    {
        displayedStatus = s;
        SetStatus($"{StateName(s.State, false)}  /  {s.Scene}  /  帧 {s.Frame}  /  第 {s.Line} 行",
            $"{StateName(s.State, true)}  /  {s.Scene}  /  Frame {s.Frame}  /  Line {s.Line}");
        if (s.Line == 0 && s.Frame > 0 && session?.Script.HasExplicitRooms == true)
            SetSegment($"等待自然转场 / 章节 {s.Chapter + 1}", $"Waiting for natural transition / Chapter {s.Chapter + 1}");
        else if (s.SegmentFrames > 0)
            SetSegment($"输入段：{s.SegmentFrame} / {s.SegmentFrames} 帧 · 章节 {s.Chapter + 1}", $"Segment: {s.SegmentFrame} / {s.SegmentFrames} · Ch. {s.Chapter + 1}");
        else if (s.Frame <= 0 || session == null) SetSegment("输入段：尚未开始（初始基线）", "Input segment: not started (baseline)");
        else
        {
            var part = session.Script.Segments.FirstOrDefault(p => s.Frame - 1 >= p.StartFrame && s.Frame - 1 < p.StartFrame + p.Frames);
            if (part == null) SetSegment("输入段：无", "Input segment: none");
            else SetSegment($"输入段：{s.Frame - part.StartFrame} / {part.Frames} 帧", $"Input segment: {s.Frame - part.StartFrame} / {part.Frames} frames");
        }
        SynchronizeHighlight();
        RenderRoomEvents(s);
        var inputZh = "无";
        var inputEn = "None";
        var target = "";
        if (session != null && session.Script.TotalFrames > 0 && s.Frame > 0 && (!session.Script.HasExplicitRooms || s.Line > 0))
        {
            int line;
            TasFrameInput value;
            if (session.Script.HasExplicitRooms)
            {
                var chapter = Math.Clamp(s.Chapter, 0, session.Script.Chapters.Count - 1);
                var local = Math.Clamp(s.ChapterFrame - 1, 0, session.Script.Chapters[chapter].TotalFrames - 1);
                value = session.Script.GetInputAtChapterFrame(chapter, local, out line);
            }
            else value = session.Script.GetInputAtFrame(Math.Clamp(s.Frame - 1, 0, session.Script.TotalFrames - 1), out line);
            inputZh = FormattableString.Invariant($"移动 ({value.MoveX:0.######}, {value.MoveY:0.######})\r\n射击 {value.AimKind} ({value.AimX:0.######}, {value.AimY:0.######})\r\n脚本行 {line}");
            inputEn = FormattableString.Invariant($"Movement ({value.MoveX:0.######}, {value.MoveY:0.######})\r\nAim {value.AimKind} ({value.AimX:0.######}, {value.AimY:0.######})\r\nSource line {line}");
            if (value.AimKind == AimKind.WorldTarget) target = FormattableString.Invariant($"({value.AimX:0.######}, {value.AimY:0.######})");
        }
        else if (s.Frame > 0 && s.Line == 0) { inputZh = "零输入（章节转场或激活等待）"; inputEn = "Zero input (transition or activation wait)"; }
        var xy = $"({s.X.ToString("0.######", CultureInfo.InvariantCulture)}, {s.Y.ToString("0.######", CultureInfo.InvariantCulture)})";
        SetDetails($"关卡：{s.Scene}\r\n状态：{StateName(s.State, false)}\r\n输入帧：{s.Frame}\r\n执行行：{s.Line}\r\n玩家世界 XY：{xy}\r\n\r\n{inputZh}\r\n\r\n固定瞄准点：{(target.Length == 0 ? "无" : target)}\r\n\r\n随机调用累计：{s.RandomCount}\r\n随机摘要：{s.RandomHash}\r\n\r\n失步 / 错误：{s.Error ?? "无"}\r\n\r\n日志目录：\r\n{latestDirectory}",
            $"Level: {s.Scene}\r\nState: {StateName(s.State, true)}\r\nInput frame: {s.Frame}\r\nSource line: {s.Line}\r\nPlayer world XY: {xy}\r\n\r\n{inputEn}\r\n\r\nWorld aim target: {(target.Length == 0 ? "None" : target)}\r\n\r\nRandom calls: {s.RandomCount}\r\nRandom digest: {s.RandomHash}\r\n\r\nDesync / error: {(s.Error == null ? "None" : words.Reason(s.Error))}\r\n\r\nLog directory:\r\n{latestDirectory}");
        UpdateButtons();
    }

    private void RenderRoomEvents(GameStatus s)
    {
        bool changed = false;
        foreach (var item in s.RoomEvents)
        {
            var key = item.Frame + "|" + item.Scene + "|" + item.Type;
            if (seenRoomEvents.Add(key)) { renderedRoomEvents.Add(item); changed = true; }
        }
        if (changed) RefreshRoomEventList();
    }

    private void RefreshRoomEventList()
    {
        roomEvents.BeginUpdate();
        roomEvents.Items.Clear();
        foreach (var item in renderedRoomEvents)
        {
            var kind = item.Type == "completed" ? T("过关", "Completed") : item.Type == "entered" ? T("入场", "Entered") : item.Type;
            roomEvents.Items.Add($"{kind} · {T("帧", "frame")} {item.Frame} · {item.Scene}");
        }
        if (roomEvents.Items.Count > 0) roomEvents.TopIndex = roomEvents.Items.Count - 1;
        roomEvents.EndUpdate();
    }

    private void SynchronizeHighlight()
    {
        var line = displayedStatus is { Frame: > 0, Line: > 0 } ? displayedStatus.Line - 1 : -1;
        SetLineHighlight(runningSnapshot, ref snapshotHighlightedLine, line, follow: true);
        var unchanged = launchedText != null && draft.Text == launchedText;
        SetLineHighlight(draft, ref draftHighlightedLine, unchanged ? line : -1, follow: false);
        int segmentFrame = displayedStatus?.SegmentFrame ?? 0;
        runningSnapshot.ShowFrame(line, segmentFrame);
        draft.ShowFrame(unchanged ? line : -1, segmentFrame);
    }

    private void SetLineHighlight(RichTextBox box, ref int priorLine, int line, bool follow)
    {
        if (priorLine == line) return;
        formattingInputLine = true;
        var caret = box.SelectionStart;
        var selectionLength = box.SelectionLength;
        ScrollPoint scroll = new();
        SendMessage(box.Handle, 0x04DD, IntPtr.Zero, ref scroll); // EM_GETSCROLLPOS
        try
        {
            if (priorLine >= 0)
            {
                var priorStart = box.GetFirstCharIndexFromLine(priorLine);
                if (priorStart >= 0) { box.Select(priorStart, box.Lines.ElementAtOrDefault(priorLine)?.Length ?? 0); box.SelectionBackColor = box.BackColor; }
            }
            var start = line < 0 ? -1 : box.GetFirstCharIndexFromLine(line);
            if (start >= 0) { box.Select(start, box.Lines.ElementAtOrDefault(line)?.Length ?? 0); box.SelectionBackColor = Color.FromArgb(255, 239, 166); if (follow) box.ScrollToCaret(); }
            priorLine = start >= 0 ? line : -1;
            box.Select(Math.Min(caret, box.TextLength), Math.Min(selectionLength, Math.Max(0, box.TextLength - caret)));
            if (!follow) SendMessage(box.Handle, 0x04DE, IntPtr.Zero, ref scroll); // EM_SETSCROLLPOS
        }
        finally { formattingInputLine = false; }
    }

    private void ClearDraftHighlight()
    {
        formattingInputLine = true;
        var caret = draft.SelectionStart;
        var length = draft.SelectionLength;
        ScrollPoint scroll = new();
        SendMessage(draft.Handle, 0x04DD, IntPtr.Zero, ref scroll);
        try { draft.SelectAll(); draft.SelectionBackColor = draft.BackColor; draft.Select(caret, length); draftHighlightedLine = -1; SendMessage(draft.Handle, 0x04DE, IntPtr.Zero, ref scroll); }
        finally { formattingInputLine = false; }
    }

    private string StateName(string value, bool? english = null) => (english ?? words.English)
        ? value switch { "starting" => "Starting", "paused" => "Paused", "running" => "Playing", "complete" => "Complete", "error" => "Error", _ => value }
        : value switch { "starting" => "启动中", "paused" => "已暂停", "running" => "播放中", "complete" => "已完成", "error" => "错误", _ => value };
    private void UI(Action action)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { if (IsHandleCreated) BeginInvoke(action); }
        else action();
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        if (busy || closing) return;
        operationEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        busy = true;
        UpdateButtons();
        try { await action(); }
        catch (OperationCanceledException) { if (!closing) SetStatus("操作已取消", "Operation canceled"); }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; UpdateButtons(); operationEnded.TrySetResult(); }
    }

    private void ShowError(Exception ex)
    {
        SetStatus("错误：" + ex.Message, "Error: " + words.Error(ex));
        SetDetails(ex.ToString() + Environment.NewLine + Environment.NewLine + "最近日志目录：" + latestDirectory,
            ex.GetType().Name + ": " + words.Error(ex) + "\r\n" + ex.StackTrace + "\r\n\r\nLatest logs: " + latestDirectory);
        if (ex is ScriptParseException syntax)
        {
            var start = draft.GetFirstCharIndexFromLine(Math.Max(0, syntax.Line - 1));
            if (start >= 0) { draft.Select(start, draft.Lines.ElementAtOrDefault(syntax.Line - 1)?.Length ?? 0); draft.ScrollToCaret(); draft.Focus(); }
        }
    }

    private void UpdateButtons()
    {
        if (closing) { foreach (var b in sessionButtons) b.Enabled = false; return; }
        foreach (var b in sessionButtons) b.Enabled = !busy;
        pause.Enabled = !busy && !controlFaulted && session != null && session.Latest.State != "complete";
        play.Enabled = !busy && !controlFaulted;
        step.Enabled = !busy && !controlFaulted;
        stop.Enabled = !busy && session != null;
        width.Enabled = height.Enabled = !busy && session == null;
        modes.Enabled = !busy;
        speed.Enabled = !busy;
        communityMod.Enabled = !busy;
    }

    private void OpenLogs()
    {
        if (latestDirectory != null && Directory.Exists(latestDirectory)) Process.Start(new ProcessStartInfo("explorer.exe", latestDirectory) { UseShellExecute = true });
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (canClose) { base.OnFormClosing(e); return; }
        base.OnFormClosing(e);
        if (e.Cancel) return;
        e.Cancel = true;
        if (closing) return;
        if (!ConfirmUnsaved()) return;
        closing = true;
        autosave.Stop();
        UpdateButtons();
        lifetime.Cancel();
        try { if (busy) await operationEnded.Task; await StopAsync(); }
        catch (Exception) { }
        canClose = true;
        Close();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { autosave.Dispose(); hints.Dispose(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
