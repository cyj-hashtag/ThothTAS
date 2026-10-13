using System.Text;
using System.Text.Json;

namespace ThothTAS.Editor;

internal sealed record RecoveredDraft(string RecoveryPath, string? SourcePath, string Text, DateTime SavedUtc, bool ActiveWindow = false);

// One durable checkpoint per editor window. Checkpoints never overwrite .tas documents.
internal sealed class DraftRecoveryStore(string root)
{
    private readonly string directory = Path.Combine(root, "work", "draft-recovery");
    private readonly string id = Guid.NewGuid().ToString("N");
    internal string CheckpointPath => Path.Combine(directory, "draft-" + id + ".json");
    private sealed class Checkpoint
    {
        public int Version { get; set; } = 1;
        public string? SourcePath { get; set; }
        public string Text { get; set; } = "";
        public DateTime SavedUtc { get; set; }
        public int ProcessId { get; set; }
        public DateTime ProcessStartUtc { get; set; }
    }
    internal void Write(string? sourcePath, string text)
    {
        Directory.CreateDirectory(directory);
        var temporary = CheckpointPath + ".tmp";
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Checkpoint { SourcePath = sourcePath, Text = text, SavedUtc = DateTime.UtcNow, ProcessId = process.Id, ProcessStartUtc = process.StartTime.ToUniversalTime() });
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        File.Move(temporary, CheckpointPath, true);
    }
    internal void Clear() { if (File.Exists(CheckpointPath)) File.Delete(CheckpointPath); }
    internal RecoveredDraft Read(string path)
    {
        if (Path.GetExtension(path).Equals(".tas", StringComparison.OrdinalIgnoreCase))
            return new(path, null, File.ReadAllText(path, Encoding.UTF8), File.GetLastWriteTimeUtc(path));
        var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path, Encoding.UTF8)) ?? throw new InvalidDataException("Empty draft checkpoint.");
        if (checkpoint.Version != 1) throw new InvalidDataException("Unsupported draft checkpoint version.");
        bool active = false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(checkpoint.ProcessId);
            active = !process.HasExited && process.StartTime.ToUniversalTime() == checkpoint.ProcessStartUtc;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return new(path, checkpoint.SourcePath, checkpoint.Text, checkpoint.SavedUtc, active);
    }
    internal static bool Pending(RecoveredDraft item) => !item.ActiveWindow && Path.GetFileName(item.RecoveryPath).StartsWith("draft-", StringComparison.Ordinal) && Path.GetExtension(item.RecoveryPath) == ".json";
    internal void MarkRecovered(RecoveredDraft item)
    {
        if (Pending(item)) File.Move(item.RecoveryPath, Path.Combine(directory, "recovered-" + Path.GetFileName(item.RecoveryPath)));
    }
    internal IReadOnlyList<RecoveredDraft> List()
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<RecoveredDraft>();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (path == CheckpointPath || Path.GetExtension(path) is not (".json" or ".tas")) continue;
            try { result.Add(Read(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }
        return result.OrderByDescending(item => item.SavedUtc).ToArray();
    }
}
