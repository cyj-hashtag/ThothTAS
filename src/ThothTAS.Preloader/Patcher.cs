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
    static MethodReference Hook(ModuleDefinition m,string name) { return m.ImportReference(hooks.Methods.First(x=>x.Name==name)); }
    public static void Initialize() {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("THOTH_TAS_SESSION"))) return;
        bridge=ModuleDefinition.ReadModule(Path.Combine(Path.GetDirectoryName(typeof(Patcher).Assembly.Location),"../plugins/ThothTAS.Runtime.dll"));
        hooks=bridge.Types.First(t=>t.FullName=="ThothTAS.Runtime.Hooks");
    }
    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> ts) { foreach(var t in ts) {yield return t;foreach(var n in Types(t.NestedTypes))yield return n;} }
    public static void Patch(AssemblyDefinition assembly) {
        var m=assembly.MainModule;
        var methods=Types(m.Types).SelectMany(t=>t.Methods).Where(x=>x.HasBody).ToArray();
        var proxy=new TypeDefinition("ThothTAS.Generated","Proxies",TypeAttributes.NotPublic|TypeAttributes.Abstract|TypeAttributes.Sealed,m.TypeSystem.Object); m.Types.Add(proxy);
        int random=0,input=0,resources=0;
        foreach(var method in methods) {
            ExpandBranches(method);
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
                    string site=assembly.Name.Name+"|"+method.MetadataToken.ToInt32().ToString("X8")+"|"+ins.Offset.ToString("X4")+"|"+method.DeclaringType.FullName+"."+method.Name;
                    var wrap=Wrapper(m,proxy,mr,"R"+(random++)); var il=wrap.Body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ldstr,site)); il.Append(il.Create(OpCodes.Ldstr,mr.FullName));
                    il.Append(il.Create(OpCodes.Ldc_I4,mr.Parameters.Count));il.Append(il.Create(OpCodes.Newarr,m.TypeSystem.Object));
                    for(int p=0;p<mr.Parameters.Count;p++) {il.Append(il.Create(OpCodes.Dup));il.Append(il.Create(OpCodes.Ldc_I4,p));il.Append(il.Create(OpCodes.Ldarg,wrap.Parameters[p]));if(mr.Parameters[p].ParameterType.IsValueType)il.Append(il.Create(OpCodes.Box,m.ImportReference(mr.Parameters[p].ParameterType)));il.Append(il.Create(OpCodes.Stelem_Ref));}
                    il.Append(il.Create(OpCodes.Call,Hook(m,"BeforeRandom")));
                    for(int p=0;p<mr.Parameters.Count;p++)il.Append(il.Create(OpCodes.Ldarg,wrap.Parameters[p]));
                    il.Append(il.Create(OpCodes.Call,mr));
                    if(mr.ReturnType.FullName!="System.Void") {var v=new VariableDefinition(m.ImportReference(mr.ReturnType));wrap.Body.Variables.Add(v);wrap.Body.InitLocals=true;il.Append(il.Create(OpCodes.Stloc,v));il.Append(il.Create(OpCodes.Ldloc,v));if(mr.ReturnType.IsValueType)il.Append(il.Create(OpCodes.Box,m.ImportReference(mr.ReturnType)));il.Append(il.Create(OpCodes.Call,Hook(m,"AfterRandom")));il.Append(il.Create(OpCodes.Ldloc,v));}
                    else {il.Append(il.Create(OpCodes.Ldnull));il.Append(il.Create(OpCodes.Call,Hook(m,"AfterRandom")));}
                    il.Append(il.Create(OpCodes.Ret));ins.OpCode=OpCodes.Call;ins.Operand=wrap;
                } else if(mr.DeclaringType.FullName=="UnityEngine.Input" && !mr.HasThis) {
                    var wrap=Wrapper(m,proxy,mr,"I"+(input++));DefaultReturn(wrap);ins.OpCode=OpCodes.Call;ins.Operand=wrap;
                } else if(mr.DeclaringType.FullName=="UnityEngine.Screen" && mr.Name=="SetResolution" && mr.Parameters.Count==3) {ins.Operand=Hook(m,"SetResolution");}
                else if(mr.DeclaringType.FullName=="UnityEngine.Object"&&mr.Name=="DontDestroyOnLoad") {ins.OpCode=OpCodes.Call;ins.Operand=Hook(m,"PersistGameObject");}
                else if(mr.DeclaringType.FullName=="UnityEngine.AudioSource" && new[]{"LaserBeat","RhythmLaser"}.Contains(method.DeclaringType.FullName) && mr.Parameters.Count==0) {
                    string audioHook=null;
                    if(mr.Name=="Play")audioHook="AudioPlay";
                    else if(mr.Name=="Stop")audioHook="AudioStop";
                    else if(mr.Name=="get_time")audioHook="AudioTime";
                    else if(mr.Name=="get_isPlaying")audioHook="AudioIsPlaying";
                    if(audioHook!=null){ins.OpCode=OpCodes.Call;ins.Operand=Hook(m,audioHook);}
                }
            }
            if(method.DeclaringType.FullName=="PlayerInput") {
                method.Body.Instructions.Clear();method.Body.ExceptionHandlers.Clear();method.Body.Variables.Clear();
                if(method.ReturnType.FullName=="System.Single") {var il=method.Body.GetILProcessor();il.Append(il.Create(OpCodes.Ldstr,method.Name));il.Append(il.Create(OpCodes.Ldarg_0));il.Append(il.Create(OpCodes.Call,Hook(m,"ReadInput")));il.Append(il.Create(OpCodes.Ret));}
                else DefaultReturn(method);
            }
            // User explicitly authorized removing the Steam requirement for the private TAS copy.
            if(method.DeclaringType.FullName=="SteamManager" && new[]{"Awake","OnEnable","Update","OnDestroy","UnlockAchievement"}.Contains(method.Name)) {method.Body.Instructions.Clear();method.Body.ExceptionHandlers.Clear();method.Body.Variables.Clear();method.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));}
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
            if(new[]{"Awake","Start","Update","FixedUpdate","LateUpdate","OnRenderImage","OnPreCull","OnPostRender","OnCollisionEnter","OnCollisionStay","OnTriggerEnter","OnTriggerStay"}.Contains(method.Name) && method.ReturnType.FullName=="System.Void") {
                var il=method.Body.GetILProcessor();var first=method.Body.Instructions[0];
                il.InsertBefore(first,il.Create(OpCodes.Ldstr,method.DeclaringType.FullName+"."+method.Name));
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
        Console.WriteLine("THOTH TAS patched "+assembly.Name.Name+" random="+random+" input="+input+" resources="+resources);
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
