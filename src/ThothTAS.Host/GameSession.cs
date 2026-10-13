using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThothTAS.Core;

namespace ThothTAS.Host;

public sealed record SessionOptions
{
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public double Speed { get; init; } = 1;
    public bool Autoplay { get; init; }
    public bool CommunityMod { get; init; }
    public string? OutputParent { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public string? ReferenceFrames { get; init; }
}

public sealed class GameStatus
{
    public int Protocol { get; set; }
    public string Type { get; set; } = "status";
    public int Frame { get; set; }
    public int Line { get; set; }
    public string Scene { get; set; } = "";
    public string State { get; set; } = "starting";
    public double X { get; set; }
    public double Y { get; set; }
    public long RandomCount { get; set; }
    public string RandomHash { get; set; } = "";
    public string? Error { get; set; }
    public string Run { get; set; } = "";
    public int Chapter { get; set; }
    public string ChapterScene { get; set; } = "";
    public int ChapterFrame { get; set; }
    public int SegmentFrame { get; set; }
    public int SegmentFrames { get; set; }
    public List<GameRoomEvent> RoomEvents { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class GameRoomEvent
{
    public int Frame { get; set; }
    public string Scene { get; set; } = "";
    public string Type { get; set; } = "";
}

/// <summary>Owns exactly one independently launched, path-verified THOTH copy.</summary>
public sealed class GameSession : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly SemaphoreSlim logGate = new(1, 1);
    private readonly SemaphoreSlim restartGate = new(1, 1);
    private readonly object stateGate = new();
    private TaskCompletionSource stateChanged = NewSignal();
    private TaskCompletionSource<GameStatus> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource hello = NewSignal();
    private Process? process;
    private TcpClient? client;
    private StreamReader? reader;
    private StreamWriter? writer;
    private StreamWriter? protocolLog;
    private StreamWriter? commandLog;
    private FileStream? instanceLock;
    private readonly List<FileStream> fingerprintLocks = [];
    private readonly HashSet<string> pinnedFingerprintNames = new(StringComparer.OrdinalIgnoreCase);
    private Task? readerTask;
    private bool disposed;
    private string gameExe = "";
    private SessionOptions activeOptions = new();
    private int activePort;
    private string activeToken = "";
    private string initialUnityLogPath = "";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr processHandle, int flags, StringBuilder exeName, ref int size);
    public string Root { get; private init; } = "";
    public string RunDirectory { get; private set; } = "";
    public TasScript Script { get; private set; } = null!;
    public GameStatus Latest { get; private set; } = new();
    public int ProcessId => process?.Id ?? 0;
    public event Action<GameStatus>? StatusChanged;
    public Task<GameStatus> Completion => finished.Task;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task<GameSession> StartAsync(string root, string scriptText, SessionOptions? options = null, CancellationToken cancellation = default)
    {
        options ??= new();
        if (options.Width < 640 || options.Width > 7680 || options.Height < 360 || options.Height > 4320) throw new ArgumentOutOfRangeException(nameof(options), "窗口尺寸须在 640×360 至 7680×4320 之间。");
        ValidateSpeed(options.Speed);
        root = Path.GetFullPath(root);
        var session = new GameSession { Root = root };
        try
        {
            session.Script = ProjectFiles.Parse(root, scriptText);
            ProjectFiles.EnsurePrivateGameCopy(root);
            session.gameExe = Path.GetFullPath(Path.Combine(root, "game", "THOTH.exe"));
            var baseline = Path.Combine(root, "docs", "baseline", "THOTH.sav");
            if (!File.Exists(baseline)) throw new FileNotFoundException("缺少固定初始存档 docs/baseline/THOTH.sav。", baseline);
            session.instanceLock = new FileStream(Path.Combine(root, "game", ".thoth-tas.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            session.LockFingerprintFiles();
            await ProjectFiles.ValidateFingerprintAsync(root, cancellation);
            var communityLock = CommunityModProfile.ValidateAndLock(root, options.CommunityMod);
            if (communityLock != null) session.fingerprintLocks.Add(communityLock);
            var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in new[] { "docs/game-fingerprint.json", "game/BepInEx/plugins/ThothTAS.Runtime.dll", "game/BepInEx/patchers/ThothTAS.Preloader.dll", "game/BepInEx/config/BepInEx.cfg" })
            {
                var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) throw new FileNotFoundException("缺少固定 TAS 模块或配置：" + name, path);
                await using var artifact = File.OpenRead(path);
                artifacts[name] = Convert.ToHexString(await SHA256.HashDataAsync(artifact, cancellation));
            }
            foreach (var name in new[] { "game/doorstop_config.ini", "game/winhttp.dll" })
            {
                var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) continue;
                await using var artifact = File.OpenRead(path);
                artifacts[name] = Convert.ToHexString(await SHA256.HashDataAsync(artifact, cancellation));
            }
            var parent = Path.GetFullPath(options.OutputParent ?? Path.Combine(root, "work", "runs"));
            session.RunDirectory = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(session.RunDirectory);
            var snapshot = Path.Combine(session.RunDirectory, "script.tas");
            await File.WriteAllTextAsync(snapshot, scriptText, new UTF8Encoding(false), cancellation);
            File.SetAttributes(snapshot, File.GetAttributes(snapshot) | FileAttributes.ReadOnly);
            var baselineBytes = await File.ReadAllBytesAsync(baseline, cancellation);
            var temporarySave = Path.Combine(root, "game", "THOTH.sav.tas-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllBytesAsync(temporarySave, baselineBytes, cancellation);
                // Replacing the destination directory entry also breaks any pre-existing hard link.
                File.Move(temporarySave, Path.Combine(root, "game", "THOTH.sav"), true);
            }
            finally { if (File.Exists(temporarySave)) File.Delete(temporarySave); }
            var baselineHash = Convert.ToHexString(SHA256.HashData(baselineBytes));
            int port = GetAvailablePort();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            session.activeOptions = options;
            session.activePort = port;
            session.activeToken = token;
            var cfg = Path.Combine(session.RunDirectory, "session.cfg");
            var values = new Dictionary<string, string>
            {
                ["protocol"] = ProtocolVersion.ToString(CultureInfo.InvariantCulture), ["script"] = snapshot,
                ["run"] = session.RunDirectory, ["port"] = port.ToString(CultureInfo.InvariantCulture), ["token"] = token,
                ["width"] = options.Width.ToString(CultureInfo.InvariantCulture), ["height"] = options.Height.ToString(CultureInfo.InvariantCulture),
                ["autoplay"] = options.Autoplay ? "true" : "false", ["speed"] = options.Speed.ToString(CultureInfo.InvariantCulture),
                ["seed"] = session.Script.Seed.ToString(CultureInfo.InvariantCulture), ["start"] = session.Script.Start,
                ["communityModPath"] = options.CommunityMod ? CommunityModProfile.AssemblyPath(root) : ""
            };
            if (options.ReferenceFrames != null)
            {
                var reference = Path.GetFullPath(options.ReferenceFrames);
                if (!File.Exists(reference)) throw new FileNotFoundException("对照帧记录不存在。", reference);
                values["reference"] = reference;
            }
            await File.WriteAllLinesAsync(cfg, values.Select(kv => kv.Key + "=" + kv.Value), new UTF8Encoding(false), cancellation);
            session.protocolLog = new StreamWriter(Path.Combine(session.RunDirectory, "protocol.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
            session.commandLog = new StreamWriter(Path.Combine(session.RunDirectory, "commands.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
            var info = new ProcessStartInfo(session.gameExe) { WorkingDirectory = Path.GetDirectoryName(session.gameExe)!, UseShellExecute = false };
            foreach (var argument in new[] { "-screen-fullscreen", "0", "-screen-width", values["width"], "-screen-height", values["height"], "-logFile", Path.Combine(session.RunDirectory, "unity.log") }) info.ArgumentList.Add(argument);
            info.Environment["THOTH_TAS_SESSION"] = cfg;
            session.process = Process.Start(info) ?? throw new InvalidOperationException("无法启动独立游戏副本。");
            session.initialUnityLogPath = Path.Combine(session.RunDirectory, "unity.log");
            await File.WriteAllTextAsync(Path.Combine(session.RunDirectory, "process-start.json"), JsonSerializer.Serialize(new { pid = session.ProcessId, expectedExe = session.gameExe, startUtc = DateTime.UtcNow }, JsonOptions), cancellation);
            await session.VerifyOwnedPathAsync(cancellation);
            await File.WriteAllTextAsync(Path.Combine(session.RunDirectory, "host-run.json"), JsonSerializer.Serialize(new
            {
                protocol = ProtocolVersion, pid = session.ProcessId, exe = session.gameExe, coldProcess = true, initialUnityLogPath = session.initialUnityLogPath, baselineSha256 = baselineHash,
                scriptSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scriptText))),
                start = session.Script.Start, seed = session.Script.Seed, frames = session.Script.TotalFrames,
                communityModEnabled = options.CommunityMod, communityModVersion = options.CommunityMod ? CommunityModProfile.Version : null, communityModSha256 = options.CommunityMod ? CommunityModProfile.ExpectedSha256 : null,
                width = options.Width, height = options.Height, speed = options.Speed, startedUtc = DateTime.UtcNow,
                gameManifestSha256 = artifacts["docs/game-fingerprint.json"], runtimeSha256 = artifacts["game/BepInEx/plugins/ThothTAS.Runtime.dll"],
                preloaderSha256 = artifacts["game/BepInEx/patchers/ThothTAS.Preloader.dll"], bepInExConfigSha256 = artifacts["game/BepInEx/config/BepInEx.cfg"], artifacts
            }, JsonOptions), cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, session.lifetime.Token);
            timeout.CancelAfter(options.ConnectTimeout);
            await session.ConnectAsync(port, timeout.Token);
            session.reader = new StreamReader(session.client!.GetStream(), new UTF8Encoding(false));
            session.writer = new StreamWriter(session.client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            session.readerTask = session.ReadLoopAsync();
            await session.SendAsync($"HELLO\t{ProtocolVersion}\t{token}", timeout.Token);
            await session.hello.Task.WaitAsync(timeout.Token);
            return session;
        }
        catch (Exception ex)
        {
            ex.Data["runDirectory"] = session.RunDirectory;
            await session.DisposeAsync();
            if (ex is OperationCanceledException && !cancellation.IsCancellationRequested)
                throw new TimeoutException("游戏模块在连接时间内未完成协议握手。日志目录：" + session.RunDirectory, ex);
            throw;
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private void LockFingerprintFiles()
    {
        // Pin both the manifest and every listed artifact before hashing. Windows
        // disallows writes, replacement and deletion while these handles are open.
        // A warm reset can reuse that verified identity without reading 176 MB again.
        foreach (var name in new[] { "docs/game-fingerprint.json", "docs/game-fingerprint.tsv" })
            fingerprintLocks.Add(new FileStream(Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar)), FileMode.Open, FileAccess.Read, FileShare.Read));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "docs/game-fingerprint.json")));
        var game = Path.GetFullPath(Path.Combine(Root, "game")) + Path.DirectorySeparatorChar;
        foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString() ?? throw new InvalidDataException("指纹条目缺少 name。");
            pinnedFingerprintNames.Add(name.Replace('\\', '/'));
            var path = Path.GetFullPath(Path.Combine(game, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(game, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("指纹路径越界：" + name);
            fingerprintLocks.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
    }

    private void CheckPinnedFingerprint()
    {
        if (fingerprintLocks.Count < 7 || fingerprintLocks.Any(file => file.SafeFileHandle.IsClosed || file.SafeFileHandle.IsInvalid))
            throw new InvalidDataException("同进程重置的已校验版本文件锁失效，请重新启动游戏。");
        foreach (var path in Directory.EnumerateFiles(Path.Combine(Root, "game", "THOTH_Data"), "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            bool resource = name.StartsWith("level", StringComparison.OrdinalIgnoreCase) || name.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("resources", StringComparison.OrdinalIgnoreCase) || name.StartsWith("globalgamemanagers", StringComparison.OrdinalIgnoreCase) || name == "mainData";
            if (resource && !pinnedFingerprintNames.Contains("THOTH_Data/" + name))
                throw new InvalidDataException("发现未列入已锁定版本指纹的新资源，拒绝同进程重置：" + name);
        }
    }

    public async Task RestartAsync(string scriptText, SessionOptions? options = null, CancellationToken cancellation = default)
    {
        var resetWatch = Stopwatch.StartNew();
        options ??= activeOptions;
        if (options.Width != activeOptions.Width || options.Height != activeOptions.Height)
            throw new ArgumentException("同进程快速重置期间窗口尺寸必须保持启动时的值。", nameof(options));
        if (options.CommunityMod != activeOptions.CommunityMod)
            throw new ArgumentException("切换社区 Mod 必须重新启动游戏进程，不能同进程重置。", nameof(options));
        ValidateSpeed(options.Speed);
        var parsed = ProjectFiles.Parse(Root, scriptText);
        var parent = Path.GetFullPath(options.OutputParent ?? Path.Combine(Root, "work", "runs"));
        var workRoot = Path.GetFullPath(Path.Combine(Root, "work")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!parent.StartsWith(workRoot, StringComparison.OrdinalIgnoreCase) && !string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), workRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("同进程快速重置的输出目录必须位于本项目 work 中。", nameof(options));
        await restartGate.WaitAsync(cancellation);
        try
        {
            if (disposed || process == null || process.HasExited) throw new InvalidOperationException("当前副本进程已停止，不能同进程重置。");
            if (Completion.IsFaulted && Latest.Type != "error" && Latest.State != "error") throw new InvalidOperationException("当前控制连接有错误，请关闭快速重置后重新回放启动新进程。");
            await VerifyOwnedPathAsync(cancellation);
            await SendAsync("PAUSE", cancellation);
            await WaitForStatusAsync(s => s.State is "paused" or "complete" or "error", TimeSpan.FromSeconds(20), cancellation);
            ProjectFiles.EnsurePrivateGameCopy(Root);
            CheckPinnedFingerprint();
            var directory = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var snapshotPath = Path.Combine(directory, "script.tas");
            await File.WriteAllTextAsync(snapshotPath, scriptText, new UTF8Encoding(false), cancellation);
            File.SetAttributes(snapshotPath, File.GetAttributes(snapshotPath) | FileAttributes.ReadOnly);
            var baselineBytes = await File.ReadAllBytesAsync(Path.Combine(Root, "docs", "baseline", "THOTH.sav"), cancellation);
            var temporarySave = Path.Combine(Root, "game", "THOTH.sav.tas-" + Guid.NewGuid().ToString("N") + ".tmp");
            try { await File.WriteAllBytesAsync(temporarySave, baselineBytes, cancellation); File.Move(temporarySave, Path.Combine(Root, "game", "THOTH.sav"), true); }
            finally { if (File.Exists(temporarySave)) File.Delete(temporarySave); }
            var config = Path.Combine(directory, "session.cfg");
            var values = new Dictionary<string, string>
            {
                ["protocol"] = "1", ["script"] = snapshotPath, ["run"] = directory,
                ["port"] = activePort.ToString(CultureInfo.InvariantCulture), ["token"] = activeToken,
                ["width"] = activeOptions.Width.ToString(CultureInfo.InvariantCulture), ["height"] = activeOptions.Height.ToString(CultureInfo.InvariantCulture),
                ["autoplay"] = "false", ["speed"] = options.Speed.ToString(CultureInfo.InvariantCulture),
                ["seed"] = parsed.Seed.ToString(CultureInfo.InvariantCulture), ["start"] = parsed.Start,
                ["communityModPath"] = options.CommunityMod ? CommunityModProfile.AssemblyPath(Root) : ""
            };
            if (options.ReferenceFrames != null)
            {
                var reference = Path.GetFullPath(options.ReferenceFrames);
                if (!File.Exists(reference)) throw new FileNotFoundException("对照帧记录不存在。", reference);
                values["reference"] = reference;
            }
            await File.WriteAllLinesAsync(config, values.Select(kv => kv.Key + "=" + kv.Value), new UTF8Encoding(false), cancellation);
            var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in new[] { "docs/game-fingerprint.json", "game/BepInEx/plugins/ThothTAS.Runtime.dll", "game/BepInEx/patchers/ThothTAS.Preloader.dll", "game/BepInEx/config/BepInEx.cfg", "game/doorstop_config.ini", "game/winhttp.dll" })
            {
                var path = Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) { if (name.EndsWith("doorstop_config.ini") || name.EndsWith("winhttp.dll")) continue; throw new FileNotFoundException("缺少固定 TAS 模块或配置：" + name, path); }
                await using var artifact = File.OpenRead(path);
                artifacts[name] = Convert.ToHexString(await SHA256.HashDataAsync(artifact, cancellation));
            }
            var previousRun = RunDirectory;
            await File.WriteAllTextAsync(Path.Combine(directory, "host-run.json"), JsonSerializer.Serialize(new
            {
                protocol = ProtocolVersion, pid = ProcessId, exe = gameExe, coldProcess = false, reusedPid = ProcessId, sameProcessReset = true, coldEquivalent = false, fingerprintPolicy = "startup-sha256-held-read-locks", initialUnityLogPath, previousRun,
                baselineSha256 = Convert.ToHexString(SHA256.HashData(baselineBytes)), scriptSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scriptText))),
                start = parsed.Start, seed = parsed.Seed, frames = parsed.TotalFrames, width = options.Width, height = options.Height, speed = options.Speed, startedUtc = DateTime.UtcNow,
                communityModEnabled = options.CommunityMod, communityModVersion = options.CommunityMod ? CommunityModProfile.Version : null, communityModSha256 = options.CommunityMod ? CommunityModProfile.ExpectedSha256 : null,
                gameManifestSha256 = artifacts["docs/game-fingerprint.json"], runtimeSha256 = artifacts["game/BepInEx/plugins/ThothTAS.Runtime.dll"],
                preloaderSha256 = artifacts["game/BepInEx/patchers/ThothTAS.Preloader.dll"], bepInExConfigSha256 = artifacts["game/BepInEx/config/BepInEx.cfg"], artifacts
            }, JsonOptions), cancellation);
            await logGate.WaitAsync(cancellation);
            try
            {
                if (protocolLog != null) await protocolLog.DisposeAsync();
                if (commandLog != null) await commandLog.DisposeAsync();
                protocolLog = new StreamWriter(Path.Combine(directory, "protocol.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
                commandLog = new StreamWriter(Path.Combine(directory, "commands.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
                lock (stateGate)
                {
                    var oldFinished = finished;
                    finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    RunDirectory = directory;
                    Script = parsed;
                    Latest = new() { Run = directory, State = "loading", Frame = -1, Protocol = ProtocolVersion };
                    var oldChanged = stateChanged; stateChanged = NewSignal(); oldChanged.TrySetResult();
                    oldFinished.TrySetCanceled();
                }
            }
            finally { logGate.Release(); }
            await SendAsync("RESTART\t" + config, cancellation);
            var preparedMilliseconds = resetWatch.Elapsed.TotalMilliseconds;
            await WaitForStatusAsync(s => s.Run == directory && s.Frame >= 0 && s.State == "paused", TimeSpan.FromSeconds(90), cancellation);
            await File.WriteAllTextAsync(Path.Combine(directory, "reset-performance.json"), JsonSerializer.Serialize(new { version = 1, pid = ProcessId, totalMilliseconds = resetWatch.Elapsed.TotalMilliseconds, preparedMilliseconds, fingerprintPolicy = "startup-sha256-held-read-locks", coldEquivalent = false }, JsonOptions), cancellation);
            activeOptions = options;
        }
        catch (Exception ex) { ex.Data["runDirectory"] = RunDirectory; throw; }
        finally { restartGate.Release(); }
    }

    private async Task VerifyOwnedPathAsync(CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 25; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (process!.HasExited) throw new InvalidOperationException("独立副本启动后已退出；请检查运行目录中的 unity.log。");
            try
            {
                var actual = GetExecutablePath(process);
                if (actual != null)
                {
                    if (!string.Equals(Path.GetFullPath(actual), gameExe, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"启动进程路径错误：{actual}，期望 {gameExe}。");
                    return;
                }
            }
            catch (System.ComponentModel.Win32Exception) when (attempt < 24) { }
            await Task.Delay(50, cancellation);
        }
        throw new InvalidOperationException("无法核对独立副本实际进程路径，拒绝继续。");
    }

    private static string GetExecutablePath(Process ownedProcess)
    {
        var buffer = new StringBuilder(32768);
        int size = buffer.Capacity;
        if (!QueryFullProcessImageName(ownedProcess.Handle, 0, buffer, ref size))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "QueryFullProcessImageName 无法读取启动进程路径。");
        return buffer.ToString();
    }

    private async Task ConnectAsync(int port, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (process!.HasExited) throw new InvalidOperationException("游戏在协议连接前退出。日志：" + RunDirectory);
            var candidate = new TcpClient { NoDelay = true };
            try
            {
                await candidate.ConnectAsync(IPAddress.Loopback, port, cancellation);
                client = candidate;
                return;
            }
            catch (SocketException) { candidate.Dispose(); await Task.Delay(100, cancellation); }
            catch { candidate.Dispose(); throw; }
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var line = await reader!.ReadLineAsync(lifetime.Token);
                if (line == null) throw new IOException("本地协议连接已断开；游戏模块应在帧边界暂停。");
                await logGate.WaitAsync(lifetime.Token);
                try { await protocolLog!.WriteLineAsync(line); }
                finally { logGate.Release(); }
                var status = JsonSerializer.Deserialize<GameStatus>(line, JsonOptions) ?? throw new InvalidDataException("协议返回空消息。");
                if (status.Protocol != ProtocolVersion) throw new InvalidDataException("游戏模块协议版本不匹配。");
                if (status.Run.Length != 0 && !string.Equals(status.Run, RunDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                lock (stateGate)
                {
                    Latest = status;
                    var old = stateChanged;
                    stateChanged = NewSignal();
                    old.TrySetResult();
                }
                if (StatusChanged != null)
                    foreach (Action<GameStatus> handler in StatusChanged.GetInvocationList())
                        try { handler(status); } catch (Exception ex) { Debug.WriteLine("状态观察者错误：" + ex.Message); }
                if (status.Type == "hello") hello.TrySetResult();
                if (status.Type == "error" || status.State == "error")
                {
                    var error = new InvalidOperationException(status.Error ?? "游戏模块报告错误。");
                    hello.TrySetException(error);
                    finished.TrySetException(error);
                }
                else if (status.State == "complete") finished.TrySetResult(status);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            hello.TrySetException(ex);
            finished.TrySetException(ex);
            lock (stateGate) stateChanged.TrySetException(ex);
        }
    }

    public async Task SendAsync(string command, CancellationToken cancellation = default)
    {
        if (disposed || writer == null) throw new InvalidOperationException("游戏会话未连接。");
        if (command.Contains('\n') || command.Contains('\r')) throw new ArgumentException("协议命令须单行。", nameof(command));
        await sendGate.WaitAsync(cancellation);
        try
        {
            var safeCommand = command.StartsWith("HELLO\t", StringComparison.Ordinal) ? "HELLO\t1\t<redacted>" : command;
            await logGate.WaitAsync(cancellation);
            try { await commandLog!.WriteLineAsync(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, frame = Latest.Frame, command = safeCommand })); }
            finally { logGate.Release(); }
            await writer.WriteLineAsync(command.AsMemory(), cancellation);
        }
        finally { sendGate.Release(); }
    }

    public Task SetSpeedAsync(double speed, CancellationToken cancellation = default)
    {
        ValidateSpeed(speed);
        return SendAsync("SPEED\t" + speed.ToString(CultureInfo.InvariantCulture), cancellation);
    }

    public static void ValidateSpeed(double speed)
    {
        if (speed is not (0.5 or 1 or 2)) throw new ArgumentOutOfRangeException(nameof(speed), "播放速度只支持 0.5×、1×、2×。");
    }

    public async Task<GameStatus> WaitForStatusAsync(Func<GameStatus, bool> predicate, TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        limit.CancelAfter(timeout);
        while (true)
        {
            Task changed;
            lock (stateGate)
            {
                if (predicate(Latest)) return Latest;
                if (Latest.Type == "error" || Latest.State == "error") throw new InvalidOperationException(Latest.Error ?? "模块错误。");
                changed = stateChanged.Task;
            }
            await changed.WaitAsync(limit.Token);
        }
    }

    public async Task<GameStatus> StepAsync(CancellationToken cancellation = default)
    {
        var before = Latest.Frame;
        await SendAsync("STEP", cancellation);
        return await WaitForStatusAsync(s => s.Frame > before || s.State == "complete", TimeSpan.FromSeconds(15), cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        if (writer != null)
        {
            try { await SendAsync("STOP"); } catch (Exception) { }
        }
        disposed = true;
        lifetime.Cancel();
        client?.Dispose();
        if (readerTask != null) { try { await readerTask; } catch (Exception) { } }
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    using var exitLimit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await process.WaitForExitAsync(exitLimit.Token); }
                    catch (OperationCanceledException)
                    {
                        // The handle is ours, and the executable path must still match.
                        var actual = GetExecutablePath(process);
                        if (actual != null && string.Equals(Path.GetFullPath(actual), gameExe, StringComparison.OrdinalIgnoreCase))
                        { process.Kill(false); await process.WaitForExitAsync(); }
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine("会话清理：" + ex.Message); }
            process.Dispose();
        }
        reader?.Dispose();
        writer?.Dispose();
        if (protocolLog != null) await protocolLog.DisposeAsync();
        if (commandLog != null) await commandLog.DisposeAsync();
        instanceLock?.Dispose();
        foreach (var file in fingerprintLocks) file.Dispose();
        fingerprintLocks.Clear();
        pinnedFingerprintNames.Clear();
        lifetime.Dispose();
    }
}
