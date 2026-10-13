namespace ThothTAS.Editor;

internal sealed class UnsavedDraftForm : Form
{
    internal UnsavedDraftForm(EditorText words, string document)
    {
        Text = words.Pick("尚有未保存的修改", "Unsaved changes");
        ClientSize = new Size(510, 170);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 10);
        var explanation = new Label { Dock = DockStyle.Fill, Padding = new Padding(18), Text = words.Pick("是否保存对以下草稿的修改？\r\n\r\n", "Save changes to this draft?\r\n\r\n") + document };
        var choices = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        foreach (var choice in new[] { ("取消", "Cancel", DialogResult.Cancel), ("不保存", "Don't save", DialogResult.No), ("保存", "Save", DialogResult.Yes) })
        {
            var button = new Button { AutoSize = true, Text = words.Pick(choice.Item1, choice.Item2), DialogResult = choice.Item3, Tag = choice.Item3 };
            choices.Controls.Add(button);
            if (choice.Item3 == DialogResult.Cancel) CancelButton = button;
            if (choice.Item3 == DialogResult.Yes) AcceptButton = button;
        }
        Controls.Add(explanation); Controls.Add(choices);
    }
    internal void TestChoose(DialogResult result)
        => Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b => Equals(b.Tag, result)).PerformClick();
}
