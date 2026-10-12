using System.Globalization;
using System.Text;
using ThothTAS.Core;

namespace ThothTAS.Host;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine("THOTH TAS 控制与实际回放验证\nrun <script.tas> [--control play|pause|step] [--speed 0.5|1|2] [--out dir]\nverify <script.tas> [--count 5] [--modes] [--out dir]\ncompose --out <combined.tas> <input1.tas> <input2.tas> ...（按顺序拼接，拒绝覆盖）\ncompare <run-dir-1> <run-dir-2>\nvalidate <script.tas>\ncheck-game（只读检查副本路径和所有游戏指纹）\n所有命令可用 --root <project-root>；启动可用 --width 1280 --height 720。\nverify 的 --modes 在重复连续回放外加入暂停继续、逐帧和两种速度。\nrun --control pause 验证暂停后继续播放；交互暂停请用编辑器。");
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            string? Option(string name) { var i = Array.IndexOf(args, name); if (i < 0) return null; if (i + 1 >= args.Length) throw new ArgumentException(name + " 缺少值。"); return args[i + 1]; }
            string Arg(int i) => args.Length > i && !args[i].StartsWith("--") ? args[i] : throw new ArgumentException("缺少位置参数。");
            if (args[0] == "compare")
            {
                var difference = ReplayVerifier.Compare(Path.GetFullPath(Arg(1)), Path.GetFullPath(Arg(2)));
                Console.WriteLine(difference == null ? "逐帧状态与完整随机记录一致。" : $"失步：帧 {difference.Frame}，{difference.File}，{difference.Field}\n期望 {difference.Expected}\n实际 {difference.Actual}");
                return difference == null ? 0 : 2;
            }
            var root = ProjectFiles.FindRoot(Option("--root"));
            if (args[0] == "compose")
            {
                var inputs = new List<string>();
                for (int index = 1; index < args.Length; index++)
                {
                    if (args[index] is "--root" or "--out") { if (++index >= args.Length || args[index].StartsWith("--")) throw new ArgumentException("拼接选项缺少值。"); continue; }
                    if (args[index].StartsWith("--")) throw new ArgumentException("未知拼接选项：" + args[index]);
                    inputs.Add(Path.GetFullPath(args[index]));
                }
                if (inputs.Count == 0) throw new ArgumentException("至少选择一个输入脚本。");
                var target = Path.GetFullPath(Option("--out") ?? throw new ArgumentException("compose 必须指定 --out 新文件。"));
                if (inputs.Any(input => string.Equals(input, target, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("拼接输出不得覆盖输入文件。");
                var sources = new List<ScriptSource>();
                foreach (var input in inputs) sources.Add(new(input, await File.ReadAllTextAsync(input, cancellation.Token)));
                var orderedStarts = (await File.ReadAllLinesAsync(Path.Combine(root, "docs", "levels.txt"), cancellation.Token)).Select(s => s.Trim()).Where(s => s.Length != 0 && !s.StartsWith('#')).ToList();
                var combined = ScriptComposer.Compose(sources, orderedStarts);
                await using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await using var writer = new StreamWriter(file, new UTF8Encoding(false));
                await writer.WriteAsync(combined.AsMemory(), cancellation.Token);
                Console.WriteLine("已生成拼接脚本：" + target + "。单关通过不代表整段连续回放已通过，请用 verify 精确复核。");
                return 0;
            }
            if (args[0] == "check-game")
            {
                ProjectFiles.EnsurePrivateGameCopy(root);
                await ProjectFiles.ValidateFingerprintAsync(root, cancellation.Token);
                Console.WriteLine("只读游戏副本路径与版本指纹检查通过，未启动游戏。");
                return 0;
            }
            var text = await File.ReadAllTextAsync(Path.GetFullPath(Arg(1)), cancellation.Token);
            var script = ProjectFiles.Parse(root, text);
            if (args[0] == "validate") { Console.WriteLine($"校验通过：{script.Start}，种子 {script.Seed}，{script.TotalFrames} 帧。"); return 0; }
            var options = new SessionOptions
            {
                Width = int.Parse(Option("--width") ?? "1280", CultureInfo.InvariantCulture),
                Height = int.Parse(Option("--height") ?? "720", CultureInfo.InvariantCulture),
                Speed = double.Parse(Option("--speed") ?? "1", CultureInfo.InvariantCulture), OutputParent = Option("--out")
            };
            if (args[0] == "run")
            {
                var run = await ReplayVerifier.RunAsync(root, text, options, Option("--control") ?? "play", Console.WriteLine, cancellation.Token);
                Console.WriteLine($"实际回放完成：{run.Frames} 帧。日志 {run.Directory}");
                return 0;
            }
            if (args[0] == "verify")
            {
                var report = await ReplayVerifier.VerifyAsync(root, text, options, int.Parse(Option("--count") ?? "5", CultureInfo.InvariantCulture), args.Contains("--modes"), Console.WriteLine, cancellation.Token);
                if (report.Error != null) Console.Error.WriteLine(report.Error);
                if (report.Differences.Count != 0) { var d = report.Differences[0]; Console.Error.WriteLine($"首个失步：帧 {d.Frame}，{d.Field}，{d.Expected} → {d.Actual}"); }
                Console.WriteLine(report.Passed ? "实际重复回放验证通过。" : "实际重复回放验证未通过，请查看报告。");
                return report.Passed ? 0 : 2;
            }
            throw new ArgumentException("未知命令：" + args[0]);
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("操作已取消；只停止本工具启动的游戏副本。"); return 130; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
