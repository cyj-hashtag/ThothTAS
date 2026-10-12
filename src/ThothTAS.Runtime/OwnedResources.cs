using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ThothTAS.Runtime {
public static partial class Hooks {
    static readonly HashSet<UnityEngine.Object> ownedResources=new HashSet<UnityEngine.Object>();
    static readonly HashSet<GameObject> persistentGameRoots=new HashSet<GameObject>();
    static bool captureOwnedResources=true;
    static string resourceCleanup="none";
    static int resourceTrackingFailures;

    public static void PersistGameObject(UnityEngine.Object value) {
        UnityEngine.Object.DontDestroyOnLoad(value);
        var go=value as GameObject;var component=value as Component;
        if(go==null&&component!=null)go=component.gameObject;
        if(go!=null)persistentGameRoots.Add(go.transform.root.gameObject);
    }

    // Only original gameplay constructors and instancing getters call this.
    // Never infer ownership by enumerating all loaded engine objects.
    public static void TrackOwnedResource(UnityEngine.Object value) {
        if(captureOwnedResources&&value!=null)try{ownedResources.Add(value);}catch(Exception){resourceTrackingFailures++;}
    }
    public static Material MaterialInstance(Renderer owner) {
        var previous=owner.sharedMaterial;var value=owner.material;
        if(value!=previous)TrackOwnedResource(value);
        return value;
    }
    public static Mesh MeshInstance(MeshFilter owner) {
        var previous=owner.sharedMesh;var value=owner.mesh;
        if(value!=previous)TrackOwnedResource(value);
        return value;
    }
    static void ProtectBaselineResources(object value,HashSet<UnityEngine.Object> protectedAssets,HashSet<object> visited) {
        if(value==null||!visited.Add(value))return;
        var resource=value as UnityEngine.Object;
        if(resource!=null){protectedAssets.Add(resource);return;}
        var items=value as IList;
        if(items!=null)foreach(var item in items)ProtectBaselineResources(item,protectedAssets,visited);
    }
    static void ReleaseOwnedResources() {
        var protectedAssets=new HashSet<UnityEngine.Object>();
        var visited=new HashSet<object>();
        foreach(var pair in staticBaseline)ProtectBaselineResources(pair.Value,protectedAssets,visited);
        int destroyed=0,retained=0,alreadyGone=0;
        captureOwnedResources=false;
        try {
            foreach(var value in ownedResources) {
                if(value==null){alreadyGone++;continue;}
                if(protectedAssets.Contains(value)){retained++;continue;}
                UnityEngine.Object.DestroyImmediate(value);destroyed++;
            }
            ownedResources.Clear();
        } finally {captureOwnedResources=true;}
        resourceCleanup="destroyed="+destroyed+";baselineRetained="+retained+";alreadyGone="+alreadyGone;
    }
    static void RecordResources() {
        // Counts and process memory are diagnostics, outside the deterministic
        // gameplay comparison. They do not create material/mesh instances.
        eventLog.WriteLine("resource baseline;reset="+resetCount+";owned="+ownedResources.Count+
            ";meshes="+Resources.FindObjectsOfTypeAll(typeof(Mesh)).Length+
            ";materials="+Resources.FindObjectsOfTypeAll(typeof(Material)).Length+
            ";textures="+Resources.FindObjectsOfTypeAll(typeof(Texture)).Length+
            ";gameObjects="+Resources.FindObjectsOfTypeAll(typeof(GameObject)).Length+
            ";privateBytes="+System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64+
            ";persistentRoots="+persistentGameRoots.Count+";trackingFailures="+resourceTrackingFailures+";cleanup="+resourceCleanup);
        var groups=new SortedDictionary<string,int>(StringComparer.Ordinal);
        foreach(GameObject go in Resources.FindObjectsOfTypeAll(typeof(GameObject))) {
            string key=(go.scene.IsValid()?"scene:"+go.scene.name:"asset")+";root="+(go.transform.parent==null)+";flags="+go.hideFlags;
            int count;groups.TryGetValue(key,out count);groups[key]=count+1;
        }
        foreach(var group in groups)eventLog.WriteLine("resource objects;reset="+resetCount+";"+group.Key+";count="+group.Value);
    }
}
}
