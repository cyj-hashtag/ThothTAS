namespace ThothTAS.Editor;

internal sealed class ComposeOrderForm : Form
{
    private readonly ListBox paths = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly EditorText words;
    private readonly Button up = new() { AutoSize = true, Tag = "上移" };
    private readonly Button down = new() { AutoSize = true, Tag = "下移" };
    private readonly Button remove = new() { AutoSize = true, Tag = "移除" };
    private readonly Button accept = new() { AutoSize = true, DialogResult = DialogResult.OK, Tag = "生成拼接草稿" };
    public string[] OrderedPaths => paths.Items.Cast<string>().ToArray();

    public ComposeOrderForm(IEnumerable<string> selectedPaths, EditorText? language = null)
    {
        words = language ?? new EditorText();
        Text = words.Pick("确认脚本拼接顺序", "Compose TAS: confirm level order");
        ClientSize = new Size(760, 360);
        MinimumSize = new Size(540, 300);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9);
        paths.Items.AddRange(selectedPaths.Cast<object>().ToArray());
        if (paths.Items.Count > 0) paths.SelectedIndex = 0;
        var explanation = new Label { Dock = DockStyle.Top, Height = 66, Padding = new Padding(12), Text = words.Pick("按从上到下的顺序拼接。须符合游戏自然关卡顺序并使用同一种子。\r\n原文件保持不变，生成新的 Room 章节草稿；生成后须验证完整连续回放。", "Compose from top to bottom, using naturally adjacent levels and the same seed.\r\nSources stay unchanged. Save the new Room draft, then verify the complete continuous replay.") };
        var controls = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, MinimumSize = new Size(0, 48), Padding = new Padding(8), FlowDirection = FlowDirection.LeftToRight };
        up.Click += (_, _) => MoveItem(-1);
        down.Click += (_, _) => MoveItem(1);
        var add = new Button { AutoSize = true, Tag = "添加脚本" };
        add.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Multiselect = true, Filter = words.Pick("TAS 脚本 (*.tas)|*.tas", "TAS scripts (*.tas)|*.tas"), Title = words.Pick("添加 TAS 脚本", "Add TAS scripts") };
            if (picker.ShowDialog(this) == DialogResult.OK) AddPaths(picker.FileNames);
        };
        remove.Click += (_, _) => RemoveSelected();
        var cancel = new Button { AutoSize = true, DialogResult = DialogResult.Cancel, Tag = "取消" };
        controls.Controls.AddRange([add, remove, up, down, accept, cancel]);
        foreach (Button button in controls.Controls) button.Text = words.Pick((string)button.Tag!, EditorText.ControlEnglish((string)button.Tag!));
        Controls.Add(paths);
        Controls.Add(explanation);
        Controls.Add(controls);
        AcceptButton = accept;
        CancelButton = cancel;
        paths.SelectedIndexChanged += (_, _) => UpdateActions();
        UpdateActions();
    }

    private void MoveItem(int direction)
    {
        var at = paths.SelectedIndex;
        var next = at + direction;
        if (at < 0 || next < 0 || next >= paths.Items.Count) return;
        var value = paths.Items[at];
        paths.Items.RemoveAt(at);
        paths.Items.Insert(next, value);
        paths.SelectedIndex = next;
        UpdateActions();
    }
    private void AddPaths(IEnumerable<string> files)
    {
        foreach (var file in files) paths.Items.Add(Path.GetFullPath(file));
        if (paths.SelectedIndex < 0 && paths.Items.Count > 0) paths.SelectedIndex = 0;
        UpdateActions();
    }
    private void RemoveSelected()
    {
        var selected = paths.SelectedIndex;
        if (selected < 0) return;
        paths.Items.RemoveAt(selected);
        if (paths.Items.Count > 0) paths.SelectedIndex = Math.Min(selected, paths.Items.Count - 1);
        UpdateActions();
    }
    private void UpdateActions()
    {
        up.Enabled = paths.SelectedIndex > 0;
        down.Enabled = paths.SelectedIndex >= 0 && paths.SelectedIndex < paths.Items.Count - 1;
        remove.Enabled = paths.SelectedIndex >= 0;
        accept.Enabled = paths.Items.Count > 0;
    }
    internal void TestMove(int direction) => (direction < 0 ? up : down).PerformClick();
    internal void TestAdd(params string[] files) => AddPaths(files);
    internal void TestRemove() => remove.PerformClick();
    internal void TestAccept() => accept.PerformClick();
}
