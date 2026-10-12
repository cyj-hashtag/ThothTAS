using System;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using ThothTAS.Core;

namespace ThothTAS.Runtime {
public static partial class Hooks {
    static int chapterIndex,chapterFrame,segmentFrame,segmentFrames,transitionFrames;
    static bool chapterInputIssued,chapterWaiting;
    sealed class RoomNotice {public int frame;public string scene,type;}
    static readonly List<RoomNotice> roomNotices=new List<RoomNotice>();
    static object completedRoom,enteredRoom;
    static void ResetChapterPlayback() {
        chapterIndex=0;chapterFrame=0;segmentFrame=0;segmentFrames=0;transitionFrames=0;chapterInputIssued=false;chapterWaiting=false;
        completedRoom=null;enteredRoom=null;roomNotices.Clear();
    }
    static bool RoomIsActive(object room) {
        var p=GetGlobal("player") as Component;
        return room!=null&&p!=null&&p.gameObject.activeInHierarchy&&CallBool(room,"GetPlayersAreActive");
    }
    static bool RoomIsCompleted(object room) {return room!=null&&object.Equals(Get(room,"roomCompleted"),true);}
    static void RoomNoticeAt(string scene,string type) {
        roomNotices.Add(new RoomNotice{frame=frame,scene=scene,type=type});
        eventLog.WriteLine("room "+type+";frame="+frame+";scene="+scene);
    }
    static void ObserveRoomEvents() {
        var room=GetGlobal("currentRoom");string scene=SceneManager.GetActiveScene().name;
        if(RoomIsCompleted(room)&&!object.ReferenceEquals(room,completedRoom)){completedRoom=room;RoomNoticeAt(scene,"completed");}
        if(RoomIsActive(room)&&!object.ReferenceEquals(room,enteredRoom)){enteredRoom=room;RoomNoticeAt(scene,"entered");}
    }
    static bool AdvanceChapter() {
        if(!script.HasExplicitRooms)return false;
        var room=GetGlobal("currentRoom");string scene=SceneManager.GetActiveScene().name;
        var current=script.Chapters[chapterIndex];
        if(!chapterWaiting&&scene==current.Scene&&RoomIsCompleted(room)) {
            if(chapterIndex+1==script.Chapters.Count)return true;
            chapterWaiting=true;transitionFrames=0;
        }
        if(chapterWaiting) {
            var next=script.Chapters[chapterIndex+1];
            if(scene==next.Scene&&RoomIsActive(room)) {
                chapterIndex++;chapterFrame=0;chapterWaiting=false;transitionFrames=0;chapterInputIssued=false;line=0;segmentFrame=0;segmentFrames=0;
                eventLog.WriteLine("chapter baseline;frame="+frame+";chapter="+chapterIndex+";scene="+scene+";randomCount="+randomCount);
            } else {
                transitionFrames++;
                if(scene!=current.Scene&&scene!=next.Scene)throw new InvalidOperationException("转场进入 "+scene+"，拼接预期 "+next.Scene+"；已暂停。");
                if(transitionFrames>3600)throw new InvalidOperationException("等待拼接关卡 "+next.Scene+" 激活超过3600帧；已暂停。");
            }
        } else if(scene!=script.Chapters[chapterIndex].Scene)throw new InvalidOperationException("当前场景 "+scene+" 与拼接关卡 "+script.Chapters[chapterIndex].Scene+" 不符；已暂停。");
        return false;
    }
    static void PrepareNextInput() {
        if(script.HasExplicitRooms&&chapterWaiting) {
            input=new TasFrameInput(0,0,AimKind.None,0,0);line=0;segmentFrame=0;segmentFrames=0;chapterInputIssued=false;return;
        }
        int local=script.HasExplicitRooms?chapterFrame:frame;
        var chapter=script.Chapters[chapterIndex];
        input=chapter.GetInputAtFrame(local,out line);chapterInputIssued=script.HasExplicitRooms;
        foreach(var segment in chapter.Segments)if(local>=segment.StartFrame&&local<segment.StartFrame+segment.Frames){segmentFrame=local-segment.StartFrame+1;segmentFrames=segment.Frames;break;}
    }
    static string RoomEventsJson() {
        var sb=new StringBuilder("[");bool first=true;
        foreach(var notice in roomNotices){if(!first)sb.Append(',');first=false;sb.Append("{\"frame\":").Append(notice.frame).Append(",\"scene\":").Append(Q(notice.scene)).Append(",\"type\":").Append(Q(notice.type)).Append('}');}
        return sb.Append(']').ToString();
    }
}
}
