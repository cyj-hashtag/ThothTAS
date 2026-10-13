using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using ThothTAS.Core;

namespace ThothTAS.Runtime {
public static partial class Hooks {
    const float DeterministicStep=1f/60f;
    static int modifiedEpoch,modifiedLevelEpoch,modifiedPreparedFrame=-1,modifiedFixedTicks;
    static float modifiedLastFixedTime;
    static string modifiedLevel="";
    static StreamWriter nativeTelemetry;
    static int modifiedUpdateFrame=-1,modifiedUpdateDepth;
    sealed class UpdateEntry {public MonoBehaviour source;public MethodInfo method;public string key;}
    static readonly Dictionary<Type,MethodInfo> updateMethods=new Dictionary<Type,MethodInfo>();
    static bool ModifiedRules {get {return script!=null&&script.Rules==TasRules.Deterministic1;}}

    static void ResetModifiedClock() {
        modifiedEpoch=Time.frameCount;modifiedLevelEpoch=modifiedEpoch;
        modifiedPreparedFrame=-1;modifiedFixedTicks=0;modifiedLastFixedTime=Time.fixedTime;
        modifiedUpdateFrame=-1;modifiedUpdateDepth=0;
        modifiedLevel=SceneManager.GetActiveScene().name;
    }
    static void ModifiedPhase(string name) {
        if(!ModifiedRules)return;
        string scene=SceneManager.GetActiveScene().name;
        if(scene!=modifiedLevel){modifiedLevel=scene;modifiedLevelEpoch=Time.frameCount;}
        if(name.EndsWith(".FixedUpdate",StringComparison.Ordinal)&&Time.fixedTime!=modifiedLastFixedTime) {
            modifiedLastFixedTime=Time.fixedTime;modifiedFixedTicks++;
        }
        if(modifiedPreparedFrame==Time.frameCount)return;
        modifiedPreparedFrame=Time.frameCount;
        // The modified profile deliberately replaces the native 100Hz rule and
        // death slow motion with one constant 60Hz step. The original profile
        // retains its existing native behavior.
        Time.timeScale=1;Time.fixedDeltaTime=DeterministicStep;
        foreach(Rigidbody body in UnityEngine.Object.FindObjectsOfType(typeof(Rigidbody))) {
            body.interpolation=RigidbodyInterpolation.None;
            body.constraints=body.constraints|RigidbodyConstraints.FreezeRotation;
        }
    }
    public static bool UseDeterministicRules() {Ensure();return ModifiedRules;}
    public static float RulesTime(string name) {
        Ensure();EngineReady();
        if(name=="get_deltaTime"||name=="get_fixedDeltaTime"||name=="get_unscaledDeltaTime"||name=="get_smoothDeltaTime")return DeterministicStep;
        if(name=="get_timeScale")return 1;
        if(name=="get_fixedTime")return modifiedFixedTicks*DeterministicStep;
        if(name=="get_timeSinceLevelLoad")return (Time.frameCount-modifiedLevelEpoch)*DeterministicStep;
        if(name=="get_time"||name=="get_unscaledTime"||name=="get_realtimeSinceStartup")return (Time.frameCount-modifiedEpoch)*DeterministicStep;
        throw new InvalidOperationException("Unsupported deterministic clock: "+name);
    }
    public static int RulesFrameCount() {Ensure();EngineReady();return Time.frameCount-modifiedEpoch;}
    public static void RulesTimeScale(float value) {if(!ModifiedRules)Time.timeScale=value;}
    public static float RulesRandomFloat(float minimum,float maximum) {
        // Zero-centered perturbations become zero; scale ranges become their
        // neutral midpoint. There is no Unity random-state consumption.
        return (float)((double)minimum+((double)maximum-minimum)*0.5);
    }
    public static int RulesRandomInt(int minimum,int maximum) {
        if(maximum<=minimum)return minimum;
        return (int)((long)minimum+((long)maximum-minimum)/2);
    }
    public static float RulesRandomValue() {return 0.5f;}

    static MethodInfo ModifiedUpdateMethod(Type type) {
        MethodInfo method;
        if(updateMethods.TryGetValue(type,out method))return method;
        for(Type parent=type;parent!=null;parent=parent.BaseType) {
            method=parent.GetMethod("__TasDet_Update",BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly);
            if(method!=null)break;
        }
        updateMethods[type]=method;return method;
    }
    static string StableUpdateKey(MonoBehaviour source) {
        var parts=new List<string>();var t=source.transform;
        while(t!=null){parts.Add(t.name+(t.parent==null?"":"["+t.GetSiblingIndex()+"]"));t=t.parent;}
        parts.Reverse();return string.Join("/",parts.ToArray())+"/"+source.GetType().FullName+"#"+Array.IndexOf(source.GetComponents(source.GetType()),source);
    }
    static void InvokeModifiedUpdate(MonoBehaviour source,MethodInfo method) {
        try {method.Invoke(source,null);}catch(TargetInvocationException e){throw e.InnerException??e;}
    }
    public static void DeterministicUpdate(MonoBehaviour caller,string declaringType) {
        Ensure();EngineReady();
        // Explicit calls to a base Update must still execute that base body;
        // Unity message entrypoints only dispatch one stable batch per cycle.
        if(modifiedUpdateDepth>0) {
            var type=caller.GetType().Assembly.GetType(declaringType,true);
            var body=type.GetMethod("__TasDet_Update",BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly);
            InvokeModifiedUpdate(caller,body);return;
        }
        if(modifiedUpdateFrame==Time.frameCount)return;
        modifiedUpdateFrame=Time.frameCount;
        var entries=new List<UpdateEntry>();
        foreach(MonoBehaviour source in UnityEngine.Object.FindObjectsOfType(typeof(MonoBehaviour))) {
            if(source==null||!source.enabled||!source.gameObject.activeInHierarchy)continue;
            var method=ModifiedUpdateMethod(source.GetType());if(method==null)continue;
            entries.Add(new UpdateEntry{source=source,method=method,key=StableUpdateKey(source)});
        }
        entries.Sort(delegate(UpdateEntry a,UpdateEntry b){return StringComparer.Ordinal.Compare(a.key,b.key);});
        modifiedUpdateDepth++;
        try {foreach(var entry in entries)if(entry.source!=null&&entry.source.enabled&&entry.source.gameObject.activeInHierarchy)InvokeModifiedUpdate(entry.source,entry.method);}
        finally {modifiedUpdateDepth--;}
    }

    static void ResetModifiedSceneTrace() {
        if(!ModifiedRules)return;
        Flush();randomLog.Dispose();phaseLog.Dispose();
        File.Move(Path.Combine(run,"random.tsv"),Path.Combine(run,"bootstrap-random.tsv"));
        File.Move(Path.Combine(run,"phases.tsv"),Path.Combine(run,"bootstrap-phases.tsv"));
        randomLog=Log("random.tsv");phaseLog=Log("phases.tsv");
        randomLog.WriteLine("index\tframe\tphase\tsite\tapi\targuments\tresult");phaseLog.WriteLine("index\tframe\tcallback");
        randomCount=0;phaseCount=0;phaseFrameCount=0;randomHash=14695981039346656037UL;phaseHash=14695981039346656037UL;
        ResetModifiedClock();
        eventLog.WriteLine("rules=deterministic-1;random=neutral-constants;physics=60Hz;timeScale=1;interpolation=none;rigidbodyRotation=frozen;clock=scene-relative;bootstrapTrace=separate");
        nativeTelemetry=Log("native-telemetry.jsonl");
    }
    static void RemoveModifiedLauncherSound() {
        if(!ModifiedRules)return;
        // The launch jingle has no gameplay role. Its wall-clock completion
        // formerly destroyed a persistent Update source at an arbitrary frame.
        foreach(MonoBehaviour source in UnityEngine.Object.FindObjectsOfType(typeof(MonoBehaviour))) {
            if(source!=null&&source.GetType().FullName=="LauncherSound") {
                foreach(AudioSource audio in source.GetComponents<AudioSource>())audio.Stop();
                UnityEngine.Object.DestroyImmediate(source.gameObject);
            }
        }
    }
    static void AppendRulesState(SortedDictionary<string,string> map) {
        if(!ModifiedRules)return;
        map["Rules.profile"]="Deterministic-1";
        map["Rules.fixedTicks"]=modifiedFixedTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        map["Rules.random"]="neutral-constants";
        nativeTelemetry.WriteLine("{\"frame\":"+frame+",\"nativeFrame\":"+Time.frameCount+",\"nativeTime\":"+F(Time.time)+",\"nativeFixedTime\":"+F(Time.fixedTime)+",\"nativeDelta\":"+F(Time.deltaTime)+",\"nativeFixedDelta\":"+F(Time.fixedDeltaTime)+",\"nativeTimeScale\":"+F(Time.timeScale)+"}");
        nativeTelemetry.Flush();
    }
}
}
