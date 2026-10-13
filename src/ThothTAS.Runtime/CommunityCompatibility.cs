using System;
using System.Collections.Generic;
using System.IO;

namespace ThothTAS.Runtime {
public static partial class Hooks {
    const string CommunityHash="2A35DDEA8A48144E926AC33796FFD63267A5AC129FD2495EF7F10A7D893E33FD";
    sealed class CommunityClock {public long elapsedFrames; public int startedFrame; public bool running;}
    static readonly Dictionary<object,CommunityClock> communityClocks=new Dictionary<object,CommunityClock>();
    static bool CommunityEnabled {get{return C("communityModPath","").Length>0;}}
    static CommunityClock CommunityClockOf(object watch) {
        if(watch==null)throw new ArgumentNullException("watch");
        CommunityClock clock;
        if(!communityClocks.TryGetValue(watch,out clock)){clock=new CommunityClock();communityClocks.Add(watch,clock);}
        return clock;
    }
    static long CommunityFrames(CommunityClock clock) {return clock.elapsedFrames+(clock.running?Math.Max(0,UnityEngine.Time.frameCount-clock.startedFrame):0);}
    public static void CommunityTimerStart(object watch) {
        Ensure();var clock=CommunityClockOf(watch);if(clock.running)return;
        clock.startedFrame=UnityEngine.Time.frameCount;clock.running=true;
    }
    public static void CommunityTimerStop(object watch) {
        Ensure();var clock=CommunityClockOf(watch);if(!clock.running)return;
        clock.elapsedFrames=CommunityFrames(clock);clock.running=false;
    }
    public static TimeSpan CommunityTimerElapsed(object watch) {
        Ensure();return TimeSpan.FromTicks(CommunityFrames(CommunityClockOf(watch))*TimeSpan.TicksPerSecond/60);
    }
    static void ResetCommunityTimers() {communityClocks.Clear();}
    public static string CommunityFormatTime(TimeSpan time) {
        string tail=time.Seconds.ToString("00")+"."+(time.Milliseconds/10).ToString("00");
        if(time.Hours>0)return time.Hours+":"+time.Minutes.ToString("00")+":"+tail;
        if(time.Minutes>0)return time.Minutes+":"+tail;
        return time.Seconds+"."+(time.Milliseconds/10).ToString("00");
    }
    public static void CommunityLauncherStart() {
        Ensure();
        // Preserve the existing copy's quality settings without rewriting the
        // incompatible eleven-line settings format used by the 2018 mod.
        string[] settings=File.Exists("settings.txt")?File.ReadAllLines("settings.txt"):new string[0];
        bool vsync=settings.Length>2&&settings[2]=="1";
        bool aa=settings.Length>4&&settings[4]=="1";
        bool cheap=settings.Length>6&&settings[6]=="1";
        Set(GT("OverlapPlane"),"useExpensiveLava",!cheap);
        if(cheap)UnityEngine.SceneManagement.SceneManager.LoadSceneAsync("LoadTilingTextureBank",UnityEngine.SceneManagement.LoadSceneMode.Additive);
        UnityEngine.QualitySettings.SetQualityLevel(aa?(vsync?3:2):(vsync?1:0));
        UnityEngine.Application.runInBackground=true;
        SetResolution(1280,720,false);
        Set(GT("Globals"),"showRoomDebugInfo",false);
        UnityEngine.Cursor.visible=false;
    }
    static void InitializeCommunityTimers() {
        if(!CommunityEnabled)return;
        var timers=GT("SpeedrunTimers");
        if(timers==null)throw new InvalidOperationException("Community SpeedrunTimers is absent from the loaded assembly.");
        Set(timers,"showRealTimeAndILTime",true);
        eventLog.WriteLine("community-mod=v2.8;assemblySha256="+CommunityHash+";timers=input-frame-clock;IL-timers=visible;physical-cheat-keys=blocked");
    }
    static void AppendCommunityTimerState(SortedDictionary<string,string> map) {
        if(!CommunityEnabled)return;
        map["CommunityMod.profile"]="rmadlal/v2.8";
        var timers=GetGlobal("speedrunTimers");
        if(timers==null){map["CommunityMod.timers"]="null";return;}
        foreach(string name in new[]{"realTimeTimer","loadlessTimer","inGameTime"}) {
            var watch=Get(timers,name);
            if(watch==null){map["CommunityMod."+name]="null";continue;}
            var clock=CommunityClockOf(watch);
            map["CommunityMod."+name+".frames"]=CommunityFrames(clock).ToString(System.Globalization.CultureInfo.InvariantCulture);
            map["CommunityMod."+name+".running"]=clock.running?"true":"false";
        }
        map["CommunityMod.prevRoomTime.ticks"]=((TimeSpan)Get(timers,"prevRoomTime")).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        map["CommunityMod.roomStartedTime.ticks"]=((TimeSpan)Get(GT("Room"),"roomStartedTime")).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        map["CommunityMod.ILVisible"]=Value(Get(GT("SpeedrunTimers"),"showRealTimeAndILTime"));
    }
}
}
