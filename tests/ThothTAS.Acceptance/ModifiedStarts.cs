using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ThothTAS.Host;

internal static class ModifiedStarts
{
    internal static async Task<int> Run(string root, string output, bool community)
    {
        Directory.CreateDirectory(output);
        var samples = new List<object>(); string? error = null;
        var options = new SessionOptions { Speed = 2, CommunityMod = community, OutputParent = Path.Combine(output, "runs") };
        var levels = File.ReadAllLines(Path.Combine(root, "docs/levels.txt")).Where(level => level != "Teleporters-5").ToArray();
        string Script(string level) => $"Version,1\nRules,Deterministic-1\nStart,{level}\nSeed,1\n\n1, N\n";
        try
        {
            await using var session = await GameSession.StartAsync(root, Script("Basic-1"), options);
            await session.WaitForStatusAsync(s => s.State == "paused" && s.Frame == 0, TimeSpan.FromSeconds(90));
            var pid = session.ProcessId;
            await session.SendAsync("PLAY"); await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            foreach (string level in levels)
            {
                string? previous = null;
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    var watch = Stopwatch.StartNew(); await session.RestartAsync(Script(level), options);
                    var ms = watch.Elapsed.TotalMilliseconds;
                    if (session.ProcessId != pid || session.Latest.Scene != level || session.Latest.Frame != 0)
                        throw new InvalidDataException($"{level}: reset did not restore the requested player-active baseline in the same PID.");
                    var directory = session.RunDirectory;
                    await session.SendAsync("PLAY"); await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));
                    ReplayVerifier.ValidateLogs(directory, 1);
                    var difference = previous == null ? null : ReplayVerifier.Compare(previous, directory);
                    samples.Add(new { level, repeat, pid, ms, directory, difference });
                    Write();
                    if (difference != null) throw new InvalidDataException($"{level}: exact two-entry comparison failed: {difference}");
                    if (ms >= 1000) throw new InvalidDataException($"{level}: reset exceeded 1000ms: {ms:F3}ms.");
                    previous = directory;
                    Console.WriteLine($"{level} #{repeat + 1}: {ms:F1}ms, same PID {pid}, exact entry comparison.");
                }
            }
        }
        catch (Exception e) { error = e.ToString(); Console.Error.WriteLine(error); }
        Write(); return error == null ? 0 : 2;
        void Write() => File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new {
            version = 1, passed = error == null && samples.Count == levels.Length * 2,
            error, rules = "Deterministic-1", community, samples,
            scope = "Actual player-active entries for all 64 rooms in the natural ordinary sequence, twice under modified rules, with exact baseline/frame/phase/random comparison and reset below 1000ms. One long-lived process exercises changing level and prior scene history. The extra Teleporters-5 resource has missing core-renderer references and is excluded. Does not independently compare every room to a fresh process, finish all rooms or establish original-game equivalence."
        }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }
}
