using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ThothTAS.Runtime {
public static partial class Hooks {
    // These audited banks contain audio assets and their selectors, not players,
    // enemies, physics bodies, gameplay pools or scene clocks. Their children
    // migrate between folders in FixedUpdate, so retaining just the root is
    // insufficient: restore the original hierarchy, selectors and audio state.
    static readonly HashSet<GameObject> cachedSoundRoots=new HashSet<GameObject>();
    static readonly List<SoundTransform> cachedSoundTransforms=new List<SoundTransform>();
    static readonly List<SoundFields> cachedSoundFields=new List<SoundFields>();
    static readonly List<SoundAudio> cachedSoundAudio=new List<SoundAudio>();
    static readonly List<SoundFilter> cachedSoundFilters=new List<SoundFilter>();
    static readonly Dictionary<FieldInfo,object> cachedSoundStatics=new Dictionary<FieldInfo,object>();
    static readonly HashSet<string> soundBankTypes=new HashSet<string>(new[]{
        "SoundBankLoader","LoadSoundBank","GeneralSoundBank","ReceiverTrailsGlobalAudioList"},StringComparer.Ordinal);
    sealed class SoundTransform {
        public Transform value,parent;public int sibling;public Vector3 position,scale;public Quaternion rotation;public bool active;
    }
    sealed class SoundFields {public MonoBehaviour value;public Dictionary<FieldInfo,object> fields;public bool enabled;}
    sealed class SoundAudio {
        public AudioSource value;public AudioClip clip;public float volume,pitch,pan;public bool enabled,loop,mute,playOnAwake;
    }
    sealed class SoundFilter {public Behaviour value;public bool enabled;}
    static void RememberSoundBanks() {
        if(cachedSoundRoots.Count!=0)return;
        foreach(var root in persistentGameRoots) {
            if(root==null)continue;
            bool bank=false;
            foreach(var component in root.GetComponentsInChildren<MonoBehaviour>(true))if(component!=null&&soundBankTypes.Contains(component.GetType().FullName))bank=true;
            if(bank)cachedSoundRoots.Add(root);
        }
        foreach(var root in cachedSoundRoots) {
            if(root.GetComponentsInChildren<Collider>(true).Length!=0||root.GetComponentsInChildren<Rigidbody>(true).Length!=0)
                throw new InvalidOperationException("声音库含物理实体，不能缓存重置。");
            foreach(var transform in root.GetComponentsInChildren<Transform>(true))cachedSoundTransforms.Add(new SoundTransform {
                value=transform,parent=transform.parent,sibling=transform.GetSiblingIndex(),position=transform.localPosition,
                rotation=transform.localRotation,scale=transform.localScale,active=transform.gameObject.activeSelf});
            foreach(var component in root.GetComponentsInChildren<MonoBehaviour>(true)) {
                if(component==null)throw new InvalidOperationException("声音库含缺失脚本，不能缓存重置。");
                if(!soundBankTypes.Contains(component.GetType().FullName)&&component.GetType().FullName!="LoadSoundFolder")
                    throw new InvalidOperationException("声音库含未核对组件："+component.GetType().FullName);
                var fields=new Dictionary<FieldInfo,object>();
                foreach(var field in component.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))fields[field]=CopySoundValue(field.GetValue(component));
                cachedSoundFields.Add(new SoundFields {value=component,fields=fields,enabled=component.enabled});
                foreach(var field in component.GetType().GetFields(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)) {
                    if(field.IsLiteral||field.IsInitOnly)continue;
                    cachedSoundStatics[field]=CopySoundValue(field.GetValue(null));
                }
            }
            foreach(var audio in root.GetComponentsInChildren<AudioSource>(true))cachedSoundAudio.Add(new SoundAudio {
                value=audio,clip=audio.clip,volume=audio.volume,pitch=audio.pitch,pan=audio.panStereo,enabled=audio.enabled,
                loop=audio.loop,mute=audio.mute,playOnAwake=audio.playOnAwake});
            foreach(var filter in root.GetComponentsInChildren<AudioReverbFilter>(true))cachedSoundFilters.Add(new SoundFilter {value=filter,enabled=filter.enabled});
        }
        if(cachedSoundRoots.Count==0||cachedSoundAudio.Count==0)throw new InvalidOperationException("初始声音库缓存为空。");
        eventLog.WriteLine("sound bank cache;roots="+cachedSoundRoots.Count+";transforms="+cachedSoundTransforms.Count+";audioSources="+cachedSoundAudio.Count+";selectors="+cachedSoundFields.Count);
    }
    static object CopySoundValue(object value) {
        if(value==null||value is UnityEngine.Object||value is string||value.GetType().IsValueType)return value;
        var array=value as Array;if(array!=null)return array.Clone();
        var list=value as IList;
        if(list!=null){var copy=(IList)Activator.CreateInstance(value.GetType());foreach(var item in list)copy.Add(item);return copy;}
        throw new InvalidOperationException("未核对的声音库可变状态："+value.GetType().FullName);
    }
    static void RestoreSoundBanks() {
        foreach(var item in cachedSoundAudio) {
            if(item.value==null)throw new InvalidOperationException("声音库音源已销毁，请重新启动游戏。");
            // Stop also cancels PlayDelayed scheduled by the previous room.
            item.value.Stop();item.value.clip=item.clip;item.value.volume=item.volume;item.value.pitch=item.pitch;
            item.value.panStereo=item.pan;item.value.loop=item.loop;item.value.mute=item.mute;
            item.value.playOnAwake=item.playOnAwake;item.value.enabled=item.enabled;
        }
        foreach(var item in cachedSoundTransforms) {
            if(item.value==null)throw new InvalidOperationException("声音库层级已销毁，请重新启动游戏。");
            item.value.SetParent(item.parent,false);item.value.localPosition=item.position;item.value.localRotation=item.rotation;
            item.value.localScale=item.scale;item.value.gameObject.SetActive(item.active);
        }
        foreach(var item in cachedSoundTransforms)item.value.SetSiblingIndex(item.sibling);
        foreach(var item in cachedSoundFields) {
            if(item.value==null)throw new InvalidOperationException("声音库选择器已销毁，请重新启动游戏。");
            item.value.StopAllCoroutines();item.value.CancelInvoke();
            foreach(var field in item.fields)if(!field.Key.IsInitOnly)field.Key.SetValue(item.value,CopySoundValue(field.Value));
            item.value.enabled=item.enabled;
        }
        foreach(var item in cachedSoundFilters)item.value.enabled=item.enabled;
        foreach(var field in cachedSoundStatics)field.Key.SetValue(null,CopySoundValue(field.Value));
    }
}
}
