r"""Read Unity 5.3 resources as data; never launch the game or load game code.

Run with the project-local UnityPy venv:
work\unitypy-venv\Scripts\python.exe src\ThothTAS.Inspect\extract_levels.py D:\Workspace4All\ThothTAS
"""
import hashlib
import json
from pathlib import Path
import re
import struct
import sys
import UnityPy

project = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
data_dir = project / "game" / "THOTH_Data"
global_path = data_dir / "globalgamemanagers"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def serial_strings(raw, pos):
    count = struct.unpack_from("<i", raw, pos)[0]
    if not 0 <= count <= 100:
        raise ValueError(f"Invalid serialized List<string> count: {count}")
    pos += 4
    result = []
    for _ in range(count):
        n = struct.unpack_from("<i", raw, pos)[0]
        pos += 4
        if n < 0 or pos + n > len(raw):
            raise ValueError("Invalid serialized string length")
        result.append(raw[pos:pos + n].decode("utf-8"))
        pos = (pos + n + 3) & ~3
    return result


env = UnityPy.load(str(global_path))
build_obj = next(o for o in env.objects if o.type.name == "BuildSettings")
build = build_obj.read_typetree()
time_obj = next(o for o in env.objects if o.type.name == "TimeManager")
raw_time = time_obj.get_raw_data()
fixed_dt, max_dt, time_scale = struct.unpack("<fff", raw_time)
scenes = build["scenes"]

# Resolve MonoScript pointers against the shared-assets table rather than inferring
# script types from string presence. Custom MonoBehaviour trees were stripped in
# this build. The two custom fields below are explicitly decoded from their first
# serialized field after the Unity base MonoBehaviour header. CIL/metadata shows
# Globals.levelNames = List<string> and Room.roomID = Int32 as their first fields.
menu_env = UnityPy.load(str(data_dir / "level4"), str(data_dir / "sharedassets0.assets"), str(data_dir / "sharedassets4.assets"))
globals_evidence = []
level_names = None
for obj in menu_env.objects:
    if obj.type.name != "MonoBehaviour" or Path(obj.assets_file.name).name != "level4":
        continue
    base = obj.read(check_read=False)
    script = base.m_Script.read()
    if script.m_ClassName != "Globals" or script.m_AssemblyName != "Assembly-CSharp.dll":
        continue
    raw = obj.get_raw_data()
    if base.m_Name != "" or struct.unpack_from("<i", raw, 28)[0] != 0:
        raise ValueError("Expected empty m_Name and 32-byte MonoBehaviour base")
    level_names = serial_strings(raw, 32)
    globals_evidence.append({"asset": "level4", "pathId": obj.path_id, "scriptClass": script.m_ClassName,
                             "customFieldOffset": 32, "field": "Globals.levelNames", "value": level_names})
assert level_names is not None, "Globals.levelNames could not be resolved"

all_scenes = []
levels = []
errors = []
for index, scene_path in enumerate(scenes):
    scene_name = Path(scene_path).stem
    match = re.fullmatch(r"(.+)-(\d+)", scene_name)
    ordinary = "/SinglePlayer/" in scene_path and match is not None and match[1].casefold() in [n.casefold() for n in level_names[:16]]
    reason = None if ordinary else ("hardcore" if "HARDCORE" in scene_name else "procedural" if "Procedural" in scene_name or scene_name.startswith("Proc-") else "ending" if "ENDING" in scene_name else "initialization-or-menu")
    all_scenes.append({"buildIndex": index, "path": scene_path, "name": scene_name, "ordinaryFixedRoom": ordinary, "excludedReason": reason})
    if not ordinary:
        continue
    prefix, room_number = match[1], int(match[2])
    group_id = next(i for i, n in enumerate(level_names[:16]) if n.casefold() == prefix.casefold())
    level_path = data_dir / f"level{index}"
    asset_paths = [level_path, data_dir / "sharedassets0.assets", data_dir / f"sharedassets{index}.assets"]
    level_env = UnityPy.load(*(str(p) for p in asset_paths if p.exists()))
    room_fields = []
    classes = set()
    for obj in level_env.objects:
        if obj.type.name != "MonoBehaviour" or Path(obj.assets_file.name).name != f"level{index}":
            continue
        try:
            base = obj.read(check_read=False)
            script = base.m_Script.read()
            if script.m_AssemblyName != "Assembly-CSharp.dll":
                continue
            classes.add(script.m_ClassName)
            if script.m_ClassName == "Room":
                raw = obj.get_raw_data()
                if base.m_Name != "" or struct.unpack_from("<i", raw, 28)[0] != 0:
                    raise ValueError("Expected empty m_Name and 32-byte MonoBehaviour base")
                room_fields.append({"pathId": obj.path_id, "customFieldOffset": 32,
                                    "roomId": struct.unpack_from("<i", raw, 32)[0]})
        except Exception as ex:
            errors.append({"asset": f"level{index}", "pathId": obj.path_id, "error": f"{type(ex).__name__}: {ex}"})
    if len(room_fields) != 1:
        errors.append({"asset": f"level{index}", "error": f"Expected one Room component, observed {len(room_fields)}"})
    actual_room_id = room_fields[0]["roomId"] if len(room_fields) == 1 else None
    inferred_global = group_id * 4 + actual_room_id - 1 if actual_room_id is not None else None
    levels.append({"name": scene_name, "buildIndex": index, "scenePath": scene_path, "asset": f"level{index}",
                   "assetSha256": sha(level_path), "groupId": group_id, "globalLevelName": level_names[group_id],
                   "sceneSuffixNumber": room_number, "serializedRoomId": actual_room_id, "roomId": actual_room_id,
                   "roomFieldEvidence": room_fields, "inferredGlobalRoomId": inferred_global, "globalRoomId": inferred_global,
                   "presentMonoBehaviourClasses": sorted(classes), "runtimeEntryVerified": False})

levels.sort(key=lambda x: (x["groupId"], x["sceneSuffixNumber"]))
result = {
    "schemaVersion": 1, "evidenceKind": "static-unity-resource-decoding", "decoder": f"UnityPy {UnityPy.__version__}",
    "runtimeExecutions": 0, "unityVersion": build["m_Version"], "globalGameManagersSha256": sha(global_path),
    "buildSettingsPathId": build_obj.path_id, "buildSettingsSceneCount": len(scenes),
    "timeManager": {"pathId": time_obj.path_id, "rawBytesHex": raw_time.hex(), "fixedDeltaTime": fixed_dt,
                    "maximumDeltaTime": max_dt, "timeScale": time_scale},
    "globalsLevelNames": level_names, "globalsFieldEvidence": globals_evidence,
    "ordinaryFixedRoomCount": len(levels), "ordinaryFixedGroupCount": len(set(x["groupId"] for x in levels)),
    "levels": levels, "allBuildScenes": all_scenes, "decodeErrors": errors,
    "limitations": [
        "资源存在及 Room 字段不证明实际游戏能够从这个起点成功入场或重复基线一致；runtimeEntryVerified 均为 false。",
        "此程序集实际字段为 Globals.levelNames；没有依赖不存在的 Globals.allRoomNames。",
        "Globals.levelNames 与 BuildSettings prefix 使用忽略大小写匹配；保存的 scene name / buildIndex 均来自原资源。",
        "普通资源共有 65 个 Room；Teleporters-5 是否在正常自然转场路径上必须看 Room CIL 和实际运行，不能因文件存在就假定可达。",
        "inferredGlobalRoomId 根据 NewMenu.UpdateChooseLevel 的 groupId*4 与 Room.UpdateSliceOut 的 roomID 递增推导，不是运行观察。",
        "Teleporters-5 的 Room.roomID=5，按公式 globalRoomId=44 与 zapper-1 冲突；UpdateSliceOut 在 roomID=4 已切下一组，普通主流程静态预期 64 室。Teleporters-5 的后继字符串会为 Teleporters-6，但 BuildSettings 没有该场景；此额外资源必须独立标识。",
        "自定义 MonoBehaviour typetree 被剥离；仅按已确认首个序列化字段读取 Globals.levelNames 和 Room.roomID。其余字段未作结构解码。"
    ]
}
out = project / "docs" / "levels.json"
out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
(project / "docs" / "levels.txt").write_text("\n".join(x["name"] for x in levels) + "\n", encoding="utf-8")
print(f"BuildSettings {len(scenes)} scenes / {len(levels)} ordinary room resources / {len(errors)} decode errors")
print("Globals.levelNames:", json.dumps(level_names, ensure_ascii=False))
for entry in levels:
    print(f"{entry['name']}: buildIndex={entry['buildIndex']} Room.roomID={entry['serializedRoomId']} groupId={entry['groupId']}")
sys.exit(0 if not errors else 2)
