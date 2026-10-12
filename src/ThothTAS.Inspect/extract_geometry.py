"""Static Unity scene geometry, composed with full parent TRS matrices.

This parses resource files only. It does not observe Awake/Start, spawning,
player activation, audio playback, physics, or runtime object positions.
"""
import json
import math
from pathlib import Path
import struct
import sys
import UnityPy
from UnityPy.classes.PPtr import PPtr

project = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
data_dir = project / "game" / "THOTH_Data"
catalog = json.loads((project / "docs" / "levels.json").read_text(encoding="utf-8"))
focus_names = ["Basic-1", "Balls-1", *[f"Teleporters-{i}" for i in range(1, 6)], *[f"Final-{i}" for i in range(1, 5)]]
receiver_classes = {"Receiver", "Bouncer", "BossBouncer", "Expander", "ShadowBouncer"}
key_classes = receiver_classes | {"Player", "PlayerAim", "Teleporter", "BigBoss", "BossBouncerSequence", "BossBouncerSphere",
    "BeatMaster", "LaserBeat", "RhythmLaser", "Seeker", "Scaler", "DynamicCube", "EvilPlayerBouncer", "Killer"}


def vector(v):
    return [float(v.x), float(v.y), float(v.z)]


def identity():
    return [[1.0 if i == j else 0.0 for j in range(4)] for i in range(4)]


def matmul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def transform_point(m, p):
    return [sum(m[i][j] * p[j] for j in range(3)) + m[i][3] for i in range(3)]


def local_matrix(t):
    q = t.m_LocalRotation
    x, y, z, w = float(q.x), float(q.y), float(q.z), float(q.w)
    s = vector(t.m_LocalScale)
    p = vector(t.m_LocalPosition)
    # Unity Quaternion TRS: column vectors and signed scale on each column.
    # No lossyScale approximation; parent rotation/negative/nonuniform scale is
    # preserved by multiplication of full affine matrices.
    r = [[1-2*y*y-2*z*z, 2*x*y-2*z*w, 2*x*z+2*y*w],
         [2*x*y+2*z*w, 1-2*x*x-2*z*z, 2*y*z-2*x*w],
         [2*x*z-2*y*w, 2*y*z+2*x*w, 1-2*x*x-2*y*y]]
    m = identity()
    for i in range(3):
        for j in range(3):
            m[i][j] = r[i][j] * s[j]
        m[i][3] = p[i]
    return m


def objkey(o):
    return (Path(o.object_reader.assets_file.name).name, o.object_reader.path_id)


def ptr_info(p):
    return {"fileId": p.m_FileID, "pathId": p.m_PathID}


def read_ptr(raw, offset, assets_file):
    file_id, path_id = struct.unpack_from("<iq", raw, offset)
    return PPtr(m_FileID=file_id, m_PathID=path_id, assetsfile=assets_file)


all_scenes = []
errors = []
for level in catalog["levels"]:
    index = level["buildIndex"]
    asset = f"level{index}"
    environment = UnityPy.load(str(data_dir / asset), str(data_dir / "sharedassets0.assets"), str(data_dir / f"sharedassets{index}.assets"))
    objects = [o for o in environment.objects if Path(o.assets_file.name).name == asset]
    transforms = {}
    gos = {}
    components = []
    colliders = []
    matrix_cache = {}
    path_cache = {}

    for reader in objects:
        if reader.type.name in ("Transform", "RectTransform"):
            t = reader.read()
            transforms[t.m_GameObject.m_PathID] = t
        elif reader.type.name == "GameObject":
            gos[reader.path_id] = reader.read()

    def world_matrix(t, active=None):
        key = objkey(t)
        if key in matrix_cache:
            return matrix_cache[key]
        active = set() if active is None else active
        if key in active:
            raise ValueError("Transform parent cycle")
        active.add(key)
        local = local_matrix(t)
        result = matmul(world_matrix(t.m_Father.read(), active), local) if t.m_Father.m_PathID else local
        active.remove(key)
        matrix_cache[key] = result
        return result

    def parent_path(t, active=None):
        key = objkey(t)
        if key in path_cache:
            return path_cache[key]
        active = set() if active is None else active
        if key in active:
            raise ValueError("Transform parent cycle")
        active.add(key)
        name = t.m_GameObject.read().m_Name
        result = parent_path(t.m_Father.read(), active) + "/" + name if t.m_Father.m_PathID else name
        active.remove(key)
        path_cache[key] = result
        return result

    def transform_entry(t):
        m = world_matrix(t)
        return {"asset": objkey(t)[0], "transformPathId": objkey(t)[1], "gameObjectPathId": t.m_GameObject.m_PathID,
                "path": parent_path(t), "worldPosition": transform_point(m, [0, 0, 0]),
                "localPosition": vector(t.m_LocalPosition), "localRotationQuaternion": [t.m_LocalRotation.x, t.m_LocalRotation.y, t.m_LocalRotation.z, t.m_LocalRotation.w],
                "localScale": vector(t.m_LocalScale), "worldMatrixRowMajor": [v for row in m for v in row]}

    def link(p):
        result = ptr_info(p)
        if not p.m_PathID:
            result["target"] = None
            return result
        target = p.deref().read(check_read=False)
        result["targetType"] = target.object_reader.type.name
        if result["targetType"] == "AudioSource":
            fields = target.object_reader.read_typetree()
            result["audioSource"] = {k: fields.get(k) for k in ("m_Enabled", "m_PlayOnAwake", "m_Pitch", "m_Volume", "Loop", "Mute", "m_audioClip")}
            clip_ptr = getattr(target, "m_audioClip", None)
            if clip_ptr is not None and clip_ptr.m_PathID:
                clip = clip_ptr.read()
                result["audioSource"]["clip"] = {"asset": Path(clip.object_reader.assets_file.name).name, "pathId": clip.object_reader.path_id,
                    "name": clip.m_Name, "length": clip.m_Length, "channels": clip.m_Channels, "frequency": clip.m_Frequency,
                    "loadType": clip.m_LoadType, "preloadAudioData": clip.m_PreloadAudioData}
            source_go = target.m_GameObject.read()
            source_transform = next((cp.read() for type_id, cp in source_go.m_Component if type_id in (4, 224)), None)
            if source_transform is not None and source_transform.m_Children:
                child = source_transform.m_Children[0].read()
                child_go = child.m_GameObject.read()
                result["firstChildAudioSources"] = [link(cp) for type_id, cp in child_go.m_Component if type_id == 82]
        if hasattr(target, "m_LocalPosition"):
            result["target"] = transform_entry(target)
        elif hasattr(target, "m_GameObject"):
            go = target.m_GameObject.read()
            result["targetGameObjectName"] = go.m_Name
            t = transforms.get(target.m_GameObject.m_PathID)
            if t is None:
                for type_id, component_ptr in go.m_Component:
                    if type_id in (4, 224):
                        t = component_ptr.read()
                        break
            if t is not None:
                result["target"] = transform_entry(t)
        return result

    classes = set()
    for reader in objects:
        if reader.type.name == "MonoBehaviour":
            try:
                base = reader.read(check_read=False)
                script = base.m_Script.read()
                if script.m_AssemblyName != "Assembly-CSharp.dll":
                    continue
                classname = script.m_ClassName
                classes.add(classname)
                if classname not in key_classes and not classname.startswith("Boss"):
                    continue
                go = gos.get(base.m_GameObject.m_PathID) or base.m_GameObject.read()
                t = transforms.get(base.m_GameObject.m_PathID)
                entry = {"class": classname, "componentPathId": reader.path_id, "gameObject": go.m_Name,
                         "gameObjectPathId": base.m_GameObject.m_PathID, "serializedActiveSelf": bool(go.m_IsActive),
                         "componentEnabled": bool(base.m_Enabled), "transform": transform_entry(t) if t is not None else None,
                         "receiverDerived": classname in receiver_classes}
                raw = reader.get_raw_data()
                # This game's MonoBehaviour base ends at offset 32 (empty m_Name).
                # Decode only bounded, known public-field layouts, never guess
                # unparsed private/runtime values.
                if base.m_Name == "" and struct.unpack_from("<i", raw, 28)[0] == 0:
                    fields = {}
                    if classname == "PlayerAim":
                        fields["overrideShootPos"] = link(read_ptr(raw, 56, reader.assets_file))
                        entry["shootOriginStatic"] = fields["overrideShootPos"].get("target") or entry["transform"]
                    if classname == "Player":
                        fields["isPlayer2"] = bool(raw[32])
                        fields["coopSpawnPos"] = link(read_ptr(raw, 36, reader.assets_file))
                        fields["moveSpeed"] = struct.unpack_from("<f", raw, 48)[0]
                    if classname in receiver_classes:
                        fields["myCollider"] = link(read_ptr(raw, 32, reader.assets_file))
                    if classname == "Teleporter":
                        fields["sphereTarget"] = link(read_ptr(raw, 32, reader.assets_file))
                        fields["bouncer"] = link(read_ptr(raw, 44, reader.assets_file))
                        fields["startVelocity"] = list(struct.unpack_from("<fff", raw, 56))
                        fields["speed"] = struct.unpack_from("<f", raw, 80)[0]
                        fields["activateOnCount"] = struct.unpack_from("<i", raw, 84)[0]
                        fields["shootDir"] = struct.unpack_from("<i", raw, 108)[0] if len(raw) >= 112 else None
                        entry["runtimeRelocationWarning"] = "Teleporter.Update follows bouncer.position + (0,0,-20) while seeking with no sphereTarget; a sphereTarget follows boss projectile/bouncer instead. Serialized sphere position is not an active-frame teleport destination. OnOtherReceiverOn teleports player when already active, not by walking through a fixed door."
                    if classname == "BigBoss":
                        fields["enemies"] = link(read_ptr(raw, 32, reader.assets_file))
                        fields["bouncerTemplate"] = link(read_ptr(raw, 44, reader.assets_file))
                        fields["cubeCount"] = struct.unpack_from("<i", raw, 56)[0]
                        n = struct.unpack_from("<i", raw, 60)[0]
                        if not 0 <= n <= 1000:
                            raise ValueError("Invalid BigBoss bouncerSpheres count")
                        tail = 64 + n * 12
                        fields["bouncerSpheresCount"] = n
                        fields["teleporter1"] = link(read_ptr(raw, tail, reader.assets_file))
                        fields["teleporter2"] = link(read_ptr(raw, tail + 12, reader.assets_file))
                        fields["createAsLine"] = bool(raw[tail + 24])
                        fields["createAsColumn"] = bool(raw[tail + 28])
                        fields["spaceBetween"] = struct.unpack_from("<f", raw, tail + 32)[0]
                        fields["rotateBossZ"] = struct.unpack_from("<f", raw, tail + 36)[0]
                        entry["runtimeSpawnWarning"] = "BigBoss.Awake clones template into a grid/line/column; static asset list is not runtime enemy population."
                    if classname == "LaserBeat":
                        fields["laserBeatSound"] = link(read_ptr(raw, 32, reader.assets_file))
                        fields["enemies"] = link(read_ptr(raw, 44, reader.assets_file))
                    if classname == "RhythmLaser":
                        fields["laserBeat"] = link(read_ptr(raw, 32, reader.assets_file))
                        fields["laser"] = link(read_ptr(raw, 44, reader.assets_file))
                    if classname == "BeatMaster":
                        fields["loop"] = link(read_ptr(raw, 32, reader.assets_file))
                        fields["startDelay"] = struct.unpack_from("<f", raw, 44)[0]
                    entry["decodedPublicFields"] = fields
                components.append(entry)
            except Exception as ex:
                errors.append({"scene": level["name"], "asset": asset, "pathId": reader.path_id, "error": f"{type(ex).__name__}: {ex}"})
        elif reader.type.name in ("BoxCollider", "SphereCollider", "CapsuleCollider"):
            try:
                c = reader.read()
                go = gos.get(c.m_GameObject.m_PathID) or c.m_GameObject.read()
                t = transforms.get(c.m_GameObject.m_PathID)
                if t is None:
                    continue
                m = world_matrix(t)
                center = vector(c.m_Center)
                entry = {"type": reader.type.name, "componentPathId": reader.path_id, "gameObject": go.m_Name,
                         "gameObjectPathId": c.m_GameObject.m_PathID, "path": parent_path(t),
                         "serializedActiveSelf": bool(go.m_IsActive), "enabled": bool(c.m_Enabled), "isTrigger": bool(c.m_IsTrigger),
                         "localCenter": center, "worldCenter": transform_point(m, center), "transform": transform_entry(t)}
                if reader.type.name == "BoxCollider":
                    size = vector(c.m_Size)
                    corners = [transform_point(m, [center[k] + signs[k] * size[k] / 2 for k in range(3)])
                               for signs in [(a, b, c) for a in (-1, 1) for b in (-1, 1) for c in (-1, 1)]]
                    entry["localSize"] = size
                    entry["worldCorners"] = corners
                    entry["worldAabb"] = {"min": [min(p[k] for p in corners) for k in range(3)], "max": [max(p[k] for p in corners) for k in range(3)]}
                else:
                    entry["localRadius"] = float(c.m_Radius)
                    if reader.type.name == "CapsuleCollider":
                        entry["localHeight"] = float(c.m_Height)
                        entry["direction"] = c.m_Direction
                    entry["worldShapeWarning"] = "Nonuniform parent scale/rotation preserved in matrix; Unity native collider scaling rules are not simulated."
                colliders.append(entry)
            except Exception as ex:
                errors.append({"scene": level["name"], "asset": asset, "pathId": reader.path_id, "error": f"{type(ex).__name__}: {ex}"})

    scene = {"name": level["name"], "buildIndex": index, "asset": asset, "assetSha256": level["assetSha256"],
             "allClasses": sorted(classes), "beatMasterPresent": "BeatMaster" in classes,
             "laserBeatPresent": "LaserBeat" in classes, "rhythmLaserPresent": "RhythmLaser" in classes,
             "components": components, "colliders": colliders}
    all_scenes.append(scene)
    if level["name"] in focus_names:
        print(level["name"], "players:", [(c["gameObject"], c["transform"]["worldPosition"]) for c in components if c["class"] == "Player"],
              "receivers:", [(c["class"], c["gameObject"], c["transform"]["worldPosition"]) for c in components if c["receiverDerived"]],
              "teleporters:", [(c["gameObject"], c["transform"]["worldPosition"]) for c in components if c["class"] == "Teleporter"])

# Math check independent of resource data: a parent 90 degree Z rotation and
# negative/nonuniform scale transforms child translation (1,2,3) to (-6,-2,12).
q = math.sqrt(0.5)
class Vec:
    def __init__(self, x, y, z, w=None): self.x, self.y, self.z, self.w = x, y, z, w
class TransformTest:
    m_LocalRotation, m_LocalScale, m_LocalPosition = Vec(0,0,q,q), Vec(-2,3,4), Vec(0,0,0)
math_observed = transform_point(local_matrix(TransformTest()), [1,2,3])
assert all(abs(a-b) < 1e-12 for a,b in zip(math_observed, [-6,-2,12]))

summary = {}
for scene in all_scenes:
    if scene["name"] in focus_names:
        summary[scene["name"]] = {"players": [c for c in scene["components"] if c["class"] in ("Player", "PlayerAim")],
            "receivers": [c for c in scene["components"] if c["receiverDerived"]],
            "teleporters": [c for c in scene["components"] if c["class"] == "Teleporter"],
            "bosses": [c for c in scene["components"] if c["class"] == "BigBoss" or c["class"].startswith("Boss")],
            "walls": [c for c in scene["colliders"] if "wall" in c["path"].lower()]}
result = {"schemaVersion": 1, "evidenceKind": "static-unity-resource-geometry", "decoder": f"UnityPy {UnityPy.__version__}",
          "runtimeExecutions": 0, "coordinateSystem": "Unity world XYZ; full parent T*R*S matrix composition; script angles use world XY",
          "matrixMathCheck": {"input": [1,2,3], "parentRotationZDegrees": 90, "parentScale": [-2,3,4], "expected": [-6,-2,12], "observed": math_observed},
          "audioDrivenGameplay": {
              "beatMasterScenes": [s["name"] for s in all_scenes if s["beatMasterPresent"]],
              "laserBeatScenes": [s["name"] for s in all_scenes if s["laserBeatPresent"]],
              "rhythmLaserScenes": [s["name"] for s in all_scenes if s["rhythmLaserPresent"]],
              "staticCodeFinding": "LaserBeat.FixedUpdate reads AudioSource.isPlaying / time>1.85 and fires laserOn/laserOff; RhythmLaser toggles Killer and reads isPlaying to update child positions. Audio clock can affect gameplay. BeatMaster.FixedUpdate reads loop.time but no BeatMaster component was found in these ordinary scene resources.",
              "runtimeExecutionConfirmed": False},
          "bossSceneClasses": {s["name"]: sorted(set(c["class"] for c in s["components"] if c["class"] == "BigBoss" or c["class"].startswith("Boss"))) for s in all_scenes if any(c["class"] == "BigBoss" or c["class"].startswith("Boss") for c in s["components"])},
          "focusSummaries": summary, "scenes": all_scenes, "decodeErrors": errors,
          "limitations": [
              "These coordinates are serialized pre-Awake resource values, not player-active baseline observations. Player Awake resets rotation and disables Player2 in single-player; Room and enemy code animate/scale/move objects at entry.",
              "Enemy templates/prefabs and inactive GameObjects can appear in this list. BigBoss dynamically instantiates enemies; their runtime identity/position requires actual game logs.",
              "World positions use full affine parent matrices including negative scale and quaternion rotation; no assumption that local positions equal world positions.",
              "Only specified bounded custom field layouts are decoded. Other custom MonoBehaviour fields remain unknown. Collider boxes have transformed corners/AABB; native sphere/capsule and physics behavior are not simulated.",
              "BeatMaster absence is bounded to the 65 ordinary scene resource component inventories, not dynamically spawned or persistent startup resources."]}
(project / "docs" / "scene-geometry.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print("Static geometry:", len(all_scenes), "scenes /", len(errors), "decode errors")
print("Boss scenes:", json.dumps(result["bossSceneClasses"], ensure_ascii=False))
sys.exit(0 if not errors else 2)
