using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using ThothTAS.Core;

namespace ThothTAS.Runtime {
public static partial class Hooks {
    static string pendingRestart;
    static bool suppressTrace;
    static int resetCount;
    static double initialPhysicsPhase;
    static int phaseAlignmentWait;
    static Dictionary<FieldInfo,object> staticBaseline;
    static System.Diagnostics.Stopwatch resetWatch;
    static readonly List<string> resetTimings=new List<string>();
    static void ResetTiming(string stage) {
        if(resetWatch!=null)resetTimings.Add(stage+"="+resetWatch.Elapsed.TotalMilliseconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture));
    }
    static void ResetReachedBaseline() {
        if(resetWatch==null)return;
        ResetTiming("baselineMs");
        eventLog.WriteLine("reset timing;"+string.Join(";",resetTimings.ToArray())+";fingerprint=held-read-locks;soundBanks=restore-initial-cache;coldEquivalent=false");
        resetWatch=null;resetTimings.Clear();
    }

    // This rebuilds scenes/pools and restores the audited sound-bank cache.
    // Unity's native clocks and
    // physics accumulator remain live; exact cold-process validation stays separate.
    static void RememberStaticBaseline() {
        if(staticBaseline!=null)return;
        initialPhysicsPhase=(double)Time.time-Time.fixedTime;
        staticBaseline=new Dictionary<FieldInfo,object>();
        captureOwnedResources=false;
        try {foreach(var type in Game.GetTypes()) {
            // InControl owns native worker threads and resets itself in OnDisable.
            // Its .cctor tables must not be nulled or restored with disposed devices.
            if(type.ContainsGenericParameters||type.Namespace=="InControl"||(type.Namespace!=null&&type.Namespace.StartsWith("InControl.",StringComparison.Ordinal)))continue;
            foreach(var field in type.GetFields(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)) {
                if(field.IsLiteral)continue;
                object value=field.GetValue(null);
                staticBaseline[field]=CopyInitial(value);
            }
        }} finally {captureOwnedResources=true;}
    }
    static bool ResetPhaseReady() {
        if(resetCount==0)return true;
        // 60 Hz input with 100 Hz physics has three accumulator phases. Wait
        // in the zero-input menu to match the observed initial loading phase.
        // This improves the room baseline; native absolute clocks stay live.
        double current=(double)Time.time-Time.fixedTime;
        if(Math.Abs(current-initialPhysicsPhase)<0.0005)return true;
        if(++phaseAlignmentWait<=30)return false;
        eventLog.WriteLine("warm phase alignment approximate;nativeTime="+F(Time.time)+";phase="+current.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+";coldPhase="+initialPhysicsPhase.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }
    static object CopyInitial(object value) {
        if(value==null)return null;
        var t=value.GetType();
        if(t.IsValueType||value is string)return value;
        if(value is Delegate)return null;
        var obj=value as UnityEngine.Object;
        if(obj!=null) {
            var component=obj as Component;var go=obj as GameObject;
            if(component!=null||go!=null)return null;
            return obj; // Material/texture assets are retained independently of scenes.
        }
        var array=value as Array;if(array!=null)return array.Clone();
        var list=value as IList;
        if(list!=null&&!list.IsReadOnly&&!list.IsFixedSize) {
            var copy=Activator.CreateInstance(t) as IList;
            if(copy!=null){foreach(var item in list)copy.Add(item);return copy;}
        }
        // The file-path helper has no scene ownership and its constructor runs
        // only once. Keep it so the recreated launcher can load the baseline save.
        if(t.FullName=="SaveDataHelper")return value;
        // Other mutable gameplay caches/singletons are rebuilt lazily.
        return null;
    }
    static void RestoreGameStatics() {
        foreach(var pair in staticBaseline) {
            if(pair.Key.IsInitOnly) {
                var target=pair.Key.GetValue(null) as IList;var original=pair.Value as IList;
                if(target!=null&&!target.IsReadOnly&&!target.IsFixedSize){target.Clear();if(original!=null)foreach(var item in original)target.Add(item);}
                continue;
            }
            pair.Key.SetValue(null,CopyInitial(pair.Value));
        }
    }
    static void CloseRunLogs() {
        Flush();
        var oldRandom=randomLog;var oldFrames=framesLog;var oldPhases=phaseLog;var oldEvents=eventLog;var oldReference=reference;
        // Clear shared references before disposing. A failed rebuild must still
        // report its first exception through TCP, rather than writing to a closed log.
        randomLog=null;framesLog=null;phaseLog=null;eventLog=null;reference=null;
        if(oldRandom!=null)oldRandom.Dispose();if(oldFrames!=null)oldFrames.Dispose();if(oldPhases!=null)oldPhases.Dispose();
        if(oldEvents!=null){oldEvents.Flush();oldEvents.Dispose();}
        if(oldReference!=null)oldReference.Dispose();
    }
    static void RestartAtBoundary(string cfgPath) {
        resetWatch=System.Diagnostics.Stopwatch.StartNew();resetTimings.Clear();
        cfgPath=Path.GetFullPath(cfgPath);
        string allowed=Path.GetFullPath(Path.Combine(Root,"work"))+Path.DirectorySeparatorChar;
        if(!cfgPath.StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new IOException("重置配置必须位于本项目 work 中。");
        var next=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string raw in File.ReadAllLines(cfgPath)){int p=raw.IndexOf('=');if(p>0)next[raw.Substring(0,p)]=raw.Substring(p+1);}
        if(!next.ContainsKey("token")||next["token"]!=C("token","")||next["port"]!=C("port","")||next["width"]!=C("width","1280")||next["height"]!=C("height","720"))throw new IOException("同进程重置不得改变控制连接或窗口尺寸。");
        string nextCommunity;next.TryGetValue("communityModPath",out nextCommunity);
        if((nextCommunity??"")!=C("communityModPath",""))throw new IOException("同进程重置不得切换社区 Mod；必须启动新进程。");
        string newRun=Path.GetFullPath(next["run"]);
        if(!newRun.StartsWith(allowed,StringComparison.OrdinalIgnoreCase)||newRun==run)throw new IOException("重置必须使用独立的新记录目录。");
        string scriptPath=Path.GetFullPath(next["script"]);
        if(!scriptPath.StartsWith(newRun+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("重置脚本必须属于新记录目录。");
        var nextScript=ScriptParser.Parse(File.ReadAllText(scriptPath,Encoding.UTF8),new HashSet<string>(File.ReadAllLines(Path.Combine(Root,"docs/levels.txt")),StringComparer.Ordinal));
        if(staticBaseline==null)throw new InvalidOperationException("首次关卡尚未建立，不能快速重置。");
        VerifyFingerprint();
        ResetTiming("verifiedMs");
        suppressTrace=true;
        try {
            CloseRunLogs();
            // Bind any rebuild error to the requested run even if scene cleanup
            // fails before its new writers are opened.
            run=newRun;
            // Destroy the active room first: its OnDestroy may consult music pools.
            var keep=UnityEngine.Object.FindObjectOfType(typeof(TasPlugin)) as TasPlugin;
            for(int pass=0;pass<3;pass++) {
                var unique=new HashSet<GameObject>();
                foreach(GameObject go in Resources.FindObjectsOfTypeAll(typeof(GameObject))) {
                    if(go==null||go.transform.parent!=null||!go.scene.IsValid()||cachedSoundRoots.Contains(go)||keep!=null&&go==keep.transform.root.gameObject)continue;
                    unique.Add(go);
                }
                // Unity 5.3 reports an invalid Scene for DontDestroyOnLoad roots.
                // Register original persistence calls rather than treating every
                // invalid-scene prefab asset as a runtime object to destroy.
                foreach(var go in persistentGameRoots)if(go!=null&&!cachedSoundRoots.Contains(go)&&(keep==null||go.transform.root.gameObject!=keep.transform.root.gameObject))unique.Add(go.transform.root.gameObject);
                var roots=new List<GameObject>(unique);
                roots.Sort(delegate(GameObject a,GameObject b){return (persistentGameRoots.Contains(a)?1:0).CompareTo(persistentGameRoots.Contains(b)?1:0);});
                if(roots.Count==0)break;
                foreach(var go in roots)if(go!=null)UnityEngine.Object.DestroyImmediate(go);
            }
            // Destroy changes the old Unity wrappers' native identity. Hash-based
            // removal is unreliable after that change; never reuse their registry.
            int survivingRoots=0;foreach(var go in persistentGameRoots)if(go!=null&&!cachedSoundRoots.Contains(go))survivingRoots++;
            persistentGameRoots.Clear();
            foreach(var go in cachedSoundRoots)if(go!=null)persistentGameRoots.Add(go);
            if(survivingRoots!=0)throw new InvalidOperationException("重置后仍有 "+survivingRoots+" 个旧持久游戏根存活。");
            ReleaseOwnedResources();
            ResetTiming("cleanupMs");
            RestoreGameStatics();
            RestoreSoundBanks();
            audioClocks.Clear();teleportBefore.Clear();
            config=next;script=nextScript;run=newRun;Directory.CreateDirectory(run);
            ResetCommunityTimers();
            error="";requested=false;baseline=false;complete=false;frame=-1;line=0;
            input=new TasFrameInput(0,0,AimKind.None,0,0);aimOrigin=Vector3.zero;aimResolved=Vector3.zero;
            ResetChapterPlayback();
            randomCount=0;phaseCount=0;phaseFrameCount=0;randomHash=14695981039346656037UL;phaseHash=14695981039346656037UL;phase="bootstrap";
            randomSite=null;randomApi=null;randomArgs=null;
            randomLog=Log("random.tsv");framesLog=Log("frames.jsonl");phaseLog=Log("phases.tsv");eventLog=Log("runtime-events.txt");eventLog.AutoFlush=true;
            randomLog.WriteLine("index\tframe\tphase\tsite\tapi\targuments\tresult");phaseLog.WriteLine("index\tframe\tcallback");
            if(C("reference","").Length>0)reference=new StreamReader(C("reference",""),Encoding.UTF8);
            lock(controlLock){paused=true;stepBudget=0;speed=double.Parse(C("speed","1"),System.Globalization.CultureInfo.InvariantCulture);}
            Time.timeScale=1;Time.fixedDeltaTime=0.01f;engineReady=false;resetCount++;
            phaseAlignmentWait=0;
            suppressTrace=false;EngineReady();
            eventLog.WriteLine("same-process reset="+resetCount+";pid="+System.Diagnostics.Process.GetCurrentProcess().Id+";nativeTime="+F(Time.time)+";nativeFixedTime="+F(Time.fixedTime)+";coldEquivalent=false");
            UpdateStatus("loading");Send(status);
            ResetTiming("preparedMs");
            eventLog.WriteLine("sound bank restored;roots="+cachedSoundRoots.Count+";transforms="+cachedSoundTransforms.Count+";audioSources="+cachedSoundAudio.Count+";selectors="+cachedSoundFields.Count);
            // Scene zero recreates RoomMusic, meshes and other gameplay roots.
            // Its loader finds the restored sound singleton and skips loading
            // thousands of already resident audio objects a second time.
            // Loading NewMenu directly would omit those destroyed persistent roots.
            SceneManager.LoadScene(0);
            ResetTiming("bootstrapLoadedMs");
        } finally {suppressTrace=false;}
    }
}
}
