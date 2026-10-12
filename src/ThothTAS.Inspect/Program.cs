using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;
using TypeDefinition = Mono.Cecil.TypeDefinition;
using TypeReference = Mono.Cecil.TypeReference;

// No game assembly is loaded by the CLR, and no target code is executed.
// Cecil decodes the supplied immutable assembly files as data.
bool resetOnly = args.Contains("--reset-only", StringComparer.Ordinal);
args = args.Where(x => x != "--reset-only").ToArray();
var project = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var managed = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(project, "game", "THOTH_Data", "Managed");
var output = args.Length > 2 ? Path.GetFullPath(args[2]) : Path.Combine(project, "docs");
Directory.CreateDirectory(output);
if (resetOnly) return ResetInspection.Run(managed, output);
string[] names = ["Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "Assembly-UnityScript-firstpass.dll"];
var assemblies = new List<AssemblyIdentity>();
var sites = new List<RandomSite>();
var audit = new List<AuditSite>();
var bodyErrors = new List<string>();
var audioAudit = new List<AuditSite>();
var cil = new StringBuilder("THOTH TAS 原始 CIL 静态证据\n工具：ThothTAS.Inspect / Mono.Cecil 0.11.6\n边界：只读解析三个程序集，不加载或执行游戏代码。IL token 只在记录的 SHA256 / MVID 内有意义。\n\n");
var audioCil = new StringBuilder("THOTH TAS 音频玩法原始 CIL 静态证据\n范围：BeatMaster / LaserBeat / RhythmLaser 全部方法，未运行目标代码。\n资源 AudioSource 与 AudioClip 的关联/loop/pitch/PlayOnAwake 见 scene-geometry.json 中 LaserBeat.decodedPublicFields.laserBeatSound。\n\n");
foreach (var name in names)
{
    string path = Path.Combine(managed, name);
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadSymbols = false, ReadingMode = ReadingMode.Deferred });
    string assemblySha = HashFile(path);
    var identity = new AssemblyIdentity(name, assemblySha, module.Mvid.ToString(), module.RuntimeVersion, module.Architecture.ToString(), ReaEvidence(assemblySha));
    assemblies.Add(identity);
    cil.AppendLine($"ASSEMBLY {name}\nSHA256 {identity.Sha256}\nMVID {identity.Mvid}\nREA_IDENTITY_EVIDENCE {identity.ReaIdentityEvidenceId ?? "未知：此 SHA256 未绑定当前调查 Evidence"}\n");
    audioCil.AppendLine($"ASSEMBLY {name}\nSHA256 {identity.Sha256}\nMVID {identity.Mvid}\nREA_IDENTITY_EVIDENCE {identity.ReaIdentityEvidenceId ?? "unknown"}\n");
    foreach (var type in AllTypes(module.Types))
    {
    if (type.FullName is "Globals" or "Room")
    {
        cil.AppendLine($"TYPE_FIELDS {type.FullName} [{Token(type)}] — metadata declaration order, custom resource layout must also follow Unity serialization rules");
        foreach (var field in type.Fields)
            cil.AppendLine($"  {Token(field)} {field.FieldType.FullName} {field.Name}; static={field.IsStatic} public={field.IsPublic} attributes=[{string.Join(",", field.CustomAttributes.Select(a => a.AttributeType.FullName))}]");
        cil.AppendLine();
    }
    foreach (var method in type.Methods)
    {
        if (!method.HasBody) continue;
        try
        {
            var ins = method.Body.Instructions;
            var bytes = pe.GetMethodBody((int)method.RVA).GetILBytes() ?? [];
            string rawSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            bool focus = Focus(type.FullName, method.Name);
            bool audioFocus = type.FullName is "BeatMaster" or "LaserBeat" or "RhythmLaser";
            if (focus || audioFocus)
            {
                var bodyText = new StringBuilder($"METHOD {method.FullName}\nTOKEN {Token(method)} RVA 0x{method.RVA:x8} RAW_IL_SHA256 {rawSha}\nMaxStack {method.Body.MaxStackSize} InitLocals {method.Body.InitLocals}\n");
                foreach (var v in method.Body.Variables) bodyText.AppendLine($"  LOCAL V_{v.Index}: {v.VariableType.FullName}");
                foreach (var eh in method.Body.ExceptionHandlers) bodyText.AppendLine($"  EH {eh.HandlerType} try {Label(eh.TryStart)}..{Label(eh.TryEnd)} handler {Label(eh.HandlerStart)}..{Label(eh.HandlerEnd)} filter {Label(eh.FilterStart)} catch {eh.CatchType?.FullName}");
                foreach (var item in ins) bodyText.AppendLine("  " + Line(item));
                bodyText.AppendLine();
                if (focus) cil.Append(bodyText);
                if (audioFocus) audioCil.Append(bodyText);
            }
            for (int i = 0; i < ins.Count; i++)
            {
                var item = ins[i];
                if (item.Operand is not MethodReference target) continue;
                bool direct = item.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj;
                bool randomRef = target.DeclaringType.FullName == "UnityEngine.Random";
                string id = $"{Path.GetFileNameWithoutExtension(name)}:{Token(method)}:IL_{item.Offset:x4}";
                if (audioFocus && target.DeclaringType.FullName is "UnityEngine.AudioSource" or "UnityEngine.AudioClip")
                    audioAudit.Add(new AuditSite("audio-gameplay-reference", id, name, type.FullName, method.FullName, Token(method), Label(item), item.OpCode.Name, target.FullName, ins.Skip(Math.Max(0, i - 4)).Take(9).Select(Line).ToArray()));
                if (randomRef && direct)
                {
                    var nearbyWrites = ins.Skip(i + 1).Take(16)
                        .Where(x => x.OpCode.Code is Code.Stfld or Code.Stsfld)
                        .Select(x => new NearbyFieldWrite(Label(x), ((FieldReference)x.Operand).FullName)).ToList();
                    sites.Add(new RandomSite(id, name, identity.Sha256, identity.Mvid, type.FullName, method.FullName,
                        Token(method), rawSha, item.Offset, Label(item), item.OpCode.Name, target.FullName,
                        Token(target), ins.Skip(Math.Max(0, i - 14)).Take(i - Math.Max(0, i - 14)).Select(Line).ToArray(),
                        ins.Skip(i + 1).Take(16).Select(Line).ToArray(), nearbyWrites,
                        "未知：此清单保留静态参数构造 CIL，实际参数和结果必须从实际回放日志读取。",
                        AffectedFields(type.FullName, method.Name, target.Name, item.Offset), null, null));
                }
                string? category = Classify(target, item.OpCode.Code);
                if (category != null)
                    audit.Add(new AuditSite(category, id, name, type.FullName, method.FullName, Token(method), Label(item), item.OpCode.Name, target.FullName,
                        ins.Skip(Math.Max(0, i - 4)).Take(9).Select(Line).ToArray()));
            }
        }
        catch (Exception ex)
        {
            bodyErrors.Add($"{name} {method.FullName}: {ex.GetType().Name}: {ex.Message}");
        }
    }
    }
}
var result = new
{
    schemaVersion = 1,
    evidenceKind = "static-cil-scan",
    scanner = "ThothTAS.Inspect / Mono.Cecil 0.11.6",
    observedRuntimeExecutions = 0,
    coverage = new { assemblies = names, methodBodyErrors = bodyErrors, directCallOnly = true },
    limitations = new[]
    {
        "静态调用点不证明该路径已经执行。runtimeParameters / runtimeResult 为 null，不代填推测值。",
        "siteId 用程序集名 + MethodDef token + IL offset，需结合 assemblySha256 / mvid；更换 build 后必须重新扫描。",
        "nearbyFieldWrites 只是后续 16 条指令中的写字段候选，未经数据流分析，不能作为受随机值影响的证明。",
        "不覆盖引擎原生随机源、反射/动态加载的未知代码、Unity 内部行为或非游戏三个程序集。",
        "ldftn / ldvirtftn 的随机引用单独记入 audit；不计为直接随机调用点。"
    },
    assemblies,
    randomCallCounts = sites.GroupBy(x => x.Assembly).ToDictionary(g => g.Key, g => g.Count()),
    totalRandomDirectCalls = sites.Count,
    randomCalls = sites,
    audit
};
var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
File.WriteAllText(Path.Combine(output, "random-calls.json"), JsonSerializer.Serialize(result, options) + "\n", new UTF8Encoding(false));
File.WriteAllText(Path.Combine(output, "cil-evidence.txt"), cil.ToString(), new UTF8Encoding(false));
audioCil.AppendLine("DIRECT_AUDIO_API_COUNTS — bounded to these three classes; zero does not establish absence elsewhere:");
foreach (var api in new[] { "Play", "Stop", "PlayDelayed", "PlayScheduled", "get_time", "set_time", "get_timeSamples", "set_timeSamples", "get_isPlaying", "get_loop", "set_loop", "get_pitch", "set_pitch", "get_clip", "set_clip", "get_length" })
    audioCil.AppendLine($"  {api}: {audioAudit.Count(a => a.Api.Contains("::" + api + "("))}");
audioCil.AppendLine("\nDIRECT_AUDIO_CALLS:");
audioCil.AppendLine(JsonSerializer.Serialize(audioAudit, options));
File.WriteAllText(Path.Combine(output, "cil-audio-evidence.txt"), audioCil.ToString(), new UTF8Encoding(false));
File.WriteAllText(Path.Combine(output, "random-calls.md"), Markdown(assemblies, sites, audit, bodyErrors), new UTF8Encoding(false));
Console.WriteLine($"静态扫描完成：{sites.Count} 个 UnityEngine.Random 直接调用，{audit.Count} 个其他审计调用点，{bodyErrors.Count} 个解析错误。");
foreach (var a in assemblies) Console.WriteLine($"{a.Name}: {sites.Count(s => s.Assembly == a.Name)} / {a.Sha256} / {a.Mvid}");
return bodyErrors.Count == 0 ? 0 : 2;

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> source)
{
    foreach (var t in source) { yield return t; foreach (var child in AllTypes(t.NestedTypes)) yield return child; }
}
static string HashFile(string path) { using var s = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(s)); }
static string Token(IMetadataTokenProvider t) => $"0x{t.MetadataToken.ToUInt32():x8}";
static string Label(Instruction? i) => i == null ? "<end>" : $"IL_{i.Offset:x4}";
static string Line(Instruction i) => $"{Label(i)}: {i.OpCode.Name} {Operand(i.Operand)}".TrimEnd();
static string Operand(object? o) => o switch
{
    null => "", Instruction i => Label(i), Instruction[] a => string.Join(", ", a.Select(Label)),
    MethodReference m => $"{m.FullName} [{Token(m)}]", FieldReference f => $"{f.FullName} [{Token(f)}]",
    TypeReference t => $"{t.FullName} [{Token(t)}]", VariableDefinition v => $"V_{v.Index}",
    ParameterDefinition p => $"arg_{p.Index} ({p.Name})", string s => JsonSerializer.Serialize(s),
    float f => f.ToString("R", CultureInfo.InvariantCulture), double d => d.ToString("R", CultureInfo.InvariantCulture),
    _ => Convert.ToString(o, CultureInfo.InvariantCulture) ?? ""
};
static bool Focus(string type, string method) =>
    type == "PlayerInput" || type == "Player" && method is "Awake" or "Start" or "Update" or "UpdateMovement" ||
    type == "PlayerAim" && method is "Awake" or "Start" or "Update" or "UpdateAim" or "SpawnBullet" or "SpawnNormal" ||
    type == "Room" ||
    type == "Teleporter" && method is "OnCollisionStay" or "Start" or "LateStart" or "Update" or "OnActivateTeleporter" or "OnOtherReceiverOn" || type == "Bouncer" && method == "SetRandomSpeedScale" ||
    type == "SimpleLauncher" && method is "Awake" or "Start" or "FixedUpdate" ||
    type == "NewMenu" && method == "UpdateChooseLevel" ||
    type is "Globals" or "SaveLoad" or "SaveDataHelper" ||
    type == "SteamManager" && method is "UnlockAchievement" or "Update" ||
    type == "InControl.InputManager" && method is "OnApplicationFocus" or "UpdateInternal" ||
    type == "InControl.InControlManager" && method is "OnApplicationFocus" or "Update";
static string? ReaEvidence(string sha) => sha switch
{
    "9675a94cce19c883f0e0927e9a9185ff8c793080698100a8717ed5aaa4b2555c" => "ev_32d819f034c1c5f31cbac0c8471ab95c617396375edc5ee4623818bd66d05121",
    "fe69544f7b80cfd3a4f7155888cbf83394424cb61916fe025daeee5dfdb29bb3" => "ev_c25b569800e0f182ca9afa4f5224af1c9954f706740a38723856b83c728bad8c",
    "f93acb5a43f6a7f5e7e3bbfd104d44b872e106409d64842120c746e66596a484" => "ev_a0d7623f5ed2d51ee0f630767c88774b2dfc4aa496f2ced4358278d1da4748db",
    _ => null
};
static string? Classify(MethodReference m, Code code)
{
    string t = m.DeclaringType.FullName, n = m.Name;
    if (t == "System.Random") return "system-random";
    if (t == "UnityEngine.Random" && (n.Contains("seed", StringComparison.OrdinalIgnoreCase) || n.Contains("state", StringComparison.OrdinalIgnoreCase))) return "unity-random-seed-or-state";
    if (t == "UnityEngine.Random" && code is Code.Ldftn or Code.Ldvirtftn) return "unity-random-function-reference";
    if (t == "UnityEngine.Time") return "unity-time";
    if (t is "System.DateTime" or "System.Diagnostics.Stopwatch" || t == "System.Environment" && n is "get_TickCount" or "get_TickCount64" || t == "System.Guid" && n == "NewGuid") return "clock-or-external-entropy";
    if (t.StartsWith("System.Security.Cryptography", StringComparison.Ordinal) && (t.Contains("Random") || n.Contains("Random"))) return "crypto-random";
    if (t.StartsWith("System.Threading", StringComparison.Ordinal) || t.StartsWith("System.Threading.Tasks", StringComparison.Ordinal)) return "thread-or-task";
    if (t.StartsWith("System.Reflection", StringComparison.Ordinal) || t == "System.Activator" || t == "System.Type" && n is "GetType" or "GetMethod" or "GetMethods" or "GetField" or "GetFields" || t == "UnityEngine.Component" && n is "SendMessage" or "BroadcastMessage") return "reflection-or-indirect-dispatch";
    if (t == "UnityEngine.Input") return "unity-input-direct";
    if (t.StartsWith("Steamworks", StringComparison.Ordinal) && n is "SetAchievement" or "ClearAchievement" or "StoreStats" or "SetStat" or "ResetAllStats" or "IndicateAchievementProgress" or "FileWrite" or "FileDelete") return "steam-write";
    if (t == "UnityEngine.Screen" && n == "SetResolution") return "screen-resolution";
    if (t == "UnityEngine.Application" && n is "set_runInBackground" or "set_targetFrameRate") return "application-pacing-or-focus";
    if (t == "UnityEngine.QualitySettings" && n == "set_vSyncCount") return "vsync";
    if (t == "UnityEngine.PlayerPrefs" || t == "System.IO.File" && n is "WriteAllText" or "WriteAllLines" or "WriteAllBytes" or "Delete") return "save-or-file-write";
    return null;
}
static string AffectedFields(string t, string m, string api, int offset)
{
    if (t == "Bouncer" && m == "SetRandomSpeedScale") return "静态 CIL 核对：随机 Range 结果写入 Bouncer.speedScale；调用结果仍需运行日志。";
    if (t == "Teleporter" && m == "OnCollisionStay" && offset == 0x9e) return "静态 CIL 核对：Range(-0.2,0.2) * 0.95 加到 Teleporter.velocity.x；运行结果未知。";
    if (t == "Teleporter" && m == "OnCollisionStay" && offset == 0xc5) return "静态 CIL 核对：Range(-0.2,0.2) * 0.95 加到 Teleporter.velocity.y；运行结果未知。";
    if (t == "Teleporter" && m == "OnCollisionStay" && offset == 0xe1) return "静态 CIL 核对：Range(0.9,1.1) 写入 Teleporter.speedScale；运行结果未知。";
    return "未知：需结合完整 CIL 数据流或运行观察核实；nearbyFieldWrites 仅为邻近写入候选。";
}
static string Markdown(List<AssemblyIdentity> assemblies, List<RandomSite> sites, List<AuditSite> audit, List<string> errors)
{
    var md = new StringBuilder("# 随机调用及确定性静态审计\n\n此报告来自游戏副本的三个原始程序集，只读解析 CIL，没有运行游戏。调用点存在不代表路径已经执行。实际参数、返回值和动态次序由每次回放日志提供，JSON 中相关运行值均为 `null`。\n\n重跑：\n\n```powershell\ndotnet run --project .\\src\\ThothTAS.Inspect -- D:\\Workspace4All\\ThothTAS\n```\n\n## 程序集绑定\n\n| 文件 | SHA256 | MVID | 随机直接调用 |\n|---|---|---|---:|\n");
    foreach (var a in assemblies) md.AppendLine($"| `{a.Name}` | `{a.Sha256}` | `{a.Mvid}` | {sites.Count(s => s.Assembly == a.Name)} |");
    md.AppendLine("\nREA `inspect_managed_artifact` 独立核对了下面的程序集身份，完整 Evidence 保存在调查包（该扫描器不执行 REA）：\n");
    foreach (var a in assemblies) md.AppendLine($"- `{a.Name}`：`{a.ReaIdentityEvidenceId ?? "unknown"}`，只绑定上表 SHA256。\n");
    md.AppendLine($"\n合计 **{sites.Count}** 个直接调用点。MethodDef token 与 IL offset 为当前 build 的定位坐标；必须同时匹配 SHA256 / MVID。\n\n## 审计边界\n");
    foreach (var category in new[] { "system-random", "unity-random-seed-or-state", "unity-random-function-reference", "crypto-random", "clock-or-external-entropy", "thread-or-task", "reflection-or-indirect-dispatch", "unity-time", "unity-input-direct", "steam-write", "screen-resolution", "application-pacing-or-focus", "vsync", "save-or-file-write" })
        md.AppendLine($"- `{category}`：{audit.Count(x => x.Category == category)} 个静态调用/引用点；详情在 JSON `audit`。零表示仅在这三个程序集及此分类匹配规则内没有发现，不表示整个 Unity 引擎没有此行为。");
    md.AppendLine("\n`nearbyFieldWrites` 只记录每个随机调用之后 16 条指令里的 `stfld` / `stsfld`，不是数据流证明。完整参数构造与结果使用需看 `cil-evidence.txt`，实际结果必须看运行日志。未覆盖原生引擎、动态加载、反射目标和非三个游戏程序集。\n\n## 已核对的静态关系\n\n- `Bouncer.SetRandomSpeedScale`：Range(0.9,1.1) 结果直接写 `speedScale`，IL_000b。\n- `Teleporter.OnCollisionStay`：IL_009e / IL_00c5 的 Range(-0.2,0.2) 经 *0.95 加到 `velocity.x/y`，IL_00e1 的 Range(0.9,1.1) 写 `speedScale`。碰撞和 timeSinceImpact 等条件控制是否执行。\n- `SimpleLauncher.Start` 有 5 个 SetResolution(Int32,Int32,Boolean) 调用，IL_01a1 / IL_01d0 / IL_0222 / IL_0251 / IL_0280，原第三参数为 true。\n- `PlayerAim.UpdateAim` 保留平方死区 0.16000001。`SpawnBullet` 的鼠标分支会从 PlayerCursor.position 重新计算方向；TAS 要控制 keyboardControlEnabled / cursorActive。\n- `SteamManager.UnlockAchievement` 的 IL_0022 调用 SetAchievement；`SteamManager.Update` 的 IL_0016 调用 StoreStats。\n- `InControl.InputManager.OnApplicationFocus(false)` 在 SuspendInBackground=false 时仍然 SetZeroTickOnAllControls。\n- 本次仅发现 Range(Float,Float)、Range(Int32,Int32)、get_value 三类直接 Unity 随机 API。种子/state/System.Random 的零发现只适用于此三个程序集。\n\n## 原始 CIL 锚点\n\n- `PlayerInput` 所有方法；`Player` Awake / Start / Update / UpdateMovement。\n- `PlayerAim` Awake / Start / Update / UpdateAim / SpawnBullet。\n- `Room` 所有方法，含 Awake / Start / FixedUpdate / Update / UpdateSliceOut 自然转场和激活逻辑。\n- `Bouncer.SetRandomSpeedScale`、`Teleporter.OnCollisionStay`。\n- `SimpleLauncher` Awake / Start / FixedUpdate、`NewMenu.UpdateChooseLevel`、`Globals`、`SaveLoad`、`SaveDataHelper`、Steam 成就、InControl 的焦点行为。\n\n每个方法保存 token、RVA、原始 IL 字节 SHA256、local 类型、异常区间和完整 opcode / operand。它是 CIL 的解码文本，不是恢复的原源码。\n\n## 随机调用清单\n\n| siteId | 方法 | IL | API |\n|---|---|---|---|\n");
    foreach (var s in sites) md.AppendLine($"| `{s.SiteId}` | `{s.Method}` | `{s.IlLabel}` | `{s.Api}` |");
    md.AppendLine($"\n方法体解析错误：{errors.Count}。\n");
    foreach (var error in errors) md.AppendLine("- " + error);
    return md.ToString();
}
record AssemblyIdentity(string Name, string Sha256, string Mvid, string RuntimeVersion, string Architecture, string? ReaIdentityEvidenceId);
record NearbyFieldWrite(string Il, string Field);
record RandomSite(string SiteId, string Assembly, string AssemblySha256, string Mvid, string Type, string Method, string MethodToken, string MethodRawIlSha256, int IlOffset, string IlLabel, string Opcode, string Api, string ApiToken, string[] ArgumentConstructionCil, string[] FollowingCil, List<NearbyFieldWrite> NearbyFieldWrites, string ParametersStatus, string AffectedFieldsStatus, object? RuntimeParameters, object? RuntimeResult);
record AuditSite(string Category, string SiteId, string Assembly, string Type, string Method, string MethodToken, string Il, string Opcode, string Api, string[] CilContext);
