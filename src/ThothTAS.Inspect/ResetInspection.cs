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

// Read CIL and declarations as data. This does not load game code into the CLR,
// create a Unity process, evaluate field initializers, or change any game file.
internal static class ResetInspection
{
    private static readonly HashSet<string> FullTypes = new(StringComparer.Ordinal)
    {
        "Globals", "Room", "Player", "Receiver", "NewMenu", "SoundBankLoader", "LoadSoundBank",
        "RoomMusic", "MusicTrack", "BossMusic", "BossTrack", "ProceduralMusic", "GeneralSoundBank",
        "Bullet", "BulletPool", "WallImpactPool", "ReceiverTrailsGlobalAudioList", "PlayerAudio",
        "SeekerSphere", "Teleporter", "DeathSwipeManager", "HardcoreArena", "LaserBeat", "BeatMaster",
        "BeatListener", "LoadSoundFolder", "GlobalMeshesBank", "GenerateNoiseMeshes", "TilingTextureBank",
        "StarsBGScroller", "LauncherSound", "SimpleLauncher", "SaveLoad", "SaveDataHelper", "GlobalAudioInterface",
        "InControl.InControlManager", "InControl.InputManager", "InControl.TouchManager",
        "InControl.SingletonMonoBehavior`2", "InControl.KeyCombo", "InControl.InputDeviceProfile",
        "InControl.Native", "InControl.NativeInputDeviceManager", "InControl.XInputDeviceManager",
        "BouncerDeathEffect", "BouncerDeathEffect2", "BouncerLink", "LineToggler", "RhythmLaser",
        "RoomNumberExtra", "RorschachPattern", "SeekerSphereLink", "ShadowBouncer", "LaserGfx",
        "Quads", "Triangles", "PostEffectsBase", "BlurEffect", "NoiseEffect", "NoiseAndGrain", "CameraMotionBlur",
        "ContrastStretchEffect", "GlowEffect", "ImageEffectBase", "MotionBlur", "SSAOEffect",
        "ColorCorrectionCurves", "ColorCorrectionLut", "Tonemapping",
        "BigBoss", "BossBouncer", "Bouncer", "Killer", "HitBox"
    };
    public static int Run(string managed, string output)
    {
        string[] names = ["Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "Assembly-UnityScript-firstpass.dll"];
        var assemblies = new List<object>();
        var fields = new List<object>();
        var writers = new List<object>();
        var lifetimeCalls = new List<object>();
        var references = new List<object>();
        var resourceReferences = new List<object>();
        var resourceInstrumentation = new List<object>();
        var errors = new List<string>();
        var cil = new StringBuilder("THOTH TAS 同进程重置静态 CIL 证据\n只读三原始程序集；未加载或执行目标代码，字段值仅元数据/初始化CIL，未读取运行内存。\n所有 .cctor + 重点生命周期类全文；包括所有直接 stsfld、生命周期/API/事件引用的独立清单。\n\n");
        foreach (string name in names)
        {
            string path = Path.Combine(managed, name);
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadSymbols = false });
            string sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            string mvid = module.Mvid.ToString();
            assemblies.Add(new { name, sha256 = sha, mvid });
            cil.AppendLine($"ASSEMBLY {name}\nSHA256 {sha}\nMVID {mvid}\n");
            foreach (TypeDefinition type in Types(module.Types))
            {
                var staticEvents = type.Events.Where(e => e.AddMethod?.IsStatic == true || e.RemoveMethod?.IsStatic == true)
                    .Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var field in type.Fields.Where(f => f.IsStatic))
                    fields.Add(new { assembly = name, assemblySha256 = sha, mvid, type = type.FullName,
                        token = Token(field), fullName = field.FullName, name = field.Name, fieldType = field.FieldType.FullName,
                        isLiteral = field.IsLiteral, isInitOnly = field.IsInitOnly, isPublic = field.IsPublic,
                        hasConstant = field.HasConstant, constant = field.HasConstant ? field.Constant : null,
                        isEventBackingField = staticEvents.Contains(field.Name),
                        resetInterpretation = field.IsLiteral ? "常量，不应重置" : field.IsInitOnly
                            ? "readonly/初始化缓存，不能当普通可写运行状态" : "需按生命周期和运行证据决定，不建议一概清零" });
                bool fullType = FullTypes.Contains(type.FullName) ||
                    type.DeclaringType != null && FullTypes.Contains(type.DeclaringType.FullName);
                if (fullType)
                {
                    cil.AppendLine($"TYPE {type.FullName} [{Token(type)}]");
                    foreach (var field in type.Fields)
                        cil.AppendLine($"  FIELD {Token(field)} {field.FullName}; static={field.IsStatic} literal={field.IsLiteral} initOnly={field.IsInitOnly}");
                    cil.AppendLine();
                }
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    try
                    {
                        var body = method.Body;
                        var raw = pe.GetMethodBody((int)method.RVA).GetILBytes() ?? [];
                        string rawSha = Convert.ToHexStringLower(SHA256.HashData(raw));
                        if (fullType || method.Name == ".cctor")
                        {
                            cil.AppendLine($"METHOD {method.FullName}\nTOKEN {Token(method)} RVA 0x{method.RVA:x8} RAW_IL_SHA256 {rawSha}\nMaxStack {body.MaxStackSize} InitLocals {body.InitLocals}");
                            foreach (var local in body.Variables) cil.AppendLine($"  LOCAL V_{local.Index}: {local.VariableType.FullName}");
                            foreach (var eh in body.ExceptionHandlers) cil.AppendLine($"  EH {eh.HandlerType} try {Label(eh.TryStart)}..{Label(eh.TryEnd)} handler {Label(eh.HandlerStart)}..{Label(eh.HandlerEnd)}");
                            foreach (var instruction in body.Instructions) cil.AppendLine("  " + Line(instruction));
                            cil.AppendLine();
                        }
                        for (int index = 0; index < body.Instructions.Count; index++)
                        {
                            var instruction = body.Instructions[index];
                            string[] nearby = body.Instructions.Skip(Math.Max(0, index - 6)).Take(13).Select(Line).ToArray();
                            if (instruction.OpCode.Code == Code.Stsfld && instruction.Operand is FieldReference field)
                                writers.Add(new { assembly = name, assemblySha256 = sha, mvid, method = method.FullName,
                                    methodToken = Token(method), rawIlSha256 = rawSha, il = Label(instruction),
                                    field = field.FullName, fieldToken = Token(field), nearbyCil = nearby });
                            if (instruction.Operand is not MethodReference target) continue;
                            string owner = target.DeclaringType.FullName;
                            bool lifetime = owner == "UnityEngine.Object" && target.Name is "DontDestroyOnLoad" or "Destroy" or "DestroyImmediate" ||
                                owner == "UnityEngine.MonoBehaviour" && target.Name is "StartCoroutine" or "StopCoroutine" or "StopAllCoroutines" or "Invoke" or "InvokeRepeating" or "CancelInvoke" ||
                                owner == "UnityEngine.SceneManagement.SceneManager" ||
                                owner == "UnityEngine.Application" && target.Name.Contains("Level", StringComparison.Ordinal) ||
                                owner == "UnityEngine.Time" || owner == "UnityEngine.Random" && target.Name is "set_seed" or "InitState" or "get_state" or "set_state";
                            bool eventCall = target.Name.StartsWith("add_", StringComparison.Ordinal) || target.Name.StartsWith("remove_", StringComparison.Ordinal);
                            bool resource = owner is "UnityEngine.Mesh" or "UnityEngine.Material" or "UnityEngine.Texture2D" or "UnityEngine.Texture3D" or "UnityEngine.RenderTexture" && target.Name == ".ctor" ||
                                owner == "UnityEngine.MeshFilter" && target.Name == "get_mesh" ||
                                owner == "UnityEngine.Renderer" && target.Name is "get_material" or "get_materials" ||
                                owner == "UnityEngine.Resources" && target.Name == "UnloadUnusedAssets" ||
                                owner == "UnityEngine.Object" && target.Name is "set_hideFlags" or "Destroy" or "DestroyImmediate";
                            if (resource) resourceReferences.Add(new { assembly = name, assemblySha256 = sha, mvid,
                                method = method.FullName, methodToken = Token(method), rawIlSha256 = rawSha,
                                il = Label(instruction), opcode = instruction.OpCode.Name, api = target.FullName, nearbyCil = nearby,
                                interpretation = instruction.OpCode.Code == Code.Newobj ? "直接构造；实际对象归属及释放需运行验证" :
                                    target.Name.StartsWith("get_", StringComparison.Ordinal) ? "可能实例化的资源getter；不能据此判断返回资源可任意销毁" : "资源生命周期引用；不证明目标是运行时生成资源" });
                            bool trackedConstructor = instruction.OpCode.Code == Code.Newobj &&
                                owner is "UnityEngine.Mesh" or "UnityEngine.Material" or "UnityEngine.Texture2D" or "UnityEngine.Texture3D";
                            bool trackedGetter = instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                                (owner == "UnityEngine.Renderer" && target.Name == "get_material" ||
                                 owner == "UnityEngine.MeshFilter" && target.Name == "get_mesh");
                            if (trackedConstructor || trackedGetter)
                            {
                                var next = instruction.Next;
                                var inboundHere = body.Instructions.Where(i => Targets(i, instruction)).Select(Line).ToArray();
                                var inboundNext = next == null ? [] : body.Instructions.Where(i => Targets(i, next)).Select(Line).ToArray();
                                var boundaries = body.ExceptionHandlers.SelectMany(eh => new[] {
                                    ("TryStart", eh.TryStart), ("TryEnd", eh.TryEnd), ("HandlerStart", eh.HandlerStart),
                                    ("HandlerEnd", eh.HandlerEnd), ("FilterStart", eh.FilterStart) })
                                    .Where(x => x.Item2 == instruction || next != null && x.Item2 == next)
                                    .Select(x => x.Item1 + "=" + Label(x.Item2)).ToArray();
                                resourceInstrumentation.Add(new { assembly = name, method = method.FullName,
                                    methodToken = Token(method), rawIlSha256 = rawSha, il = Label(instruction),
                                    api = target.FullName, opcode = instruction.OpCode.Name,
                                    isStaticConstructor = method.Name == ".cctor", originalMaxStack = body.MaxStackSize,
                                    originalNext = next == null ? null : Line(next), branchesTargetingOriginal = inboundHere,
                                    branchesTargetingOriginalNext = inboundNext, adjacentExceptionBoundaries = boundaries,
                                    previousInstruction = instruction.Previous == null ? null : Line(instruction.Previous),
                                    hasTailPrefix = instruction.Previous?.OpCode.Code == Code.Tail,
                                    interpretation = trackedConstructor ? "已完成构造后可登记该返回实例；后置观察必须不抛异常" :
                                        "返回对象可能已存在/被借用；仅返回值不足以证明本调用创建了资源" });
                            }
                            if (lifetime || eventCall)
                            {
                                var item = new { assembly = name, assemblySha256 = sha, mvid, type = type.FullName,
                                    method = method.FullName, methodToken = Token(method), rawIlSha256 = rawSha,
                                    il = Label(instruction), opcode = instruction.OpCode.Name, api = target.FullName, nearbyCil = nearby };
                                if (lifetime) lifetimeCalls.Add(item);
                                if (eventCall) references.Add(item);
                            }
                        }
                    }
                    catch (Exception ex) { errors.Add($"{name} {method.FullName}: {ex.GetType().Name}: {ex.Message}"); }
                }
            }
        }
        var options = new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var engineApis = new List<object>();
        string enginePath = Path.Combine(managed, "UnityEngine.dll");
        if (File.Exists(enginePath))
        {
            using var engine = ModuleDefinition.ReadModule(enginePath, new ReaderParameters { ReadSymbols = false });
            string engineSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(enginePath)));
            foreach (var type in engine.Types.Where(t => t.FullName is "UnityEngine.Time" or "UnityEngine.Random" or "UnityEngine.Physics" or "UnityEngine.HideFlags" or "UnityEngine.Resources"))
                engineApis.Add(new { assembly = "UnityEngine.dll", sha256 = engineSha, mvid = engine.Mvid.ToString(),
                    type = type.FullName, properties = type.Properties.Select(p => new { name = p.Name,
                        propertyType = p.PropertyType.FullName, canRead = p.GetMethod != null, canWrite = p.SetMethod != null }).ToArray(),
                    fields = type.Fields.Select(f => new { name = f.Name, fieldType = f.FieldType.FullName,
                        isLiteral = f.IsLiteral, constant = f.HasConstant ? f.Constant : null }).ToArray(),
                    publicMethods = type.Methods.Where(m => m.IsPublic).Select(m => new { method = m.FullName,
                        token = Token(m), hasBody = m.HasBody, isInternalCall = m.IsInternalCall }).ToArray() });
        }
        var inventory = new { schemaVersion = 1, evidenceKind = "static-reset-lifecycle-inventory",
            runtimeExecutions = 0, assemblies, staticFields = fields, directStaticWrites = writers,
            lifetimeCalls, eventReferences = references, engineResetApiMetadata = engineApis, methodBodyErrors = errors,
            resourceCreationAndLifetimeReferences = resourceReferences,
            resourceInstrumentationCandidates = resourceInstrumentation,
            limitations = new[] { "只列三个程序集的字段声明和直接CIL引用，不包含Unity原生/其他程序集/动态生成状态。",
                "泛型静态字段声明不等于运行中的全部封闭泛型实例；反射写字段/间接赋值未被直接stsfld扫描覆盖。",
                ".cctor的原CIL不是可重复调用的重置API；不能重新执行它后便假定持久对象、事件、物理和原生缓存已恢复。",
                "所有运行值及同进程重播一致性仍待实际游戏日志验证。" } };
        File.WriteAllText(Path.Combine(output, "reset-static-fields.json"), JsonSerializer.Serialize(inventory, options) + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "cil-reset-evidence.txt"), cil.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"只读重置扫描：{fields.Count} 静态字段，{writers.Count} 直接 stsfld，{lifetimeCalls.Count} 生命周期/时间引用，{references.Count} 事件引用；解析错误 {errors.Count}。未启动或执行游戏。");
        return errors.Count == 0 ? 0 : 2;
    }
    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> source)
    {
        foreach (var type in source) { yield return type; foreach (var child in Types(type.NestedTypes)) yield return child; }
    }
    private static string Token(IMetadataTokenProvider x) => $"0x{x.MetadataToken.ToUInt32():x8}";
    private static bool Targets(Instruction from, Instruction target) =>
        from.Operand is Instruction one && one == target ||
        from.Operand is Instruction[] many && many.Contains(target);
    private static string Label(Instruction? x) => x == null ? "<end>" : $"IL_{x.Offset:x4}";
    private static string Line(Instruction x) => $"{Label(x)}: {x.OpCode.Name} {Operand(x.Operand)}".TrimEnd();
    private static string Operand(object? x) => x switch
    {
        null => "", Instruction i => Label(i), Instruction[] a => string.Join(", ", a.Select(Label)),
        MethodReference m => $"{m.FullName} [{Token(m)}]", FieldReference f => $"{f.FullName} [{Token(f)}]",
        TypeReference t => $"{t.FullName} [{Token(t)}]", VariableDefinition v => $"V_{v.Index}",
        ParameterDefinition p => $"arg_{p.Index} ({p.Name})", string s => JsonSerializer.Serialize(s),
        _ => Convert.ToString(x, CultureInfo.InvariantCulture) ?? ""
    };
}
