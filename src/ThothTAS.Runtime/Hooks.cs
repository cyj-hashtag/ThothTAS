using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.SceneManagement;
using BepInEx;
using ThothTAS.Core;

namespace ThothTAS.Runtime {
[BepInPlugin("local.thoth.tas", "THOTH TAS", "0.1.0")]
public sealed class TasPlugin : BaseUnityPlugin {
    void Awake() {
        if(!Hooks.Enabled) return;
        try { Hooks.Ensure(); DontDestroyOnLoad(gameObject); }
        catch(Exception e) { Hooks.Fail(e); }
    }
    void Start() {if(Hooks.Enabled)StartCoroutine(Loop());}
    IEnumerator Loop() {
        while(true) { yield return new WaitForEndOfFrame(); try { Hooks.EndFrame(); } catch(Exception e) { Hooks.Fail(e); Hooks.Hold(); } }
    }
}
public static partial class Hooks {
    public static bool Enabled {get {return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("THOTH_TAS_SESSION"));}}
    static bool initialized,engineReady,baseline,requested,quit,complete;
    static volatile bool paused,connected;
    static int stepBudget,frame=-1,line;
    static double speed=1;
    static Dictionary<string,string> config;
    static TasScript script;
    static TasFrameInput input=new TasFrameInput(0,0,AimKind.None,0,0);
    static StreamWriter randomLog,framesLog,phaseLog,eventLog;
    static StreamReader reference;
    static string phase="bootstrap",randomSite,randomApi,randomArgs,run,error="",reportedState="loading";
    static long randomCount,phaseCount,phaseFrameCount;
    static ulong randomHash=14695981039346656037UL,phaseHash=14695981039346656037UL;
    static readonly object controlLock=new object(),sendLock=new object();
    static StreamWriter clientWriter;
    static TcpListener listener;
    static string status="{\"protocol\":1,\"type\":\"status\",\"state\":\"loading\",\"frame\":-1}";
    static Assembly gameAssembly;
    static Stopwatch pace=Stopwatch.StartNew();
    static Vector3 aimOrigin,aimResolved;
    static double releasedAt;
    static BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static string Root {get {return Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));}}
    static string C(string name,string fallback) {string v;return config.TryGetValue(name,out v)?v:fallback;}
    public static void Ensure() {
        if(initialized) return;
        initialized=true;
        config=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string raw in File.ReadAllLines(Environment.GetEnvironmentVariable("THOTH_TAS_SESSION"))) {int p=raw.IndexOf('=');if(p>0)config[raw.Substring(0,p)]=raw.Substring(p+1);}
        run=C("run",Path.Combine(Root,"work/run"));Directory.CreateDirectory(run);
        eventLog=Log("runtime-events.txt");eventLog.AutoFlush=true;
        VerifyFingerprint();
        var known=new HashSet<string>(File.ReadAllLines(Path.Combine(Root,"docs/levels.txt")),StringComparer.Ordinal);
        script=ScriptParser.Parse(File.ReadAllText(C("script",""),Encoding.UTF8),known);
        if(C("reference","").Length>0)reference=new StreamReader(C("reference",""),Encoding.UTF8);
        randomLog=Log("random.tsv");framesLog=Log("frames.jsonl");phaseLog=Log("phases.tsv");
        randomLog.WriteLine("index\tframe\tphase\tsite\tapi\targuments\tresult");
        phaseLog.WriteLine("index\tframe\tcallback");
        Application.runInBackground=true;
        paused=C("autoplay","false")!="true";
        speed=double.Parse(C("speed","1"),CultureInfo.InvariantCulture);
        listener=new TcpListener(IPAddress.Loopback,int.Parse(C("port","51062"),CultureInfo.InvariantCulture));listener.Start();
        var thread=new Thread(NetworkLoop);thread.IsBackground=true;thread.Name="THOTH TAS control";thread.Start();
        eventLog.WriteLine("protocol=1;seed="+script.Seed+";unity="+Application.unityVersion+";pid="+Process.GetCurrentProcess().Id+";exe="+Process.GetCurrentProcess().MainModule.FileName);
    }
    static StreamWriter Log(string name){return new StreamWriter(new FileStream(Path.Combine(run,name),FileMode.Create,FileAccess.Write,FileShare.ReadWrite),new UTF8Encoding(false));}
    static void EngineReady() {
        if(engineReady)return;engineReady=true;
        Time.captureFramerate=60;QualitySettings.vSyncCount=0;Application.targetFrameRate=-1;
        UnityEngine.Random.seed=script.Seed;
        eventLog.WriteLine("engine initialized before first game callback/random;captureFramerate="+Time.captureFramerate+";fixedDeltaTime="+F(Time.fixedDeltaTime));
    }
    static void VerifyFingerprint() {
        string game=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
        foreach(string s in File.ReadAllLines(Path.Combine(Root,"docs/game-fingerprint.tsv"))) {
            string[] a=s.Split('\t');if(a.Length!=2)continue;
            string p=Path.GetFullPath(Path.Combine(game,a[0]));if(!p.StartsWith(game+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe fingerprint path");
            using(var sha=System.Security.Cryptography.SHA256.Create()) using(var f=File.OpenRead(p)) {string h=BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant();if(h!=a[1])throw new IOException("游戏版本指纹不匹配: "+a[0]);}
        }
    }
    public static void SetResolution(int w,int h,bool full) {Ensure();Screen.SetResolution(int.Parse(C("width","1280")),int.Parse(C("height","720")),false);}
    // LaserBeat uses a muted AudioSource as a gameplay timer. Device time is a
    // wall clock even under captureFramerate; virtualize only that audited path.
    sealed class AudioClock {public int startedFrame;public bool playing,observed;public float pitch;}
    static Dictionary<AudioSource,AudioClock> audioClocks=new Dictionary<AudioSource,AudioClock>();
    static AudioClock Clock(AudioSource source) {
        if(source==null)throw new ArgumentNullException("source");
        AudioClock clock;
        if(!audioClocks.TryGetValue(source,out clock)) {
            clock=new AudioClock();clock.startedFrame=Time.frameCount;clock.pitch=source.pitch;
            // Audited laser timer resources have playOnAwake=false.
            audioClocks.Add(source,clock);
        }
        return clock;
    }
    public static void AudioPlay(AudioSource source) {
        var clock=Clock(source);clock.startedFrame=Time.frameCount;clock.pitch=source.pitch;clock.playing=true;
        source.Play();
        eventLog.WriteLine("audio play;frame="+(baseline?frame+1:-1)+";source="+PathOf(source.transform)+";pitch="+F(clock.pitch));
    }
    public static void AudioStop(AudioSource source) {
        var clock=Clock(source);clock.playing=false;clock.startedFrame=Time.frameCount;source.Stop();
    }
    static float ClockTime(AudioSource source,AudioClock clock) {
        if(!clock.playing||source.clip==null)return 0;
        float elapsed=(float)((Time.frameCount-clock.startedFrame)*(double)clock.pitch/60.0);
        if(source.loop&&source.clip.length>0)return elapsed%source.clip.length;
        return elapsed>=source.clip.length?0:elapsed;
    }
    public static float AudioTime(AudioSource source) {var clock=Clock(source);clock.observed=true;return ClockTime(source,clock);}
    public static bool AudioIsPlaying(AudioSource source) {
        var clock=Clock(source);clock.observed=true;
        return clock.playing&&source.clip!=null&&(source.loop||(Time.frameCount-clock.startedFrame)*(double)clock.pitch/60.0<source.clip.length);
    }
    public static void BeforeRandom(string site,string api,object[] args) {
        if(suppressTrace)return;
        Ensure();EngineReady();randomSite=site;randomApi=api;randomArgs=Values(args);
    }
    public static void AfterRandom(object value) {
        if(suppressTrace)return;
        string record=(++randomCount)+"\t"+(baseline?frame+1:-1)+"\t"+phase+"\t"+randomSite+"\t"+randomApi+"\t"+randomArgs+"\t"+Value(value);
        randomLog.WriteLine(record);randomHash=Hash(randomHash,record);
    }
    public static void Phase(string name) {
        if(suppressTrace)return;
        Ensure();EngineReady();phase=name;phaseCount++;phaseFrameCount++;phaseHash=Hash(phaseHash,name);
        phaseLog.WriteLine(phaseCount+"\t"+(baseline?frame+1:-1)+"\t"+name);
    }
    public static float ReadInput(string name,int player) {
        if(!baseline||player!=0)return 0;
        if(name.IndexOf("MoveStickX",StringComparison.Ordinal)>=0)return (float)input.MoveX;
        if(name.IndexOf("MoveStickY",StringComparison.Ordinal)>=0)return (float)input.MoveY;
        double x,y;var p=GetGlobal("player") as Component;Vector3 pos=p==null?Vector3.zero:p.transform.position;
        if(p!=null) {var aim=p.GetComponent("PlayerAim");if(aim!=null) {var overridePos=Get(aim,"overrideShootPos") as Transform;if(overridePos!=null)pos=overridePos.position;}}
        input.ResolveAim(pos.x,pos.y,out x,out y);aimOrigin=pos;aimResolved=new Vector3((float)x,(float)y,0);
        if(name=="GetAimStickX")return (float)x;if(name=="GetAimStickY")return (float)y;return 0;
    }
    static Assembly Game {get {if(gameAssembly==null)foreach(var a in AppDomain.CurrentDomain.GetAssemblies())if(a.GetName().Name=="Assembly-CSharp"){gameAssembly=a;break;}return gameAssembly;}}
    static Type GT(string type){return Game.GetType(type,true);}
    static object Get(object obj,string name) {if(obj==null)return null;var t=obj as Type??obj.GetType();var f=t.GetField(name,flags);return f==null?null:f.GetValue(obj is Type?null:obj);}
    static void Set(Type type,string name,object value) {type.GetField(name,flags).SetValue(null,value);}
    static object GetGlobal(string name){return Get(GT("Globals"),name);}
    static Dictionary<object,Vector3> teleportBefore=new Dictionary<object,Vector3>();
    public static void BeforeTeleport(object teleporter) {
        var player=GetGlobal("player") as Component;
        teleportBefore[teleporter]=player==null?Vector3.zero:player.transform.position;
    }
    public static void AfterTeleport(object teleporter) {
        var component=teleporter as Component;var player=GetGlobal("player") as Component;
        Vector3 before;teleportBefore.TryGetValue(teleporter,out before);teleportBefore.Remove(teleporter);
        Vector3 after=player==null?Vector3.zero:player.transform.position;
        eventLog.WriteLine("teleport callback;frame="+(baseline?frame+1:-1)+";source="+(component==null?"null":PathOf(component.transform))+";active="+Value(Get(teleporter,"teleporterActive"))+";player1Flag="+Value(Get(GT("Teleporter"),"player1TeleportedThisFrame"))+";before="+Value(before)+";after="+Value(after));
    }
    static bool CallBool(object obj,string name){return obj!=null&&(bool)obj.GetType().GetMethod(name,flags).Invoke(obj,null);}
    static void StartScene() {
        RememberStaticBaseline();
        var globals=GetGlobal("instance");var names=(IList)Get(globals,"levelNames");
        int dash=script.Start.LastIndexOf('-');string family=script.Start.Substring(0,dash);int room=int.Parse(script.Start.Substring(dash+1));int group=-1;
        for(int i=0;i<16&&i<names.Count;i++)if(string.Equals((string)names[i],family,StringComparison.OrdinalIgnoreCase)){group=i;break;}
        if(group<0)throw new InvalidOperationException("普通关卡未在 Globals.levelNames 中找到: "+family);
        Set(GT("Globals"),"currentLevelID",group);Set(GT("Globals"),"currentLevelName",family);Set(GT("Globals"),"currentGlobalRoomID",group*4+room-1);
        Set(GT("Room"),"coopRoom",false);Set(GT("Room"),"doSlideAtStart",false);Set(GT("HardcoreArena"),"hardcoreModeActive",false);
        Set(GT("PlayerInput"),"keyboardControlEnabled",false);Set(GT("PlayerInput"),"cursorActive",false);Set(GT("PlayerInput"),"lockPlayer1ToKeyboardAndMouse",false);
        var rm=GT("RoomMusic").GetMethod("GetInstance",flags).Invoke(null,null);if(rm!=null)rm.GetType().GetMethod("StartRoomSound",flags).Invoke(rm,null);
        requested=true;eventLog.WriteLine("requested scene="+script.Start);SceneManager.LoadScene(script.Start);
    }
    public static void EndFrame() {
        Ensure();if(error.Length>0){Boundary();return;}
        if(!requested) {
            if(SceneManager.GetActiveScene().name=="NewMenu" && GetGlobal("instance")!=null && Get(GT("LoadSoundBank"),"TrailsBank")!=null&&ResetPhaseReady())StartScene();
            return;
        }
        if(!baseline) {
            var room=GetGlobal("currentRoom");var player=GetGlobal("player") as Component;
            if(SceneManager.GetActiveScene().name!=script.Start||room==null||player==null||!player.gameObject.activeInHierarchy||!CallBool(room,"GetPlayersAreActive"))return;
            baseline=true;frame=0;line=0;eventLog.WriteLine("baseline;nativeFrame="+Time.frameCount+";randomCount="+randomCount+";time="+F(Time.time)+";fixedTime="+F(Time.fixedTime));RecordResources();
        } else if(!complete){frame++;if(chapterInputIssued)chapterFrame++;}
        LockWindow();
        if(!complete) {
            ObserveRoomEvents();
            bool chapterDone=AdvanceChapter();
            Capture();
            if(chapterDone||!script.HasExplicitRooms&&frame>=script.TotalFrames){complete=true;paused=true;Flush();eventLog.WriteLine("complete;frames="+frame+";randomCount="+randomCount);}
            if(!complete&&script.HasExplicitRooms&&!chapterWaiting&&chapterFrame>=script.Chapters[chapterIndex].TotalFrames)throw new InvalidOperationException("关卡 "+script.Chapters[chapterIndex].Scene+" 的输入已用尽但尚未通过；请从前序连续回放编辑本关，不能套用下一关输入。");
            UpdateStatus(complete?"complete":paused?"paused":"running");Send(status);
        }
        Boundary();
        if(baseline&&!complete&&!quit)PrepareNextInput();
    }
    public static void Hold(){Boundary();}
    static void Flush(){if(randomLog!=null)randomLog.Flush();if(framesLog!=null)framesLog.Flush();if(phaseLog!=null)phaseLog.Flush();}
    static void Boundary() {
        Flush();
        while(true) {
            string restart=null;
            bool proceed=false;
            lock(controlLock){if(quit)break;if(pendingRestart!=null){restart=pendingRestart;pendingRestart=null;}else if(!complete&&error.Length==0&&connected){if(!paused)proceed=true;else if(stepBudget>0){stepBudget--;proceed=true;}}}
            if(restart!=null){try{RestartAtBoundary(restart);}catch(Exception e){Fail(e);}return;}
            if(proceed)break;
            if(!complete&&error.Length==0&&reportedState!="paused"){UpdateStatus("paused");Send(status);}
            Pump();Thread.Sleep(5);
        }
        if(quit){Flush();Application.Quit();return;}
        double duration=1.0/(60*speed);
        while(pace.Elapsed.TotalSeconds-releasedAt<duration){Pump();Thread.Sleep(1);}
        releasedAt=pace.Elapsed.TotalSeconds;
        phaseFrameCount=0;phaseHash=14695981039346656037UL;
    }
    static void UpdateStatus(string state) {
        reportedState=state;
        var p=GetGlobal("player") as Component;var pos=p==null?Vector3.zero:p.transform.position;
        status="{\"protocol\":1,\"type\":\"status\",\"state\":"+Q(state)+",\"run\":"+Q(run)+",\"frame\":"+frame+",\"line\":"+line+",\"scene\":"+Q(SceneManager.GetActiveScene().name)+",\"chapter\":"+chapterIndex+",\"chapterScene\":"+Q(script.Chapters[chapterIndex].Scene)+",\"chapterFrame\":"+(script.HasExplicitRooms?chapterFrame:Math.Max(frame,0))+",\"segmentFrame\":"+segmentFrame+",\"segmentFrames\":"+segmentFrames+",\"roomEvents\":"+RoomEventsJson()+",\"resetCount\":"+resetCount+",\"coldProcess\":"+(resetCount==0?"true":"false")+",\"x\":"+F(pos.x)+",\"y\":"+F(pos.y)+",\"randomCount\":"+randomCount+",\"randomHash\":"+Q(randomHash.ToString("X16"))+",\"input\":"+Q(Values(new object[]{input.MoveX,input.MoveY,input.AimKind,input.AimX,input.AimY}))+",\"error\":"+Q(error)+"}";
    }
    static void NetworkLoop() {
        while(!quit) {
            try {using(var client=listener.AcceptTcpClient()) {
                client.NoDelay=true;using(var reader=new StreamReader(client.GetStream(),Encoding.UTF8)) using(var writer=new StreamWriter(client.GetStream(),new UTF8Encoding(false))) {
                    writer.AutoFlush=true;string hello=reader.ReadLine();
                    if(hello!="HELLO\t1\t"+C("token","")){writer.WriteLine("{\"protocol\":1,\"type\":\"error\",\"error\":\"协议版本或令牌不匹配\"}");continue;}
                    lock(sendLock){clientWriter=writer;connected=true;writer.WriteLine("{\"protocol\":1,\"type\":\"hello\"}");writer.WriteLine(status);}
                    string command;while((command=reader.ReadLine())!=null) {
                        lock(controlLock) {
                            if(command=="PLAY"&&!complete){paused=false;}
                            else if(command=="PAUSE"){paused=true;}
                            else if(command=="STEP"&&!complete){paused=true;stepBudget++;}
                            else if(command=="STOP"){quit=true;}
                            else if(command.StartsWith("RESTART\t",StringComparison.Ordinal)){paused=true;stepBudget=0;pendingRestart=command.Substring(8);}
                            else if(command.StartsWith("SPEED\t")){double s;if(double.TryParse(command.Substring(6),NumberStyles.Float,CultureInfo.InvariantCulture,out s)&&(s==0.5||s==1||s==2))speed=s;}
                        }
                        if(command=="STATUS")Send(status);
                    }
                }
            }} catch(Exception e){if(eventLog!=null)eventLog.WriteLine("TCP: "+e.Message);}
            lock(controlLock){connected=false;paused=true;stepBudget=0;}
            lock(sendLock){clientWriter=null;}
        }
    }
    static void Send(string s){lock(sendLock){try{if(clientWriter!=null)clientWriter.WriteLine(s);}catch{connected=false;paused=true;}}}
    public static void Fail(Exception e) {
        error=e.ToString();paused=true;if(eventLog!=null)eventLog.WriteLine(error);UnityEngine.Debug.LogError("THOTH TAS: "+error);
        status="{\"protocol\":1,\"type\":\"error\",\"state\":\"error\",\"run\":"+Q(run)+",\"frame\":"+frame+",\"line\":"+line+",\"chapter\":"+chapterIndex+",\"error\":"+Q(error)+"}";Send(status);
    }
    static bool Gameplay(Type t) {
        if(t.Assembly.GetName().Name!="Assembly-CSharp")return false;
        string[] names={"Player","PlayerAim","Room","Receiver","ShadowBouncer","Bouncer","Teleporter","DynamicCube","Bullet","DeathSwipeManager","HardcoreArena","OverlapPlane","HitBox","EvilPlayerBouncer","Wall","Scaler","Grower","Seeker","Spinner","Boss","BigBoss","FinalBoss","Lava","Laser","RhythmLaser","Enemy","EnemyBullet","Spawner","Destroyer","PlayerDeathEffect","LineMover","LineRotater","LineToggler","Mover","FireLineMover","Flower","Ghost","Killer","Reflector","SphereRotater","TeleportFollow","BeatMaster","BeatListener"};
        while(t!=null&&t!=typeof(MonoBehaviour)) {foreach(string n in names)if(t.Name==n||t.Name.StartsWith(n,StringComparison.Ordinal))return true;t=t.BaseType;}return false;
    }
    static Dictionary<Type,FieldInfo[]> fieldCache=new Dictionary<Type,FieldInfo[]>();
    static void Capture() {
        var map=new SortedDictionary<string,string>(StringComparer.Ordinal);
        foreach(var obj in UnityEngine.Object.FindObjectsOfType(typeof(MonoBehaviour))) {
            var c=obj as MonoBehaviour;if(c==null||!Gameplay(c.GetType()))continue;
            string key=PathOf(c.transform)+"/"+c.GetType().FullName;
            var same=c.GetComponents(c.GetType());int index=Array.IndexOf(same,c);if(index>0)key+="#"+index;
            map[key+".enabled"]=Value(c.enabled);map[key+".position"]=Value(c.transform.position);map[key+".rotation"]=Value(c.transform.rotation);map[key+".scale"]=Value(c.transform.localScale);
            var rb=c.GetComponent<Rigidbody>();if(rb!=null){map[key+".rb.position"]=Value(rb.position);map[key+".rb.velocity"]=Value(rb.velocity);map[key+".rb.angularVelocity"]=Value(rb.angularVelocity);map[key+".rb.kinematic"]=Value(rb.isKinematic);map[key+".rb.sleep"]=Value(rb.IsSleeping());}
            var coll=c.GetComponent<Collider>();if(coll!=null){map[key+".coll.enabled"]=Value(coll.enabled);map[key+".coll.trigger"]=Value(coll.isTrigger);}
            FieldInfo[] fields;if(!fieldCache.TryGetValue(c.GetType(),out fields)) {
                var fs=new List<FieldInfo>();for(Type t=c.GetType();t!=null&&t!=typeof(MonoBehaviour);t=t.BaseType)fs.AddRange(t.GetFields(flags|BindingFlags.DeclaredOnly));fields=fs.ToArray();fieldCache[c.GetType()]=fields;
            }
            foreach(var f in fields) {if(f.IsStatic)continue;object v=f.GetValue(c);string val=FieldValue(v,0);if(val!=null)map[key+"."+f.DeclaringType.Name+"."+f.Name]=val;}
        }
        foreach(string n in new[]{"currentLevelID","currentLevelName","currentGlobalRoomID","currentLevelSize","save_progress"})map["Globals."+n]=Value(GetGlobal(n));
        map["Time.timeScale"]=Value(Time.timeScale);map["Time.fixedDeltaTime"]=Value(Time.fixedDeltaTime);map["Time.deltaTime"]=Value(Time.deltaTime);map["Time.fixedTime"]=Value(Time.fixedTime);map["Time.time"]=Value(Time.time);
        map["Input.moveX"]=Value(input.MoveX);map["Input.moveY"]=Value(input.MoveY);map["Input.aimKind"]=Value(input.AimKind);map["Input.aimX"]=Value(input.AimX);map["Input.aimY"]=Value(input.AimY);
        map["Input.shootPosition"]=Value(aimOrigin);map["Input.resolvedAim"]=Value(aimResolved);
        foreach(var pair in audioClocks) {
            var source=pair.Key;if(source==null||!pair.Value.observed)continue;
            string key="AudioClock."+PathOf(source.transform);
            map[key+".time"]=Value(ClockTime(source,pair.Value));map[key+".playing"]=Value(AudioIsPlaying(source));
            map[key+".pitch"]=Value(pair.Value.pitch);map[key+".clipLength"]=Value(source.clip==null?0:source.clip.length);
        }
        var sb=new StringBuilder("{\"frame\":");sb.Append(frame).Append(",\"line\":").Append(line).Append(",\"scene\":").Append(Q(SceneManager.GetActiveScene().name)).Append(",\"chapter\":").Append(chapterIndex).Append(",\"chapterFrame\":").Append(script.HasExplicitRooms?chapterFrame:Math.Max(frame,0)).Append(",\"segmentFrame\":").Append(segmentFrame).Append(",\"segmentFrames\":").Append(segmentFrames).Append(",\"randomCount\":").Append(randomCount).Append(",\"randomHash\":").Append(Q(randomHash.ToString("X16"))).Append(",\"phases\":{\"count\":").Append(phaseFrameCount).Append(",\"hash\":").Append(Q(phaseHash.ToString("X16"))).Append("},\"fields\":{");
        bool first=true;foreach(var kv in map){if(!first)sb.Append(',');first=false;sb.Append(Q(kv.Key)).Append(':').Append(Q(kv.Value));}sb.Append("}}");string actual=sb.ToString();framesLog.WriteLine(actual);
        if(reference!=null) {
            string expected=reference.ReadLine();
            if(expected!=actual) {
                string field="$record",ev=expected??"missing",av=actual;
                if(expected!=null) {
                    var a=JsonFields(expected);var b=JsonFields(actual);var keys=new SortedDictionary<string,bool>(StringComparer.Ordinal);foreach(var k in a.Keys)keys[k]=true;foreach(var k in b.Keys)keys[k]=true;
                    foreach(var k in keys.Keys){string v1,v2;a.TryGetValue(k,out v1);b.TryGetValue(k,out v2);if(v1!=v2){field=k;ev=v1??"missing";av=v2??"missing";break;}}
                }
                File.WriteAllText(Path.Combine(run,"discrepancy.json"),"{\"frame\":"+frame+",\"field\":"+Q(field)+",\"expected\":"+Q(ev)+",\"actual\":"+Q(av)+"}");
                Flush();throw new InvalidOperationException("失步：帧 "+frame+"，"+field+"，期望 "+ev+"，实际 "+av);
            }
        }
    }
    static Dictionary<string,string> JsonFields(string json) {
        var d=new Dictionary<string,string>(StringComparer.Ordinal);
        var matches=System.Text.RegularExpressions.Regex.Matches(json,"\\\"((?:\\\\.|[^\\\"\\\\])*)\\\"\\s*:\\s*(\\\"(?:\\\\.|[^\\\"\\\\])*\\\"|-?[0-9]+)");
        foreach(System.Text.RegularExpressions.Match match in matches)d[match.Groups[1].Value]=match.Groups[2].Value;
        return d;
    }
    static string FieldValue(object v,int depth) {
        if(v==null)return "null";var t=v.GetType();if(t.IsPrimitive||t.IsEnum||v is string||v is Vector2||v is Vector3||v is Quaternion||v is Color)return Value(v);
        var c=v as Component;if(c!=null)return PathOf(c.transform)+"/"+c.GetType().Name;
        var g=v as GameObject;if(g!=null)return PathOf(g.transform);
        var list=v as IList;if(list!=null&&depth<1) {var sb=new StringBuilder("[");foreach(var i in list){string s=FieldValue(i,depth+1);if(s==null)return null;sb.Append(s).Append(';');}return sb.Append(']').ToString();}
        return null;
    }
    static string PathOf(Transform t) {var s=new List<string>();while(t!=null){s.Add(t.name+"["+t.GetSiblingIndex()+"]");t=t.parent;}s.Reverse();return string.Join("/",s.ToArray());}
    static string Values(object[] vs){var s=new List<string>();foreach(var v in vs)s.Add(Value(v));return string.Join(",",s.ToArray());}
    static string Value(object v) {
        if(v==null)return "null";if(v is float){float f=(float)v;return F(f)+"@"+BitConverter.ToInt32(BitConverter.GetBytes(f),0).ToString("X8");}
        if(v is double)return ((double)v).ToString("R",CultureInfo.InvariantCulture);
        if(v is Vector3){Vector3 x=(Vector3)v;return Values(new object[]{x.x,x.y,x.z});}if(v is Vector2){Vector2 x=(Vector2)v;return Values(new object[]{x.x,x.y});}
        if(v is Quaternion){Quaternion x=(Quaternion)v;return Values(new object[]{x.x,x.y,x.z,x.w});}if(v is Color){Color x=(Color)v;return Values(new object[]{x.r,x.g,x.b,x.a});}
        return Convert.ToString(v,CultureInfo.InvariantCulture).Replace("\t"," ").Replace("\n"," ").Replace("\r"," ");
    }
    static ulong Hash(ulong h,string s){foreach(byte b in Encoding.UTF8.GetBytes(s)){h^=b;h*=1099511628211UL;}return h;}
    static string F(float f){return float.IsNaN(f)||float.IsInfinity(f)?"0":f.ToString("R",CultureInfo.InvariantCulture);}
    static string Q(string s){if(s==null)return "null";return "\""+s.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n").Replace("\t","\\t")+"\"";}
    [StructLayout(LayoutKind.Sequential)]struct RECT {public int left,top,right,bottom;}
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")]static extern int GetWindowLong(IntPtr window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW")]static extern int SetWindowLong(IntPtr window,int index,int value);
    [DllImport("user32.dll")]static extern bool AdjustWindowRect(ref RECT rect,int style,bool menu);
    [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    delegate bool EnumWindowProc(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")]static extern bool EnumWindows(EnumWindowProc callback,IntPtr parameter);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
    static IntPtr gameWindow;
    static void LockWindow() {
        // Unity's old Mono implementation returns zero for MainWindowHandle.
        // Enumerate visible top-level windows and accept only this process's captioned window.
        if(gameWindow==IntPtr.Zero) {
            uint ownedPid=(uint)Process.GetCurrentProcess().Id;
            EnumWindows(delegate(IntPtr candidate,IntPtr parameter){uint pid;GetWindowThreadProcessId(candidate,out pid);if(pid==ownedPid&&IsWindowVisible(candidate)&&(GetWindowLong(candidate,-16)&0x00C00000)==0x00C00000){gameWindow=candidate;return false;}return true;},IntPtr.Zero);
        }
        IntPtr window=gameWindow;if(window==IntPtr.Zero)throw new InvalidOperationException("未找到本副本的可见窗口");
        int style=GetWindowLong(window,-16),fixedStyle=style&~0x00050000; // size border + maximize box
        if(style==fixedStyle)return;
        SetWindowLong(window,-16,fixedStyle);
        var rect=new RECT();rect.right=int.Parse(C("width","1280"));rect.bottom=int.Parse(C("height","720"));
        if(!AdjustWindowRect(ref rect,fixedStyle,false)||!SetWindowPos(window,IntPtr.Zero,0,0,rect.right-rect.left,rect.bottom-rect.top,0x36))throw new InvalidOperationException("无法固定副本窗口尺寸");
        eventLog.WriteLine("window fixed;frame="+frame+";style="+fixedStyle.ToString("X8")+";client="+C("width","1280")+"x"+C("height","720"));
    }
    [StructLayout(LayoutKind.Sequential)]struct MSG {public IntPtr hwnd;public uint message;public UIntPtr wParam;public IntPtr lParam;public uint time;public int x,y;}
    [DllImport("user32.dll")]static extern bool PeekMessage(out MSG m,IntPtr hwnd,uint min,uint max,uint remove);
    [DllImport("user32.dll")]static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")]static extern IntPtr DispatchMessage(ref MSG m);
    static void Pump(){MSG m;int i=0;while(i++<30&&PeekMessage(out m,IntPtr.Zero,0,0,1)){TranslateMessage(ref m);DispatchMessage(ref m);}}
}
}
