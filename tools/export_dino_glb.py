"""Export a Parkasaurus animal (mesh + skeleton + textures + animations) to a .glb for Blender.

The game's rigs are imported with "Optimize Game Objects", so the prefab has no bone
transforms: the skeleton lives in the Avatar. This script rebuilds it from the Avatar,
skins the SkinnedMeshRenderers to it and bakes the AnimationClips (from an AssetRipper
Unity-project export, which has them as readable .anim YAML) into glTF animations.

Usage:
  venv/bin/python -I export_dino_glb.py <PrefabName> <out_dir> [--anims <AnimationClip dir>]
           [--assets sharedassets1.assets] [--texture <Texture2D name>]

Example:
  venv/bin/python -I export_dino_glb.py ColumbianMammoth ../blender/mammoth/origineel \
      --anims rip-sharedassets2/ExportedProject/Assets/AnimationClip --texture Columbian-mammoth_COL_1k

Unity is left-handed, glTF right-handed: we mirror X (positions x -> -x, quaternions
(x, -y, -z, w), matrices S*M*S, triangle winding flipped).
"""
import argparse
import glob
import json
import math
import os
import re
import struct

import numpy as np
import UnityPy
import yaml
from UnityPy.helpers.MeshHelper import MeshHandler

GAME_DATA = os.path.expanduser(
    "~/.local/share/Steam/steamapps/common/Parkasaurus/Parkasaurus_Data/")
FPS = 24
S = np.diag([-1.0, 1.0, 1.0, 1.0])


def pos(v):
    return [-v[0], v[1], v[2]]


def quat(x, y, z, w):
    return [x, -y, -z, w]


class Glb:
    def __init__(self):
        self.j = {"asset": {"version": "2.0", "generator": "parkasaurus export_dino_glb"},
                  "scene": 0, "scenes": [{"nodes": []}], "nodes": [], "meshes": [],
                  "accessors": [], "bufferViews": [], "buffers": [{"byteLength": 0}]}
        self.bin = bytearray()

    def view(self, data, target=None):
        while len(self.bin) % 4:
            self.bin.append(0)
        bv = {"buffer": 0, "byteOffset": len(self.bin), "byteLength": len(data)}
        if target:
            bv["target"] = target
        self.bin += data
        self.j["bufferViews"].append(bv)
        return len(self.j["bufferViews"]) - 1

    def acc(self, arr, type_, ctype=5126, target=None, minmax=False, normalized=False):
        arr = np.ascontiguousarray(arr)
        a = {"bufferView": self.view(arr.tobytes(), target), "componentType": ctype,
             "count": int(arr.shape[0]), "type": type_}
        if normalized:
            a["normalized"] = True
        if minmax:
            a["min"] = arr.min(axis=0).reshape(-1).tolist()
            a["max"] = arr.max(axis=0).reshape(-1).tolist()
        self.j["accessors"].append(a)
        return len(self.j["accessors"]) - 1

    def add(self, key, item):
        self.j.setdefault(key, []).append(item)
        return len(self.j[key]) - 1

    def save(self, path):
        while len(self.bin) % 4:
            self.bin.append(0)
        self.j["buffers"][0]["byteLength"] = len(self.bin)
        js = json.dumps(self.j, separators=(",", ":")).encode()
        js += b" " * (-len(js) % 4)
        with open(path, "wb") as f:
            f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(self.bin)))
            f.write(struct.pack("<II", len(js), 0x4E4F534A) + js)
            f.write(struct.pack("<II", len(self.bin), 0x004E4942) + self.bin)


def pad4(rows, count, dtype):
    """Bone indices/weights come as 1..4 values per vertex; glTF wants exactly 4."""
    out = np.zeros((count, 4), dtype=dtype)
    for i, r in enumerate(rows or []):
        r = r if isinstance(r, (tuple, list)) else (r,)
        out[i, :len(r)] = r
    return out


def comps(go):
    out = []
    for c in go.m_Component:
        ptr = c.component if hasattr(c, "component") else c[1]
        try:
            out.append(ptr.read())
        except Exception:
            out.append(None)
    return out


def comp(go, name):
    return next((c for c in comps(go) if type(c).__name__ == name), None)


def tex_png(tex, out_dir, written):
    name = tex.m_Name
    if name not in written:
        path = os.path.join(out_dir, name + ".png")
        tex.image.save(path)
        written[name] = path
    return written[name]


# ---------- animation curves (AssetRipper .anim YAML) ----------

def load_anim(path):
    text = open(path, encoding="utf-8").read()
    text = re.sub(r"^%.*\n|^--- .*\n", "", text, flags=re.M)
    clip = yaml.load(text, Loader=yaml.CSafeLoader)["AnimationClip"]
    stop = clip["m_AnimationClipSettings"]["m_StopTime"]
    return clip, stop


def hermite(keys, t, comps_):
    """Evaluate a Unity AnimationCurve (non-weighted Hermite) at time t."""
    if t <= keys[0]["time"]:
        return [keys[0]["value"][c] if comps_ else keys[0]["value"] for c in (comps_ or [0])]
    if t >= keys[-1]["time"]:
        return [keys[-1]["value"][c] if comps_ else keys[-1]["value"] for c in (comps_ or [0])]
    for k0, k1 in zip(keys, keys[1:]):
        if k0["time"] <= t <= k1["time"]:
            break
    dt = k1["time"] - k0["time"]
    s = (t - k0["time"]) / dt if dt > 0 else 0.0
    h00 = 2 * s ** 3 - 3 * s ** 2 + 1
    h10 = s ** 3 - 2 * s ** 2 + s
    h01 = -2 * s ** 3 + 3 * s ** 2
    h11 = s ** 3 - s ** 2
    out = []
    for c in (comps_ or [None]):
        g = (lambda k, f: k[f][c]) if c else (lambda k, f: k[f])
        p0, p1 = float(g(k0, "value")), float(g(k1, "value"))
        m0, m1 = float(g(k0, "outSlope")), float(g(k1, "inSlope"))
        if math.isinf(m0) or math.isinf(m1):  # stepped key
            out.append(p0)
        else:
            out.append(h00 * p0 + h10 * dt * m0 + h01 * p1 + h11 * dt * m1)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("prefab")
    ap.add_argument("out_dir")
    ap.add_argument("--assets", default="sharedassets1.assets")
    ap.add_argument("--anims", help="dir with AssetRipper-exported <Prefab>_*.anim files")
    ap.add_argument("--anim-prefix", help="clip name prefix (default: prefab name)")
    ap.add_argument("--texture", help="texture name to show on all skinned meshes in Blender "
                    "(the game's dino shader colours by masks, so its own textures look blank)")
    args = ap.parse_args()
    os.makedirs(args.out_dir, exist_ok=True)

    env = UnityPy.load(os.path.join(GAME_DATA, args.assets))
    go = next(o.read() for o in env.objects
              if o.type.name == "GameObject" and o.peek_name() == args.prefab)
    root_tr = comp(go, "Transform")
    avatar_obj = comp(go, "Animator").m_Avatar.read()
    av = avatar_obj.m_Avatar
    tos = dict(avatar_obj.m_TOS)
    skel = av.m_AvatarSkeleton.data
    pose = av.m_AvatarSkeletonPose.data.m_X

    g = Glb()
    # --- skeleton nodes (index i in avatar skeleton == glTF node i) ---
    path_of = {}
    for i, (node, nid) in enumerate(zip(skel.m_Node, skel.m_ID)):
        path = tos.get(nid, "")
        path_of[i] = path
        x = pose[i]
        n = {"name": path.split("/")[-1] if path else args.prefab,
             "translation": pos([x.t.x, x.t.y, x.t.z]),
             "rotation": quat(x.q.x, x.q.y, x.q.z, x.q.w),
             "scale": [x.s.x, x.s.y, x.s.z]}
        g.j["nodes"].append(n)
        if node.m_ParentId >= 0:
            g.j["nodes"][node.m_ParentId].setdefault("children", []).append(i)
    g.j["scenes"][0]["nodes"] = [0]
    node_by_hash = {nid: i for i, nid in enumerate(skel.m_ID)}
    node_by_path = {p: i for i, p in path_of.items()}

    textures = {}
    override_tex = None
    if args.texture:
        override_tex = next(o.read() for o in env.objects
                            if o.type.name == "Texture2D" and o.peek_name() == args.texture)
        tex_png(override_tex, args.out_dir, textures)

    def material_for(mat, eye=False):
        props = dict(mat.m_SavedProperties.m_TexEnvs)
        texs = {k: v.m_Texture.read() for k, v in props.items() if v.m_Texture.path_id}
        for t in texs.values():
            tex_png(t, args.out_dir, textures)
        main_tex = override_tex or texs.get("_Diffuse") or texs.get("_MainTex")
        m = {"name": mat.m_Name, "pbrMetallicRoughness": {"metallicFactor": 0.0,
                                                          "roughnessFactor": 0.9}}
        if main_tex is not None and not eye:
            img = g.add("images", {"uri": os.path.basename(textures[main_tex.m_Name]),
                                   "name": main_tex.m_Name})
            g.j.setdefault("samplers", [{"magFilter": 9729, "minFilter": 9987}])
            t = g.add("textures", {"source": img, "sampler": 0})
            m["pbrMetallicRoughness"]["baseColorTexture"] = {"index": t}
        return g.add("materials", m)

    # --- prefab transforms: skinned meshes, plus objects hanging off exposed bones (eyes) ---
    def walk(tr, parent_node):
        child_go = tr.m_GameObject.read()
        smr = comp(child_go, "SkinnedMeshRenderer")
        if smr is not None:
            if smr.m_Mesh.path_id:
                add_mesh(smr.m_Mesh.read(), smr.m_Materials, child_go.m_Name, skinned=True)
            return
        if parent_node is None:
            # direct child of the prefab: an exposed bone (e.g. Head_M) or nothing we need
            here = next((i for i, p in path_of.items() if p and p.split("/")[-1] == child_go.m_Name),
                        None)
        else:
            lp, lr, ls = tr.m_LocalPosition, tr.m_LocalRotation, tr.m_LocalScale
            n = {"name": child_go.m_Name, "translation": pos([lp.x, lp.y, lp.z]),
                 "rotation": quat(lr.x, lr.y, lr.z, lr.w), "scale": [ls.x, ls.y, ls.z]}
            mf = comp(child_go, "MeshFilter")
            if mf is not None and mf.m_Mesh.path_id:
                mr = comp(child_go, "MeshRenderer")
                n["mesh"] = add_mesh(mf.m_Mesh.read(), mr.m_Materials if mr else [],
                                     child_go.m_Name, skinned=False, eye=True)
            here = g.add("nodes", n)
            g.j["nodes"][parent_node].setdefault("children", []).append(here)
        for ch in tr.m_Children:
            walk(ch.read(), here)

    def add_mesh(mesh, mat_ptrs, label, skinned, eye=False):
        h = MeshHandler(mesh)
        h.process()
        v = np.array(h.m_Vertices, dtype=np.float32).reshape(-1, 3) * [-1, 1, 1]
        attrs = {"POSITION": g.acc(v.astype(np.float32), "VEC3", target=34962, minmax=True)}
        if h.m_Normals:
            nrm = np.array(h.m_Normals, dtype=np.float32).reshape(-1, 3) * [-1, 1, 1]
            attrs["NORMAL"] = g.acc(nrm.astype(np.float32), "VEC3", target=34962)
        if h.m_UV0:
            uv = np.array(h.m_UV0, dtype=np.float32).reshape(-1, 2).copy()
            uv[:, 1] = 1.0 - uv[:, 1]
            attrs["TEXCOORD_0"] = g.acc(uv, "VEC2", target=34962)
        if h.m_Colors:
            col = np.array(h.m_Colors, dtype=np.float32).reshape(len(v), -1)
            if col.max() > 1.0:
                col /= 255.0
            attrs["COLOR_0"] = g.acc(col.astype(np.float32), "VEC4" if col.shape[1] == 4 else "VEC3",
                                     target=34962)
        skin = None
        if skinned and mesh.m_BoneNameHashes:
            joints = [node_by_hash[hh] for hh in mesh.m_BoneNameHashes]
            bi = pad4(h.m_BoneIndices, h.m_VertexCount, np.uint16)
            bw = pad4(h.m_BoneWeights, h.m_VertexCount, np.float32)
            if not h.m_BoneWeights:  # single-bone skin without weight channel
                bw[:, 0] = 1.0
            bw /= np.maximum(bw.sum(axis=1, keepdims=True), 1e-8)
            attrs["JOINTS_0"] = g.acc(bi, "VEC4", ctype=5123, target=34962)
            attrs["WEIGHTS_0"] = g.acc(bw, "VEC4", target=34962)
            ibm = []
            for bp in mesh.m_BindPose:
                m = np.array([[getattr(bp, f"e{r}{c}") for c in range(4)] for r in range(4)])
                ibm.append((S @ m @ S).T.reshape(-1))  # glTF wants column-major
            skin = g.add("skins", {"name": label, "joints": joints,
                                   "inverseBindMatrices": g.acc(np.array(ibm, np.float32), "MAT4"),
                                   "skeleton": node_by_hash.get(mesh.m_RootBoneNameHash, 0)})
        mats = [m.read() for m in mat_ptrs if m.path_id]
        prims = []
        for si, tris in enumerate(h.get_triangles()):
            idx = np.array(tris, dtype=np.uint32).reshape(-1, 3)[:, ::-1]  # flip winding
            p = {"attributes": attrs, "indices": g.acc(idx.reshape(-1), "SCALAR", ctype=5125,
                                                       target=34963)}
            if si < len(mats):
                p["material"] = material_for(mats[si], eye)
            prims.append(p)
        mi = g.add("meshes", {"name": mesh.m_Name.strip(), "primitives": prims})
        if skinned:
            n = g.add("nodes", {"name": label + " (" + mesh.m_Name.strip() + ")", "mesh": mi})
            if skin is not None:
                g.j["nodes"][n]["skin"] = skin
            g.j["scenes"][0]["nodes"].append(n)
        return mi

    for ch in root_tr.m_Children:
        walk(ch.read(), None)

    # --- animations ---
    if args.anims:
        prefix = args.anim_prefix or args.prefab
        for f in sorted(glob.glob(os.path.join(args.anims, prefix + "_*.anim"))):
            clip, stop = load_anim(f)
            n_frames = max(2, int(round(stop * FPS)) + 1)
            times = np.linspace(0, stop, n_frames).astype(np.float32)
            t_acc = None
            channels, samplers = [], []
            for key, prop, comps_ in (("m_RotationCurves", "rotation", ["x", "y", "z", "w"]),
                                      ("m_PositionCurves", "translation", ["x", "y", "z"]),
                                      ("m_ScaleCurves", "scale", ["x", "y", "z"])):
                for c in clip.get(key) or []:
                    node = node_by_path.get(c["path"])
                    keys = c["curve"]["m_Curve"]
                    if node is None or not keys:
                        continue
                    vals = np.array([hermite(keys, float(t), comps_) for t in times])
                    if prop == "rotation":
                        vals /= np.linalg.norm(vals, axis=1, keepdims=True)
                        vals *= [1, -1, -1, 1]
                    elif prop == "translation":
                        vals *= [-1, 1, 1]
                    if t_acc is None:
                        t_acc = g.acc(times.reshape(-1, 1), "SCALAR", minmax=True)
                    out = g.acc(vals.astype(np.float32), "VEC4" if prop == "rotation" else "VEC3")
                    samplers.append({"input": t_acc, "output": out, "interpolation": "LINEAR"})
                    channels.append({"sampler": len(samplers) - 1,
                                     "target": {"node": node, "path": prop}})
            if channels:
                name = clip["m_Name"][len(prefix) + 1:] if clip["m_Name"].startswith(prefix) else clip["m_Name"]
                g.add("animations", {"name": name, "channels": channels, "samplers": samplers})
                print(f"  animation {name}: {stop:.2f}s, {len(channels)} channels")

    out = os.path.join(args.out_dir, args.prefab + ".glb")
    g.save(out)
    print("wrote", out, f"({len(g.bin) // 1024} KB), textures:", ", ".join(textures))


if __name__ == "__main__":
    main()
