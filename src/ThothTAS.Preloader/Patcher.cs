using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ThothTAS.Preloader {
public static class Patcher {
    public static IEnumerable<string> TargetDLLs { get { return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("THOTH_TAS_SESSION")) ? new string[0] : new[] { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "Assembly-UnityScript-firstpass.dll" }; } }
    static TypeDefinition hooks;
    static ModuleDefinition bridge;
    static bool modifiedRules;
    static readonly Dictionary<MethodDefinition,MethodDefinition> updateOrigins=new Dictionary<MethodDefinition,MethodDefinition>();
    static readonly HashSet<MethodDefinition> updateEntrypoints=new HashSet<MethodDefinition>();
    static MethodReference Hook(ModuleDefinition m,string name) { return m.ImportReference(hooks.Methods.First(x=>x.Name==name)); }
    public static void Initialize() {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("THOTH_TAS_SESSION"))) return;
        string rule="original";
        foreach(string line in File.ReadAllLines(Environment.GetEnvironmentVariable("THOTH_TAS_SESSION")))if(line.StartsWith("rules=",StringComparison.Ordinal))rule=line.Substring(6);
        if(rule!="original"&&rule!="deterministic-1")throw new IOException("Unsupported TAS rules profile.");
        modifiedRules=rule=="deterministic-1";
        bridge=ModuleDefinition.ReadModule(Path.Combine(Path.GetDirectoryName(typeof(Patcher).Assembly.Location),"../plugins/ThothTAS.Runtime.dll"));
        hooks=bridge.Types.First(t=>t.FullName=="ThothTAS.Runtime.Hooks");
    }
    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> ts) { foreach(var t in ts) {yield return t;foreach(var n in Types(t.NestedTypes))yield return n;} }
    public static void Patch(ref AssemblyDefinition assembly) {
        bool community=false;
        if(assembly.Name.Name=="Assembly-CSharp") {
            string session=Environment.GetEnvironmentVariable("THOTH_TAS_SESSION");
            string communityPath=null;
            if(!string.IsNullOrEmpty(session))foreach(string line in File.ReadAllLines(session))if(line.StartsWith("communityModPath=",StringComparison.Ordinal))communityPath=line.Substring("communityModPath=".Length);
            if(!string.IsNullOrEmpty(communityPath)) {
                using(var sha=System.Security.Cryptography.SHA256.Create())using(var file=File.OpenRead(communityPath)) {
                    string actual=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
                    if(actual!="2A35DDEA8A48144E926AC33796FFD63267A5AC129FD2495EF7F10A7D893E33FD")throw new IOException("Community Speedrun Mod v2.8 hash mismatch; refusing load.");
                }
                var replacement=AssemblyDefinition.ReadAssembly(communityPath,new ReaderParameters {AssemblyResolver=assembly.MainModule.AssemblyResolver,ReadingMode=ReadingMode.Immediate});
                if(replacement.Name.Name!="Assembly-CSharp"||!replacement.MainModule.Types.Any(t=>t.Name=="SpeedrunTimers"))throw new IOException("Unsupported community assembly.");
                assembly=replacement;
                community=true;
                Console.WriteLine("THOTH TAS community Speedrun Mod v2.8 loaded in memory; original disk assembly unchanged.");
            }
        }
        var m=assembly.MainModule;
        if(modifiedRules)WrapUpdates(m);
        var methods=Types(m.Types).SelectMany(t=>t.Methods).Where(x=>x.HasBody).ToArray();
        var proxy=new TypeDefinition("ThothTAS.Generated","Proxies",TypeAttributes.NotPublic|TypeAttributes.Abstract|TypeAttributes.Sealed,m.TypeSystem.Object); m.Types.Add(proxy);
        int random=0,input=0,resources=0,clocks=0;
        foreach(var method in methods) {
            ExpandBranches(method);
            if(community&&method.DeclaringType.FullName=="SimpleLauncher"&&method.Name=="Start") {
                // The old mod rewrites the stock ten-line settings file. Keep the
                // copy's quality preferences and let the TAS session own its window.
                method.Body.Instructions.Clear();method.Body.ExceptionHandlers.Clear();method.Body.Variables.Clear();
                var launcherIl=method.Body.GetILProcessor();
                launcherIl.Append(launcherIl.Create(OpCodes.Call,Hook(m,"CommunityLauncherStart")));
                launcherIl.Append(launcherIl.Create(OpCodes.Ret));
            }
            if(community&&method.DeclaringType.FullName=="SpeedrunTimers"&&method.Name=="FormatTime") {
                // Correct the original display's <100 ms centisecond rounding.
                method.Body.Instructions.Clear();method.Body.ExceptionHandlers.Clear();method.Body.Variables.Clear();
                var timerIl=method.Body.GetILProcessor();timerIl.Append(timerIl.Create(OpCodes.Ldarg_0));
                timerIl.Append(timerIl.Create(OpCodes.Call,Hook(m,"CommunityFormatTime")));timerIl.Append(timerIl.Create(OpCodes.Ret));
            }
            // Main gameplay owns these constructed/instanced resources. Retain
            // .cctor assets and leave other assemblies' static post-effect caches
            // to their original cleanup methods. Shared/imported getters are untouched.
            if(assembly.Name.Name=="Assembly-CSharp"&&method.Name!=".cctor") {
                var il=method.Body.GetILProcessor();
                foreach(var ins in method.Body.Instructions.ToArray()) {
                    var resource=ins.Operand as MethodReference;
                    if(resource==null)continue;
                    bool construction=ins.OpCode.Code==Code.Newobj&&new[]{"UnityEngine.Mesh","UnityEngine.Material","UnityEngine.Texture2D","UnityEngine.Texture3D"}.Contains(resource.DeclaringType.FullName);
                    bool instanceGetter=(ins.OpCode.Code==Code.Call||ins.OpCode.Code==Code.Callvirt)&&
                        (resource.DeclaringType.FullName=="UnityEngine.Renderer"&&resource.Name=="get_material"||resource.DeclaringType.FullName=="UnityEngine.MeshFilter"&&resource.Name=="get_mesh");
                    if(!construction&&!instanceGetter)continue;
                    if(instanceGetter){ins.OpCode=OpCodes.Call;ins.Operand=Hook(m,resource.Name=="get_material"?"MaterialInstance":"MeshInstance");}
                    else {var duplicate=il.Create(OpCodes.Dup);il.InsertAfter(ins,duplicate);il.InsertAfter(duplicate,il.Create(OpCodes.Call,Hook(m,"TrackOwnedResource")));}
                    resources++;
                }
            }
            foreach(var ins in method.Body.Instructions.ToArray()) {
                var mr=ins.Operand as MethodReference;
                if(mr==null || (ins.OpCode.Code!=Code.Call && ins.OpCode.Code!=Code.Callvirt)) continue;
                if(mr.DeclaringType.FullName=="UnityEngine.Random") {
                    MethodDefinition origin;var original=updateOrigins.TryGetValue(method,out origin)?origin:method;
                    string site=assembly.Name.Name+"|"+original.MetadataToken.ToInt32().ToString("X8")+"|"+ins.Offset.ToString("X4")+"|"+method.DeclaringType.FullName+"."+original.Name;
                    var wrap=Wrapper(m,proxy,mr,"R"+(random++)); var il=wrap.Body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ldstr,site)); il.Append(il.Create(OpCodes.Ldstr,(modifiedRules?"neutral:":"")+mr.FullName));
                    il.Append(il.Create(OpCodes.Ldc_I4,mr.Parameters.Count));il.Append(il.Create(OpCodes.Newarr,m.TypeSystem.Object));
                    for(int p=0;p<mr.Parameters.Count;p++) {il.Append(il.Create(OpCodes.Dup));il.Append(il.Create(OpCodes.Ldc_I4,p));il.Append(il.Create(OpCodes.Ldarg,wrap.Parameters[p]));if(mr.Parameters[p].ParameterType.IsValueType)il.Append(il.Create(OpCodes.Box,m.ImportReference(mr.Parameters[p].ParameterType)));il.Append(il.Create(OpCodes.Stelem_Ref));}
                    il.Append(il.Create(OpCodes.Call,Hook(m,"BeforeRandom")));
                    for(int p=0;p<mr.Parameters.Count;p++)il.Append(il.Create(OpCodes.Ldarg,wrap.Parameters[p]));
                    if(modifiedRules) {
                        string neutral=mr.Name=="get_value"?"RulesRandomValue":mr.Name=="Range"&&mr.Parameters.Count==2&&mr.Parameters[0].ParameterType.FullName=="System.Single"?"RulesRandomFloat":mr.Name=="Range"&&mr.Parameters.Count==2&&mr.Parameters[0].ParameterType.FullName=="System.Int32"?"RulesRandomInt":null;
                        if(neutral==null)throw new IOException("Unsupported random API in deterministic profile: "+mr.FullName);
                        il.Append(il.Create(OpCodes.Call,Hook(m,neutral)));
                    } else il.Append(il.Create(OpCodes.Call,mr));
                    if(mr.ReturnType.FullName!="System.Void") {var v=new VariableDefinition(m.ImportReference(mr.ReturnType));wrap.Body.Variables.Add(v);wrap.Body.InitLocals=true;il.Append(il.Create(OpCodes.Stloc,v));il.Append(il.Create(OpCodes.Ldloc,v));if(mr.ReturnType.IsValueType)il.Append(il.Create(OpCodes.Box,m.ImportReference(mr.ReturnType)));il.Append(il.Create(OpCodes.Call,Hook(m,"AfterRandom")));il.Append(il.Create(OpCodes.Ldloc,v));}
                    else {il.Append(il.Create(OpCodes.Ldnull));il.Append(il.Create(OpCodes.Call,Hook(m,"AfterRandom")));}
                    il.Append(il.Create(OpCodes.Ret));ins.OpCode=OpCodes.Call;ins.Operand=wrap;
                } else if(modifiedRules&&mr.DeclaringType.FullName=="UnityEngine.Time") {
                    if(mr.Name=="set_timeScale") {ins.OpCode=OpCodes.Call;ins.Operand=Hook(m,"RulesTimeScale");clocks++;}
                    else if(mr.Name=="get_frameCount") {ins.OpCode=OpCodes.Call;ins.Operand=Hook(m,"RulesFrameCount");clocks++;}
                    else if(new[]{"get_deltaTime","get_fixedDeltaTime","get_unscaledDeltaTime","get_smoothDeltaTime","get_time","get_fixedTime","get_unscaledTime","get_realtimeSinceStartup","get_timeSinceLevelLoad","get_timeScale"}.Contains(mr.Name)) {
                        var timeProxy=Wrapper(m,proxy,mr,"T"+(clocks++));var timeIl=timeProxy.Body.GetILProcessor();
                        timeIl.Append(timeIl.Create(OpCodes.Ldstr,mr.Name));timeIl.Append(timeIl.Create(OpCodes.Call,Hook(m,"RulesTime")));timeIl.Append(timeIl.Create(OpCodes.Ret));
                        ins.OpCode=OpCodes.Call;ins.Operand=timeProxy;
                    }
                } else if(community&&mr.DeclaringType.FullName=="System.Diagnostics.Stopwatch" && new[]{"Room","Globals","SpeedrunTimers"}.Contains(method.DeclaringType.FullName) && mr.Parameters.Count==0 && new[]{"Start","Stop","get_Elapsed"}.Contains(mr.Name)) {
                    ins.OpCode=OpCodes.Call;ins.Operand=Hook(m,mr.Name=="Start"?"CommunityTimerStart":mr.Name=="Stop"?"CommunityTimerStop":"CommunityTimerElapsed");
                } else if(mr.DeclaringType.FullName=="UnityEngine.Input" && !mr.HasThis) {
                    var wrap=Wrapper(m,proxy,mr,"I"+(input++));DefaultReturn(wrap);ins.OpCode=OpCodes.Call;ins.Operand=wrap;
                } else if(mr.DeclaringType.FullName=="UnityEngine.Screen" && mr.Name=="SetResolution" && mr.Parameters.Count==3) {ins.Operand=Hook(m,"SetResolution");}
                else if(mr.DeclaringType.FullName=="UnityEngine.Object"&&mr.Name=="DontDestroyOnLoad") {ins.OpCode=OpCodes.Call;ins.Operand=Hook(m,"PersistGameObject");}
                else if(mr.DeclaringType.FullName=="UnityEngine.AudioSource" && (modifiedRules||new[]{"LaserBeat","RhythmLaser"}.Contains(method.DeclaringType.FullName)) && (modifiedRules||mr.Parameters.Count==0)) {
                    string audioHook=null;
                    if(mr.Name=="Play"&&mr.Parameters.Count==0)audioHook="AudioPlay";
                    else if(mr.Name=="Stop")audioHook="AudioStop";
                    else if(mr.Name=="get_time")audioHook="AudioTime";
                    else if(mr.Name=="get_isPlaying")audioHook="AudioIsPlaying";
                    else if(modifiedRules&&mr.Name=="set_time")audioHook="AudioSeek";
                    else if(modifiedRules&&mr.Name=="set_pitch")audioHook="AudioPitch";
                    else if(modifiedRules&&mr.Name=="PlayDelayed")audioHook="AudioPlayDelayed";
                    else if(modifiedRules&&mr.Name=="Pause")audioHook="AudioPause";
                    else if(modifiedRules&&mr.Name=="UnPause")audioHook="AudioUnPause";
                    else if(modifiedRules&&mr.Name=="PlayOneShot")audioHook=mr.Parameters.Count==1?"AudioOneShot":"AudioOneShotScale";
                    if(audioHook!=null){ins.OpCode=OpCodes.Call;ins.Operand=Hook(m,audioHook);}
                }
            }
            if(method.DeclaringType.FullName=="PlayerInput") {
                method.Body.Instructions.Clear();method.Body.ExceptionHandlers.Clear();method.Body.Variables.Clear();
                if(method.ReturnType.FullName=="System.Single") {var il=method.Body.GetILProcessor();il.Append(il.Create(OpCodes.Ldstr,method.Name));il.Append(il.Create(OpCodes.Ldarg_0));il.Append(il.Create(OpCodes.Call,Hook(m,"ReadInput")));il.Append(il.Create(OpCodes.Ret));}
                else DefaultReturn(method);
            }
            // User explicitly authorized removing the Steam requirement for the private TAS copy.
            if(method.DeclaringType.FullName=="SteamManager" && new[]{"Awake","OnEnable","Update","__TasDet_Update","OnDestroy","UnlockAchievement"}.Contains(method.Name)) {method.Body.Instructions.Clear();method.Body.ExceptionHandlers.Clear();method.Body.Variables.Clear();method.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));}
            if(method.DeclaringType.FullName=="InControl.InControlManager" && new[]{"OnApplicationFocus","OnApplicationPause"}.Contains(method.Name)) {method.Body.Instructions.Clear();method.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));}
            if(method.DeclaringType.FullName=="Teleporter" && method.Name=="OnOtherReceiverOn") {
                var il=method.Body.GetILProcessor();var first=method.Body.Instructions[0];
                il.InsertBefore(first,il.Create(OpCodes.Ldarg_0));il.InsertBefore(first,il.Create(OpCodes.Call,Hook(m,"BeforeTeleport")));
                foreach(var ret in method.Body.Instructions.Where(x=>x.OpCode.Code==Code.Ret).ToArray()) {
                    // Keep existing branch targets at this instruction, then execute the observer.
                    ret.OpCode=OpCodes.Nop;
                    var arg=il.Create(OpCodes.Ldarg_0);var call=il.Create(OpCodes.Call,Hook(m,"AfterTeleport"));var end=il.Create(OpCodes.Ret);
                    il.InsertAfter(ret,arg);il.InsertAfter(arg,call);il.InsertAfter(call,end);
                }
            }
            MethodDefinition updateOrigin;string observedName=updateOrigins.TryGetValue(method,out updateOrigin)?updateOrigin.Name:method.Name;
            if(!updateEntrypoints.Contains(method)&&new[]{"Awake","Start","Update","FixedUpdate","LateUpdate","OnRenderImage","OnPreCull","OnPostRender","OnCollisionEnter","OnCollisionStay","OnTriggerEnter","OnTriggerStay"}.Contains(observedName) && method.ReturnType.FullName=="System.Void") {
                var il=method.Body.GetILProcessor();var first=method.Body.Instructions[0];
                il.InsertBefore(first,il.Create(OpCodes.Ldstr,method.DeclaringType.FullName+"."+observedName));
                il.InsertBefore(first,il.Create(OpCodes.Call,Hook(m,"Phase")));
            }
        }
        // Make initial additive resource loading synchronous; frame count must not depend on disk scheduling.
        if(assembly.Name.Name=="Assembly-CSharp") {
            var loader=methods.FirstOrDefault(x=>x.DeclaringType.Name=="SoundBankLoader" && x.Name=="Start");
            if(loader!=null) foreach(var ins in loader.Body.Instructions.ToArray()) {
                var mr=ins.Operand as MethodReference;
                if(mr!=null && mr.Name=="LoadSceneAsync") {
                    var sync=new MethodReference("LoadScene",m.TypeSystem.Void,mr.DeclaringType);foreach(var p in mr.Parameters)sync.Parameters.Add(new ParameterDefinition(p.ParameterType));
                    ins.Operand=sync;var next=ins.Next;if(next!=null && next.OpCode.Code==Code.Pop)next.OpCode=OpCodes.Nop;
                }
            }
        }
        Console.WriteLine("THOTH TAS patched "+assembly.Name.Name+" random="+random+" input="+input+" resources="+resources+" clocks="+clocks+" rules="+(modifiedRules?"deterministic-1":"original"));
    }
    static bool IsBehaviour(TypeDefinition type) {
        TypeReference parent=type.BaseType;
        while(parent!=null) {
            if(parent.FullName=="UnityEngine.MonoBehaviour")return true;
            var definition=parent.Resolve();if(definition==null)return false;parent=definition.BaseType;
        }
        return false;
    }
    static void WrapUpdates(ModuleDefinition module) {
        foreach(var type in Types(module.Types).ToArray()) {
            if(!IsBehaviour(type))continue;
            foreach(var method in type.Methods.Where(x=>x.Name=="Update"&&x.HasBody&&x.HasThis&&x.Parameters.Count==0&&x.ReturnType.FullName=="System.Void").ToArray()) {
                var implementation=new MethodDefinition("__TasDet_Update",MethodAttributes.Private|MethodAttributes.HideBySig,module.TypeSystem.Void);
                type.Methods.Add(implementation);
                var oldBody=method.Body;var body=new MethodBody(implementation){InitLocals=oldBody.InitLocals,MaxStackSize=oldBody.MaxStackSize};implementation.Body=body;
                var variables=new Dictionary<VariableDefinition,VariableDefinition>();
                foreach(var v in oldBody.Variables){var copy=new VariableDefinition(v.VariableType);body.Variables.Add(copy);variables[v]=copy;}
                var instructions=new Dictionary<Instruction,Instruction>();
                foreach(var old in oldBody.Instructions){var copy=Instruction.Create(OpCodes.Nop);copy.OpCode=old.OpCode;copy.Operand=old.Operand;copy.Offset=old.Offset;body.Instructions.Add(copy);instructions[old]=copy;}
                foreach(var old in oldBody.Instructions) {
                    var copy=instructions[old];var target=old.Operand as Instruction;var targets=old.Operand as Instruction[];var local=old.Operand as VariableDefinition;
                    if(target!=null)copy.Operand=instructions[target];else if(targets!=null)copy.Operand=targets.Select(x=>instructions[x]).ToArray();else if(local!=null)copy.Operand=variables[local];
                }
                foreach(var handler in oldBody.ExceptionHandlers)body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType){CatchType=handler.CatchType,TryStart=handler.TryStart==null?null:instructions[handler.TryStart],TryEnd=handler.TryEnd==null?null:instructions[handler.TryEnd],HandlerStart=handler.HandlerStart==null?null:instructions[handler.HandlerStart],HandlerEnd=handler.HandlerEnd==null?null:instructions[handler.HandlerEnd],FilterStart=handler.FilterStart==null?null:instructions[handler.FilterStart]});
                updateOrigins[implementation]=method;updateEntrypoints.Add(method);
                method.Body=new MethodBody(method);var il=method.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldarg_0));il.Append(il.Create(OpCodes.Ldstr,type.FullName));il.Append(il.Create(OpCodes.Call,Hook(module,"DeterministicUpdate")));il.Append(il.Create(OpCodes.Ret));
            }
        }
    }
    static MethodDefinition Wrapper(ModuleDefinition m,TypeDefinition t,MethodReference mr,string name) {
        var w=new MethodDefinition(name,MethodAttributes.Public|MethodAttributes.Static,m.ImportReference(mr.ReturnType));
        foreach(var p in mr.Parameters)w.Parameters.Add(new ParameterDefinition(m.ImportReference(p.ParameterType)));t.Methods.Add(w);return w;
    }
    static void ExpandBranches(MethodDefinition method) {
        // Observers can enlarge a method beyond a short branch's signed byte range.
        foreach(var ins in method.Body.Instructions) {
            switch(ins.OpCode.Code) {
                case Code.Br_S:ins.OpCode=OpCodes.Br;break;
                case Code.Brfalse_S:ins.OpCode=OpCodes.Brfalse;break;
                case Code.Brtrue_S:ins.OpCode=OpCodes.Brtrue;break;
                case Code.Beq_S:ins.OpCode=OpCodes.Beq;break;
                case Code.Bge_S:ins.OpCode=OpCodes.Bge;break;
                case Code.Bge_Un_S:ins.OpCode=OpCodes.Bge_Un;break;
                case Code.Bgt_S:ins.OpCode=OpCodes.Bgt;break;
                case Code.Bgt_Un_S:ins.OpCode=OpCodes.Bgt_Un;break;
                case Code.Ble_S:ins.OpCode=OpCodes.Ble;break;
                case Code.Ble_Un_S:ins.OpCode=OpCodes.Ble_Un;break;
                case Code.Blt_S:ins.OpCode=OpCodes.Blt;break;
                case Code.Blt_Un_S:ins.OpCode=OpCodes.Blt_Un;break;
                case Code.Bne_Un_S:ins.OpCode=OpCodes.Bne_Un;break;
                case Code.Leave_S:ins.OpCode=OpCodes.Leave;break;
            }
        }
    }
    static void DefaultReturn(MethodDefinition w) {
        var il=w.Body.GetILProcessor();var rt=w.ReturnType;
        if(rt.FullName!="System.Void") {
            if(rt.IsValueType) {var v=new VariableDefinition(rt);w.Body.Variables.Add(v);w.Body.InitLocals=true;il.Append(il.Create(OpCodes.Ldloca,v));il.Append(il.Create(OpCodes.Initobj,rt));il.Append(il.Create(OpCodes.Ldloc,v));}
            else if(rt.FullName=="System.String")il.Append(il.Create(OpCodes.Ldstr,""));
            else if(rt is ArrayType){il.Append(il.Create(OpCodes.Ldc_I4_0));il.Append(il.Create(OpCodes.Newarr,((ArrayType)rt).ElementType));}
            else il.Append(il.Create(OpCodes.Ldnull));
        }il.Append(il.Create(OpCodes.Ret));
    }
}
}
