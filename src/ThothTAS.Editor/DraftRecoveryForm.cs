namespace ThothTAS.Editor;

internal sealed class DraftRecoveryForm : Form
{
    internal RecoveredDraft? Selected { get; private set; }
    internal DraftRecoveryForm(IReadOnlyList<RecoveredDraft> items, EditorText words)
    {
        Text = words.Pick("恢复未保存草稿", "Recover unsaved drafts");
        Size = new Size(820, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 10);
        var list = new ListBox { Dock = DockStyle.Top, Height = 150, HorizontalScrollbar = true };
        var preview = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 11) };
        var note = new Label { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8), Text = words.Pick("选择备份并预览。恢复只替换编辑器草稿，请保存或另存为。", "Select a backup to preview. Recovery opens a draft; Save or Save as to keep it.") };
        foreach (var item in items) list.Items.Add($"{item.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {Path.GetFileName(item.SourcePath ?? item.RecoveryPath)}");
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedIndex >= 0) preview.Text = items[list.SelectedIndex].Text; };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { AutoSize = true, Text = words.Pick("取消", "Cancel"), DialogResult = DialogResult.Cancel };
        var restore = new Button { AutoSize = true, Text = words.Pick("恢复草稿", "Recover draft"), Enabled = items.Count > 0 };
        restore.Click += (_, _) => { if (list.SelectedIndex >= 0) { Selected = items[list.SelectedIndex]; DialogResult = DialogResult.OK; } };
        buttons.Controls.Add(cancel); buttons.Controls.Add(restore);
        Controls.Add(preview); Controls.Add(list); Controls.Add(note); Controls.Add(buttons);
        CancelButton = cancel; AcceptButton = restore;
        if (items.Count > 0) list.SelectedIndex = 0;
    }
}
