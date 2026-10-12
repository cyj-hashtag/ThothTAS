using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ThothTAS.Core;
using ThothTAS.Host;

internal sealed class AcceptanceReport
{
    public int Version { get; init; } = 1;
    public string Kind { get; init; } = "";
    public string Scope { get; init; } = "";
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }
    public bool Passed { get; set; }
    public List<CaseReport> Cases { get; } = [];
}

internal sealed class CaseReport
{
    public string Name { get; init; } = "";
    public bool ActualGame { get; init; }
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }
    public bool Passed { get; set; }
    public string? Error { get; set; }
    public List<string> Checks { get; } = [];
    public List<string> RunDirectories { get; } = [];
    public Dictionary<string, object?> Evidence { get; } = new(StringComparer.Ordinal);
}

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Regex FatalLog = new(@"\bERROR\b|NullReference|MissingMethodException|InvalidProgramException|Crash!!!|Couldn.t be loaded|Unable to load scene|THOTH TAS:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string root = "";
    private static string output = "";
    private static string rawParent = "";
    private static AcceptanceReport report = new();

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            Console.WriteLine("THOTH TAS 验收入口：contract | fingerprint | inputs | seeds | protocol | divergence | actual-all [--root 路径] [--out 报告目录]");
            Console.WriteLine("contract为纯解析；fingerprint仅校验隔离根目录中的实际文件；其余命令启动独立游戏副本。逐关卡验证使用 scripts/Verify-LevelStarts.ps1。");
            return 0;
        }
        try
        {
            string command = args[0];
            if (command is not ("contract" or "fingerprint" or "inputs" or "seeds" or "protocol" or "divergence" or "actual-all")) throw new ArgumentException("未知验收命令：" + command);
            string? Option(string key)
            {
                int index = Array.IndexOf(args, key);
                if (index < 0) return null;
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(key + " 缺少参数。");
                return args[index + 1];
            }
            root = ProjectFiles.FindRoot(Option("--root"));
            bool actual = command is not ("contract" or "fingerprint");
            output = Path.GetFullPath(Option("--out") ?? Path.Combine(root, "docs", "verification", actual ? "acceptance-actual" : "acceptance-" + command));
            rawParent = Path.Combine(root, "work", "acceptance", command + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            if (actual || command == "fingerprint") Directory.CreateDirectory(rawParent);
            report = new AcceptanceReport { Kind = actual ? "actual-game" : command == "fingerprint" ? "isolated-actual-file-validation" : "pure-parser-contract", Scope = actual ? "当前电脑和游戏指纹；实际副本进程、真实帧文件及完整 Unity 日志。仅覆盖列出的测试，不能代替全部关卡及战斗路径验收。" : command == "fingerprint" ? "隔离根目录中实际文件的SHA256校验及拒绝测试；未启动游戏，不证明游戏运行或回放确定性。" : "纯脚本解析和数学契约；没有启动游戏，不证明游戏回放一致性。" };
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            if (command == "contract") await RunCase("纯解析错误与瞄准数学契约", false, Contract, cancellation.Token);
            if (command == "fingerprint") await RunCase("隔离实际文件：错误摘要及单字节修改必须拒绝", false, FingerprintFiles, cancellation.Token);
            if (command is "inputs" or "actual-all") await RunCase("实际移动、射击角度与逐帧世界目标", true, Inputs, cancellation.Token);
            if (command is "seeds" or "actual-all") await RunCase("相同种子重复及不同种子随机记录", true, Seeds, cancellation.Token);
            if (command is "protocol" or "actual-all") await RunCase("运行中断线、错误协议及正确协议重连", true, Protocol, cancellation.Token);
            if (command is "divergence" or "actual-all") await RunCase("人为修改输入并定位首个失步", true, Divergence, cancellation.Token);
            report.FinishedUtc = DateTime.UtcNow;
            report.Passed = report.Cases.Count > 0 && report.Cases.All(c => c.Passed);
            await WriteReport();
            Progress($"完成：{report.Cases.Count(c => c.Passed)}/{report.Cases.Count} 通过；报告 {Path.Combine(output, "report.md")}");
            return report.Passed ? 0 : 2;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static async Task RunCase(string name, bool actual, Func<CaseReport, CancellationToken, Task> body, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CaseReport item = new() { Name = name, ActualGame = actual };
        report.Cases.Add(item);
        Progress("开始：" + name);
        try { await body(item, token); item.Passed = true; }
        catch (Exception e) { item.Error = e.ToString(); item.Passed = false; }
        item.FinishedUtc = DateTime.UtcNow;
        await WriteReport();
        Progress((item.Passed ? "通过：" : "失败：") + name + (item.Error == null ? "" : "；" + item.Error.Split('\n')[0]));
    }

    private static Task Contract(CaseReport item, CancellationToken _)
    {
        var starts = new HashSet<string>(StringComparer.Ordinal) { "Basic-1" };
        string[] errors = ["Version,2\nStart,Basic-1\n1,N", "Version,1\nStart,Unknown\n1,N", "Version,1\nStart,Basic-1\n1,M=NaN", "Version,1\nStart,Basic-1\n1,M=0,U", "Version,1\nStart,Basic-1\n1,A=(1,2,3)", "Version,1\nStart,Basic-1\n1,A=0,A=90"];
        foreach (string text in errors)
        {
            bool rejected = false;
            try { ScriptParser.Parse(text, starts); }
            catch (ScriptParseException e) { rejected = e.Line >= 1 && e.Column >= 1; }
            Require(rejected, "非法脚本必须带行列拒绝。");
        }
        var parsed = ScriptParser.Parse("Version,1\nStart,Basic-1\n1,M=-90,A=(-12.5,3.25)\n1,N,A=0:0", starts);
        Near(parsed.Segments[0].Input.MoveX, 0, 1e-12, "负角度X"); Near(parsed.Segments[0].Input.MoveY, -1, 1e-12, "负角度Y");
        parsed.Segments[0].Input.ResolveAim(-12.5, 3.25, out var x, out var y);
        Near(x, 0, 0, "重合点X"); Near(y, 0, 0, "重合点Y");
        Require(parsed.Seed == 1, "省略种子必须默认1。");
        item.Checks.Add("6种非法脚本均定位拒绝；负角度、负坐标、小数、括号逗号、重合目标与默认种子通过。");
        item.Evidence["gameProcessesStarted"] = 0;
        item.Evidence["fullCoreContractCommand"] = "dotnet run --project tests/ThothTAS.Core.Tests -c Release";
        return Task.CompletedTask;
    }

    private static async Task FingerprintFiles(CaseReport item, CancellationToken token)
    {
        string fixture = Path.Combine(rawParent, "isolated-root");
        string game = Path.Combine(fixture, "game");
        string docs = Path.Combine(fixture, "docs");
        Directory.CreateDirectory(docs);
        string[] names = ["THOTH.exe", "THOTH_Data/Managed/Assembly-CSharp.dll", "THOTH_Data/Managed/Assembly-CSharp-firstpass.dll", "THOTH_Data/Managed/Assembly-UnityScript-firstpass.dll", "THOTH_Data/level0"];
        var entries = new List<Dictionary<string, string>>();
        for (int index = 0; index < names.Length; index++)
        {
            string path = Path.Combine(game, names[index].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Plain, small fixture files, no links and no executable game binary.
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("THOTH TAS isolated fingerprint fixture " + index.ToString(CultureInfo.InvariantCulture)), token);
            entries.Add(new Dictionary<string, string> { ["name"] = names[index], ["sha256"] = FileHash(path) });
        }
        string manifestPath = Path.Combine(docs, "game-fingerprint.json");
        async Task WriteFixtureManifest() => await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new { version = 1, files = entries }, JsonOptions), Utf8, token);
        await WriteFixtureManifest();
        await ProjectFiles.ValidateFingerprintAsync(fixture, token);
        string originalExpected = entries[0]["sha256"];
        entries[0]["sha256"] = new string('0', 64);
        await WriteFixtureManifest();
        string expectedError = await Rejection();
        entries[0]["sha256"] = originalExpected;
        await WriteFixtureManifest();
        string exe = Path.Combine(game, "THOTH.exe");
        byte[] bytes = await File.ReadAllBytesAsync(exe, token);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(exe, bytes, token);
        string changedError = await Rejection();
        item.Checks.Add("使用无链接的work隔离根目录，5个普通小文件和完整必要清单首先校验通过。");
        item.Checks.Add("将隔离清单的EXE预期摘要改为64个0，实际文件校验拒绝；恢复隔离清单后改变EXE一个字节，仍按固定摘要拒绝。");
        item.Checks.Add("只调用ProjectFiles.ValidateFingerprintAsync；未调用GameSession.StartAsync、ReplayVerifier或Process.Start，原版和当前项目docs/game不修改。");
        item.Evidence["fixtureRoot"] = fixture;
        item.Evidence["gameProcessesStarted"] = 0;
        item.Evidence["wrongExpectedHashError"] = expectedError;
        item.Evidence["singleByteMutationError"] = changedError;
        item.Evidence["expectedExeSha256"] = originalExpected;
        item.Evidence["changedExeSha256"] = FileHash(exe);

        async Task<string> Rejection()
        {
            try { await ProjectFiles.ValidateFingerprintAsync(fixture, token); }
            catch (InvalidDataException e)
            {
                Require(e.Message.Contains("游戏版本指纹不匹配", StringComparison.Ordinal) && e.Message.Contains("THOTH.exe", StringComparison.Ordinal), "错误应由实际EXE摘要不符引起，而非路径护栏或清单缺失。");
                return e.Message;
            }
            throw new InvalidDataException("错误指纹未被拒绝。");
        }
    }

    private static async Task Inputs(CaseReport item, CancellationToken token)
    {
        string script = Script("2,M=0,A=0\n2,M=90,A=90\n2,M=180,A=180\n2,M=-90,A=270\n2,M=0.5:0,A=0:0\n2,UR,A=1:0\n4,M=45,A=(-12.5,3.25)\n2,N,A=0.1:0");
        var parsed = ProjectFiles.Parse(root, script);
        var records = await Stepped(item, script, "inputs", token);
        int targets = 0;
        var resolvedTargets = new List<double[]>();
        foreach (var record in records.Where(r => r.GetProperty("frame").GetInt32() > 0))
        {
            int frame = record.GetProperty("frame").GetInt32();
            var input = parsed.GetInputAtFrame(frame - 1, out var line);
            Require(record.GetProperty("line").GetInt32() == line, "实际执行行与帧段不符。");
            var fields = record.GetProperty("fields");
            Near(Number(fields, "Input.moveX"), input.MoveX, 1e-12, "记录的移动X");
            Near(Number(fields, "Input.moveY"), input.MoveY, 1e-12, "记录的移动Y");
            Near(Number(fields, "Input.aimX"), input.AimX, 1e-12, "记录的瞄准X");
            Near(Number(fields, "Input.aimY"), input.AimY, 1e-12, "记录的瞄准Y");
            var velocity = ComponentVector(fields, "/Player.Player.targetVelocity");
            double moveLength = Math.Sqrt(input.MoveX * input.MoveX + input.MoveY * input.MoveY);
            double speed = Math.Sqrt(velocity[0] * velocity[0] + velocity[1] * velocity[1]);
            if (moveLength >= 0.3)
            {
                Require(speed > 0, "非零移动未被游戏 Player 消费。");
                Near(velocity[0] / speed, input.MoveX / moveLength, 1e-5, "实际玩家速度方向X");
                Near(velocity[1] / speed, input.MoveY / moveLength, 1e-5, "实际玩家速度方向Y");
            }
            else Near(speed, 0, 1e-5, "零输入玩家速度");
            double ax = input.AimX, ay = input.AimY;
            if (input.AimKind == AimKind.WorldTarget)
            {
                var origin = Vector(fields.GetProperty("Input.shootPosition").GetString()!);
                ax -= origin[0]; ay -= origin[1];
                double length = Math.Sqrt(ax * ax + ay * ay);
                if (length == 0) ax = ay = 0; else { ax /= length; ay /= length; }
                var resolved = Vector(fields.GetProperty("Input.resolvedAim").GetString()!);
                Near(resolved[0], ax, 1e-5, "运行模块逐帧目标方向X"); Near(resolved[1], ay, 1e-5, "运行模块逐帧目标方向Y");
                resolvedTargets.Add(resolved); targets++;
            }
            if (input.AimKind == AimKind.None || ax * ax + ay * ay < 0.16000001) ax = ay = 0;
            var consumedAim = ComponentVector(fields, "/PlayerAim.PlayerAim.currentAimStick");
            Near(consumedAim[0], ax, 1e-5, "实际 PlayerAim 输入X"); Near(consumedAim[1], ay, 1e-5, "实际 PlayerAim 输入Y");
        }
        Require(targets == 4 && resolvedTargets.Skip(1).Any(v => Math.Abs(v[0] - resolvedTargets[0][0]) + Math.Abs(v[1] - resolvedTargets[0][1]) > 1e-6), "移动过程中的固定目标方向没有重新计算。");
        var startPosition = ComponentVector(records[0].GetProperty("fields"), "/Player.position");
        string coincident = "2,N,A=(" + startPosition[0].ToString("R", CultureInfo.InvariantCulture) + "," + startPosition[1].ToString("R", CultureInfo.InvariantCulture) + ")";
        var same = await Stepped(item, Script(coincident), "coincident", token);
        var sameStart = ComponentVector(same[0].GetProperty("fields"), "/Player.position");
        Near(sameStart[0], startPosition[0], 0, "重合测试初始位置X"); Near(sameStart[1], startPosition[1], 0, "重合测试初始位置Y");
        foreach (var r in same.Skip(1))
        {
            var fields = r.GetProperty("fields");
            var resolved = Vector(fields.GetProperty("Input.resolvedAim").GetString()!);
            var consumed = ComponentVector(fields, "/PlayerAim.PlayerAim.currentAimStick");
            Near(resolved[0], 0, 0, "重合目标停止射击X"); Near(resolved[1], 0, 0, "重合目标停止射击Y");
            Near(consumed[0], 0, 0, "游戏重合瞄准X"); Near(consumed[1], 0, 0, "游戏重合瞄准Y");
        }
        item.Checks.Add("逐帧核对脚本输入、Player.targetVelocity方向及PlayerAim.currentAimStick；包含四轴角度、负角度、模拟幅度、方向组合、停火与原游戏射击死区。");
        item.Checks.Add("负坐标小数目标在移动中每帧重新计算；独立进程使用实际初始位置验证重合目标停火。");
        item.Evidence["inputFrames"] = parsed.TotalFrames;
        item.Evidence["worldTargetFrames"] = targets;
        item.Evidence["semanticTolerance"] = "方向和float转换检查1e-5；逐帧确定性比较不使用此容差。";
    }

    private static async Task Seeds(CaseReport item, CancellationToken token)
    {
        var a = await ReplayVerifier.RunAsync(root, Script("4,N", 1), Options("seed1-a"), "play", Progress, token);
        item.RunDirectories.Add(a.Directory);
        var b = await ReplayVerifier.RunAsync(root, Script("4,N", 1), Options("seed1-b") with { ReferenceFrames = Path.Combine(a.Directory, "frames.jsonl") }, "play", Progress, token);
        item.RunDirectories.Add(b.Directory);
        var c = await ReplayVerifier.RunAsync(root, Script("4,N", 2), Options("seed2"), "play", Progress, token);
        item.RunDirectories.Add(c.Directory);
        foreach (var run in new[] { a, b, c }) InspectLogs(item, run.Directory, false);
        Require(ReplayVerifier.Compare(a.Directory, b.Directory) == null, "相同种子的基线、逐帧玩法和完整随机记录不一致。");
        var randomA = ReadLinesShared(Path.Combine(a.Directory, "random.tsv"));
        var randomC = ReadLinesShared(Path.Combine(c.Directory, "random.tsv"));
        Require(randomA.Length > 1 && randomC.Length > 1, "种子差异测试未实际执行随机调用。");
        Require(!randomA.SequenceEqual(randomC), "不同种子没有改变实际随机记录。");
        item.Checks.Add("种子1两次独立进程完整比较实际frames.jsonl和random.tsv，包括frame0初始基线。");
        item.Checks.Add("种子2独立进程的非空实际随机调用记录与种子1不同。");
        item.Evidence["initialBaselineComparedFromActualFiles"] = true;
        item.Evidence["randomCallRecordsSeed1"] = randomA.Length - 1;
        item.Evidence["randomCallRecordsSeed2"] = randomC.Length - 1;
        item.Evidence["firstRandomDifferenceRecord"] = Enumerable.Range(0, Math.Max(randomA.Length, randomC.Length)).First(i => i >= randomA.Length || i >= randomC.Length || randomA[i] != randomC[i]);
    }

    private static async Task Protocol(CaseReport item, CancellationToken token)
    {
        await using var session = await GameSession.StartAsync(root, Script("60,N"), Options("protocol"), token);
        item.RunDirectories.Add(session.RunDirectory);
        await session.WaitForStatusAsync(s => s.State == "paused" && s.Frame == 0, TimeSpan.FromSeconds(90), token);
        await session.SendAsync("PLAY", token);
        await session.WaitForStatusAsync(s => s.Frame >= 8, TimeSpan.FromSeconds(20), token);
        var config = ReadLinesShared(Path.Combine(session.RunDirectory, "session.cfg")).Select(line => line.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        var field = typeof(GameSession).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException("当前测试接缝client不存在。");
        ((TcpClient?)field.GetValue(session) ?? throw new InvalidOperationException("没有已连接的受控客户端。")).Dispose();
        item.Evidence["disconnectTestSeam"] = "测试专用反射：仅关闭当前GameSession拥有的TcpClient，仍由Host管理进程。";
        await Task.Delay(350, token);
        var pausedA = LastRecord(session.RunDirectory);
        var filesA = PauseFiles(session.RunDirectory);
        await Task.Delay(450, token);
        var pausedB = LastRecord(session.RunDirectory);
        var filesB = PauseFiles(session.RunDirectory);
        SamePause(pausedA, pausedB);
        Require(filesA.SequenceEqual(filesB), "断线期间帧、随机或阶段文件继续改变。");
        int port = int.Parse(config["port"], CultureInfo.InvariantCulture);
        await using (var wrong = await RawControl.Connect(port, token))
        {
            await wrong.Send("HELLO\t999\t" + config["token"], token);
            var rejected = await wrong.Receive(s => s.Type == "error", token);
            Require(rejected.Error?.Contains("协议版本", StringComparison.Ordinal) == true, "使用正确令牌但错误版本未明确拒绝。");
            item.Evidence["wrongVersionError"] = rejected.Error;
        }
        await using (var correct = await RawControl.Connect(port, token))
        {
            await correct.Send("HELLO\t1\t" + config["token"], token);
            await correct.Receive(s => s.Type == "hello", token);
            await correct.Receive(s => s.Type == "status", token);
            await Task.Delay(350, token);
            Require(PauseFiles(session.RunDirectory).SequenceEqual(filesB), "正确重连后未经PLAY或STEP就继续推进。");
            int before = pausedB.GetProperty("frame").GetInt32();
            await correct.Send("STEP", token);
            var stepped = await correct.Receive(s => s.Frame > before, token);
            Require(stepped.Frame == before + 1 && stepped.State == "paused", "重连后的STEP未准确放行一个完整帧。");
            item.Evidence["disconnectPausedFrame"] = before;
            item.Evidence["reconnectStepFrame"] = stepped.Frame;
            await correct.Send("PLAY", token);
            var completed = await correct.Receive(s => s.State == "complete", token);
            Require(completed.Frame == 60, "重连恢复后没有完成全部60个输入帧。");
            ReplayVerifier.ValidateLogs(session.RunDirectory, 60);
            item.Evidence["reconnectCompletedFrame"] = completed.Frame;
            await correct.Send("STOP", token);
        }
        try { await session.Completion; } catch (IOException) { } catch (ObjectDisposedException) { }
        await session.DisposeAsync();
        InspectLogs(item, session.RunDirectory, false);
        item.Checks.Add("播放中关闭当前受控TCP；稳定边界后400ms内实际帧、随机计数/摘要及阶段文件不变化。");
        item.Checks.Add("保持同一游戏进程，用正确令牌发送HELLO版本999被拒绝；版本1重连后保持暂停，准确STEP一次，再恢复播放完成全部60帧。");
    }

    private static async Task Divergence(CaseReport item, CancellationToken token)
    {
        string referenceScript = Script("1,N\n3,N");
        var reference = await ReplayVerifier.RunAsync(root, referenceScript, Options("reference"), "play", Progress, token);
        item.RunDirectories.Add(reference.Directory);
        InspectLogs(item, reference.Directory, false);
        await using var altered = await GameSession.StartAsync(root, Script("1,N\n3,M=0"), Options("altered") with { ReferenceFrames = Path.Combine(reference.Directory, "frames.jsonl") }, token);
        item.RunDirectories.Add(altered.RunDirectory);
        await altered.WaitForStatusAsync(s => s.State == "paused" && s.Frame == 0, TimeSpan.FromSeconds(90), token);
        await altered.StepAsync(token);
        try { await altered.StepAsync(token); } catch (InvalidOperationException) { }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        string discrepancyPath = Path.Combine(altered.RunDirectory, "discrepancy.json");
        while (!File.Exists(discrepancyPath)) await Task.Delay(50, limit.Token);
        using var discrepancy = JsonDocument.Parse(ReadTextShared(discrepancyPath));
        var difference = discrepancy.RootElement;
        Require(difference.GetProperty("frame").GetInt32() == 2, "人为修改第二个输入帧应首次在实际记录frame2分歧；更早的差异属于基线或运行不稳定。");
        Require(difference.GetProperty("field").GetString() is { Length: > 0 } field && field != "$record", "失步缺少具体实体或字段。");
        var before = PauseFiles(altered.RunDirectory);
        await Task.Delay(400, token);
        Require(before.SequenceEqual(PauseFiles(altered.RunDirectory)), "失步后玩法或随机记录继续推进。");
        item.Evidence["runtimeDiscrepancy"] = difference.Clone();
        item.Evidence["referenceFrames"] = Path.Combine(reference.Directory, "frames.jsonl");
        item.Evidence["alteredFrames"] = Path.Combine(altered.RunDirectory, "frames.jsonl");
        item.Checks.Add("参考与修改脚本保持相同行号和首帧；只把第二段N改为M=0，实际运行模块在frame2报告具体字段并停止推进。");
        try { await altered.Completion; } catch (InvalidOperationException) { }
        await altered.DisposeAsync();
        InspectLogs(item, altered.RunDirectory, true);
    }

    private static async Task<List<JsonElement>> Stepped(CaseReport item, string text, string folder, CancellationToken token)
    {
        string run;
        int frames;
        await using (var session = await GameSession.StartAsync(root, text, Options(folder), token))
        {
            run = session.RunDirectory; frames = session.Script.TotalFrames;
            item.RunDirectories.Add(run);
            await session.WaitForStatusAsync(s => s.State == "paused" && s.Frame == 0, TimeSpan.FromSeconds(90), token);
            await WaitForRecordedFrame(run, 0, token);
            for (int frame = 1; frame <= frames; frame++)
            {
                var status = await session.StepAsync(token);
                Require(status.Frame == frame, "逐帧指令漏帧或跳帧。");
                await WaitForRecordedFrame(run, frame, token);
            }
            await session.Completion.WaitAsync(token);
            ReplayVerifier.ValidateLogs(run, frames);
        }
        InspectLogs(item, run, false);
        var records = ReadRecords(run);
        Require(records.Count == frames + 1 && records[0].GetProperty("scene").GetString() == "Basic-1", "缺少真实初始基线、输入帧或目标场景错误。");
        return records;
    }

    private static SessionOptions Options(string folder) => new() { Autoplay = false, OutputParent = Path.Combine(rawParent, folder), Width = 1280, Height = 720, Speed = 1 };
    private static string Script(string segments, int seed = 1) => "Version,1\nStart,Basic-1\nSeed," + seed.ToString(CultureInfo.InvariantCulture) + "\n" + segments + "\n";
    private static double Number(JsonElement fields, string key) => Numeric(fields.GetProperty(key).GetString()!);
    private static double Numeric(string value) => double.Parse(value.Split('@')[0], NumberStyles.Float, CultureInfo.InvariantCulture);
    private static double[] Vector(string value) => value.Split(',').Select(Numeric).ToArray();
    private static double[] ComponentVector(JsonElement fields, string suffix)
    {
        var found = fields.EnumerateObject().Where(p => p.Name.EndsWith(suffix, StringComparison.Ordinal)).ToArray();
        Require(found.Length == 1, "游戏玩家字段缺失或存在歧义：" + suffix);
        return Vector(found[0].Value.GetString()!);
    }
    private static string[] ReadLinesShared(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Utf8);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null) lines.Add(line);
        return lines.ToArray();
    }
    private static string ReadTextShared(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Utf8);
        return reader.ReadToEnd();
    }
    private static List<JsonElement> ReadRecords(string run)
    {
        using var reader = new StreamReader(new FileStream(Path.Combine(run, "frames.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Utf8);
        var records = new List<JsonElement>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            using var doc = JsonDocument.Parse(line);
            records.Add(doc.RootElement.Clone());
        }
        return records;
    }
    private static JsonElement LastRecord(string run)
    {
        var records = ReadRecords(run);
        Require(records.Count > 0, "没有实际帧记录。");
        return records[^1];
    }
    private static async Task WaitForRecordedFrame(string run, int expectedFrame, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                var last = LastRecord(run).GetProperty("frame").GetInt32();
                if (last == expectedFrame) return;
                Require(last < expectedFrame, "实际帧文件跳过要求的完整帧：" + expectedFrame);
            }
            catch (JsonException) { /* The status message can precede boundary Flush; retry incomplete writes. */ }
            await Task.Delay(20, limit.Token);
        }
    }
    private static string[] PauseFiles(string run) => new[] { "frames.jsonl", "random.tsv", "phases.tsv" }.Select(name => FileHash(Path.Combine(run, name))).ToArray();
    private static string FileHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void SamePause(JsonElement a, JsonElement b)
    {
        foreach (string key in new[] { "frame", "randomCount", "randomHash", "phases" }) Require(a.GetProperty(key).GetRawText() == b.GetProperty(key).GetRawText(), "暂停数据改变：" + key);
    }
    private static void InspectLogs(CaseReport item, string run, bool expectedDivergence)
    {
        string path = Path.Combine(run, "unity.log");
        Require(File.Exists(path), "缺少实际完整unity.log。");
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Utf8);
        int count = 0;
        var issues = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            count++;
            if (FatalLog.IsMatch(line) && !(expectedDivergence && line.Contains("失步", StringComparison.Ordinal) && !line.Contains("NullReference", StringComparison.OrdinalIgnoreCase))) issues.Add(count + ": " + line);
        }
        item.Evidence["unityLog:" + run] = new { path, linesRead = count, sha256 = FileHash(path), expectedDivergence, unexpectedErrors = issues };
        Require(issues.Count == 0, "完整Unity日志包含异常：" + string.Join(" | ", issues));
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Near(double actual, double expected, double tolerance, string label) => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, label + "：期望 " + expected.ToString("R", CultureInfo.InvariantCulture) + "，实际 " + actual.ToString("R", CultureInfo.InvariantCulture));
    private static void Progress(string message) => Console.WriteLine("[" + DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + "] " + message);
    private static async Task WriteReport()
    {
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, JsonOptions), Utf8);
        var text = new StringBuilder("# THOTH TAS 专项验收报告\n\n").AppendLine(report.Scope).AppendLine();
        text.AppendLine("类型：`" + report.Kind + "`。结果：" + (report.FinishedUtc == null ? "执行中" : report.Passed ? "通过" : "未通过") + "。\n");
        foreach (var item in report.Cases)
        {
            text.AppendLine("## " + item.Name + "\n").AppendLine("结果：" + (item.FinishedUtc == null ? "执行中" : item.Passed ? "通过" : "未通过") + "；实际游戏=" + item.ActualGame + "。\n");
            foreach (var check in item.Checks) text.AppendLine("- " + check);
            foreach (var run in item.RunDirectories) text.AppendLine("- 原始记录：`" + run + "`");
            if (item.Error != null) text.AppendLine("\n```text\n" + item.Error + "\n```\n");
        }
        text.AppendLine("\n每项证据、完整日志扫描结果及首个差异字段详见同目录report.json。缺帧、异常和缺失记录不会被计为通过；语义方向检查的浮点容差不能替代逐帧确定性比较。未执行的路径未验证。\n");
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), text.ToString(), Utf8);
    }
}

internal sealed class RawControl : IAsyncDisposable
{
    private readonly TcpClient client;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private RawControl(TcpClient client)
    {
        this.client = client;
        reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
        writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }
    public static async Task<RawControl> Connect(int port, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        try { await client.ConnectAsync(IPAddress.Loopback, port, token); return new RawControl(client); }
        catch { client.Dispose(); throw; }
    }
    public Task Send(string command, CancellationToken token) => writer.WriteLineAsync(command.AsMemory(), token);
    public async Task<GameStatus> Receive(Func<GameStatus, bool> predicate, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            string? line = await reader.ReadLineAsync(limit.Token);
            if (line == null) throw new IOException("测试原始TCP连接提前关闭。");
            var status = JsonSerializer.Deserialize<GameStatus>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("空协议消息。");
            if (predicate(status)) return status;
            if (status.Type == "error") throw new InvalidOperationException(status.Error ?? "协议错误。");
        }
    }
    public ValueTask DisposeAsync() { client.Dispose(); reader.Dispose(); writer.Dispose(); return ValueTask.CompletedTask; }
}
