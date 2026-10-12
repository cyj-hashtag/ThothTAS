using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThothTAS.Core;
using ThothTAS.Host;

string Option(string name,string fallback) {var index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:fallback;}
string root=ProjectFiles.FindRoot(Option("--root",@"D:\Workspace4All\ThothTAS"));
string command=args.Length==0?"all":args[0];
string output=Path.GetFullPath(Option("--out",Path.Combine(root,"work","acceptance","editing-v2","feature-console")));
Directory.CreateDirectory(output);
var cases=new List<object>();
var runs=new List<string>();
bool passed=true;
DateTime started=DateTime.UtcNow;
void Require(bool condition,string description){if(!condition)throw new InvalidDataException(description);Console.WriteLine("通过："+description);}
Dictionary<string,string> Fields(string directory) {
    using var file=new FileStream(Path.Combine(directory,"frames.jsonl"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
    using var reader=new StreamReader(file);using var json=JsonDocument.Parse(reader.ReadLine()??throw new InvalidDataException("没有实际基线帧"));
    return json.RootElement.GetProperty("fields").EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.GetString()!,StringComparer.Ordinal);
}
async Task<string> Hash(string path){using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);return Convert.ToHexString(await SHA256.HashDataAsync(f));}
async Task Execute(string name,Func<Task<object>> body) {
    try{var evidence=await body();cases.Add(new{name,passed=true,evidence});}
    catch(Exception e){passed=false;cases.Add(new{name,passed=false,error=e.ToString()});Console.WriteLine(e);}
}
if(command is "warm" or "all")await Execute("同一实际游戏进程重建关卡、草稿快照和段内进度",async()=>{
    string source="Version,1\nStart,Basic-1\nSeed,1\n\n4, M=0, A=0\n4, N\n";
    var options=new SessionOptions{OutputParent=Path.Combine(output,"warm-runs")};
    await using var game=await GameSession.StartAsync(root,source,options);
    runs.Add(game.RunDirectory);int pid=game.ProcessId;
    await game.WaitForStatusAsync(s=>s.State=="paused"&&s.Frame==0,TimeSpan.FromSeconds(90));
    var original=Fields(game.RunDirectory);
    await game.StepAsync();
    Require(game.Latest.Line==5&&game.Latest.SegmentFrame==1&&game.Latest.SegmentFrames==4,"第一输入行第1/4帧由实际模块回报");
    await game.SendAsync("PLAY");await game.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    string oldRun=game.RunDirectory;string oldFramesHash=await Hash(Path.Combine(oldRun,"frames.jsonl"));string oldRandomHash=await Hash(Path.Combine(oldRun,"random.tsv"));
    await game.RestartAsync(source,options);
    runs.Add(game.RunDirectory);
    Require(game.ProcessId==pid&&game.RunDirectory!=oldRun&&game.Latest.Frame==0&&game.Latest.State=="paused","重新回放保留PID并生成新快照/记录，停在帧0");
    var restarted=Fields(game.RunDirectory);
    var positionKeys=original.Keys.Where(k=>k.Contains("/Player.",StringComparison.Ordinal)&&k.EndsWith(".position",StringComparison.Ordinal)).ToArray();
    Require(positionKeys.Length>0&&positionKeys.All(k=>restarted.TryGetValue(k,out var value)&&value==original[k]),"重置恢复实际玩家初始位置");
    Require(restarted["Input.moveX"]=="0"&&restarted["Input.moveY"]=="0","重置基线无移动输入");
    Require(!restarted.Any(p=>p.Key.EndsWith(".Player.dead",StringComparison.Ordinal)&&p.Value=="True"),"重置玩家处于存活状态");
    Require(!restarted.Any(p=>p.Key.EndsWith(".Room.roomCompleted",StringComparison.Ordinal)&&p.Value=="True"),"重置关卡处于未完成状态");
    var baselineDifferences=original.Keys.Union(restarted.Keys,StringComparer.Ordinal).Where(k=>original.GetValueOrDefault(k)!=restarted.GetValueOrDefault(k)).Select(k=>new{field=k,cold=original.GetValueOrDefault(k),warm=restarted.GetValueOrDefault(k)}).ToArray();
    await game.StepAsync();Require(game.Latest.Frame==1&&game.Latest.SegmentFrame==1,"同进程重置后逐帧仍放行一个输入周期");
    await game.SendAsync("PLAY");await game.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    string seed2="Version,1\nStart,Basic-2\nSeed,2\n\n3, N\n";
    await game.RestartAsync(seed2,options);runs.Add(game.RunDirectory);
    Require(game.ProcessId==pid&&game.Script.Seed==2&&game.Latest.Scene=="Basic-2"&&game.Latest.Frame==0,"同进程重播应用新草稿、新起点及新种子");
    await game.SendAsync("PLAY");await game.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    ReplayVerifier.ValidateLogs(game.RunDirectory,3);
    Require(await Hash(Path.Combine(oldRun,"frames.jsonl"))==oldFramesHash&&await Hash(Path.Combine(oldRun,"random.tsv"))==oldRandomHash,"暖重置没有向已结束回放追加玩法或随机记录");
    return new{pid,sameProcessResets=2,runDirectories=runs.ToArray(),baselineDifferences,coldEquivalent=false,limitation="同进程关卡重建用于编辑；Unity原生绝对时间和物理累积保持活跃，不冒充独立进程确定性验证。"};
});
if(command=="resources")await Execute("同一实际进程连续五次重建的资源归属清理",async()=>{
    const string source="Version,1\nStart,Basic-1\nSeed,1\n\n2, N\n";
    var options=new SessionOptions{OutputParent=Path.Combine(output,"resource-runs")};
    await using var game=await GameSession.StartAsync(root,source,options);
    int pid=game.ProcessId;var samples=new List<Dictionary<string,string>>();
    for(int index=0;index<=5;index++) {
        if(index>0)await game.RestartAsync(source,options);
        runs.Add(game.RunDirectory);
        await game.WaitForStatusAsync(s=>s.State=="paused"&&s.Frame==0,TimeSpan.FromSeconds(90));
        Require(game.ProcessId==pid,"第"+index+"次初始边界保持同一实际PID");
        await game.SendAsync("PLAY");await game.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        ReplayVerifier.ValidateLogs(game.RunDirectory,2);
        using var diagnosticsFile=new FileStream(Path.Combine(game.RunDirectory,"runtime-events.txt"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        using var diagnosticsReader=new StreamReader(diagnosticsFile);
        string diagnostics=await diagnosticsReader.ReadToEndAsync();
        string sample=diagnostics.Split('\n').Single(x=>x.StartsWith("resource baseline;",StringComparison.Ordinal));
        var values=sample.Trim().Split(';').Where(x=>x.Contains('=')).Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>x[1]);
        values["runDirectory"]=game.RunDirectory;
        values["hostPrivateBytes"]=System.Diagnostics.Process.GetProcessById(pid).PrivateMemorySize64.ToString();samples.Add(values);
        if(index>0)Require(int.Parse(values["cleanup"].Split('=')[1])>0,"第"+index+"次重置实际释放登记的自建资源");
    }
    var warm=samples.Skip(1).ToArray();
    Require(warm.Max(x=>int.Parse(x["meshes"]))-warm.Min(x=>int.Parse(x["meshes"]))<=10,"五次暖重置后Mesh计数未逐次累积（波动不超过10）");
    Require(warm.Max(x=>int.Parse(x["materials"]))-warm.Min(x=>int.Parse(x["materials"]))<=10,"五次暖重置后Material计数未逐次累积（波动不超过10）");
    Require(warm.Max(x=>int.Parse(x["gameObjects"]))-warm.Min(x=>int.Parse(x["gameObjects"]))<=10,"五次暖重置后GameObject计数未逐次累积（波动不超过10）");
    return new{pid,resets=5,samples,coldEquivalent=false,scope="仅当前Basic-1零输入连续五次重置资源计数；进程内存和其他资源列出观测值，不声称长期不增长。"};
});
if(command is "join" or "all")await Execute("用户Basic-1和Basic-2连续自然转场拼接",async()=>{
    string a=Path.Combine(root,"examples","basic-1.tas"),b=Path.Combine(root,"examples","basic-2.tas");
    string hashA=await Hash(a),hashB=await Hash(b);
    var ordered=File.ReadAllLines(Path.Combine(root,"docs","levels.txt")).Where(x=>x.Length>0&&!x.StartsWith('#')).ToArray();
    string joined=ScriptComposer.Compose(new[]{new ScriptSource(a,await File.ReadAllTextAsync(a)),new ScriptSource(b,await File.ReadAllTextAsync(b))},ordered);
    string composed=Path.Combine(root,"examples","composed","basic-1-basic-2.tas");Directory.CreateDirectory(Path.GetDirectoryName(composed)!);
    if(File.Exists(composed)){Require(await File.ReadAllTextAsync(composed)==joined,"既有拼接示例仍为本次生成的文本，保持原文件不变");}
    else await File.WriteAllTextAsync(composed,joined,new UTF8Encoding(false));
    var report=await ReplayVerifier.VerifyAsync(root,joined,new SessionOptions{OutputParent=Path.Combine(output,"joined")},2,true,Console.WriteLine);
    runs.AddRange(report.Runs.Select(x=>x.Directory));
    Require(await Hash(a)==hashA&&await Hash(b)==hashB,"拼接没有覆盖用户两个原始脚本");
    Require(report.Passed,"两次独立连续回放及暂停/逐帧/0.5x/2x精确逐帧与随机比较全部通过");
    foreach(var run in report.Runs){string events=await File.ReadAllTextAsync(Path.Combine(run.Directory,"runtime-events.txt"));Require(events.Contains("room completed;")&&events.Contains("scene=Basic-1")&&events.Contains("scene=Basic-2")&&events.Contains("chapter baseline;"),"连续日志包含两关过关及第二章激活边界");}
    return new{composed,sourceHashes=new[]{hashA,hashB},report=Path.Combine(output,"joined","report.json"),runDirectories=report.Runs.Select(x=>x.Directory).ToArray(),frames=report.Runs.Select(x=>x.Frames).ToArray(),scope="仅这两份已记录快照与当前版本；其他单关需从前序状态重新验证。"};
});
var result=new{version=1,kind="actual-game-editing-features",startedUtc=started,finishedUtc=DateTime.UtcNow,passed,cases,runDirectories=runs};
await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
await File.WriteAllTextAsync(Path.Combine(output,"report.md"),"# 编辑功能实际游戏验收\n\n结果："+(passed?"通过":"未通过")+"。\n\n同进程测试检查真实PID、初始玩家位置、段内进度、独立快照和种子。拼接测试使用用户两个源文件，保持原文件不变，并对整段自然转场做独立进程逐帧比较。完整证据见同目录 report.json 和列出的原始运行目录。\n\n暖重置保留Unity原生时间与物理累积，不能被视作冷启动完全相同；精确交付验证仍用独立进程。\n",new UTF8Encoding(false));
return passed?0:2;
