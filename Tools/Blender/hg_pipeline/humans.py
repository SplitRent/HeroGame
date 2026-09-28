"""Realistic people: the MakeHuman base body as a game character.

Builds one skinned body with a blend shape pair for every creator slider (``<morph>_lo`` / ``<morph>_hi``), whole-body
shapes (feminine, masculine, child, old, weight, muscle, bust), a Unity-humanoid skeleton with fingers and toes carrying
MakeHuman's own skin weights, and eyes. Unity sets the blend shape weights from ``AppearanceData`` at runtime.

    blender -b -P Tools/Blender/cli.py -- human --mpfb <MPFB extension folder> --art-root Game/Assets/_Project/Art
"""
import math
import os

import bpy
import numpy as np
from mathutils import Vector

from . import makehuman as mh


def _new_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def srgb_to_linear(c):
    c = np.asarray(c, dtype=np.float64)
    return tuple(np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4))


def hex_to_linear(hexv):
    h = hexv.lstrip("#")
    return srgb_to_linear([int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4)])


def skin_detail_image(source_path, name="T_Skin_Detail", size=1024):
    """
    A tone-free skin detail map from a skin texture: each pixel divided by its blurred neighbourhood, so pores,
    creases, lips and nails remain while the overall colour goes (the game tints it with the character's tone).
    """
    src = bpy.data.images.load(source_path, check_existing=True)
    if src.size[0] != size:
        src.scale(size, size)
    px = np.asarray(src.pixels[:], dtype=np.float32).reshape(size, size, 4)[..., :3]
    lum = px.mean(axis=2, keepdims=True)
    blurred = lum.copy()
    for _ in range(6):  # box blur, repeated ≈ gaussian, radius ~48 px
        for axis in (0, 1):
            k = 16
            c = np.cumsum(np.pad(blurred, [(k, k) if a == axis else (0, 0) for a in range(3)], mode="edge"), axis=axis)
            hi = np.take(c, np.arange(2 * k, 2 * k + size), axis=axis)
            lo = np.take(c, np.arange(0, size), axis=axis)
            blurred = (hi - lo) / (2 * k)
    detail = px / np.maximum(blurred, 1e-3)
    # Keep hue shifts (lips, nipples) but centre the brightness on 1, then store as 0.5 = unchanged.
    detail = np.clip(detail * 0.5, 0.0, 1.0)
    img = bpy.data.images.new(name, size, size, alpha=False)
    # Colour space first: changing it afterwards regenerates a generated image and wipes its pixels.
    img.colorspace_settings.name = "Non-Color"
    out = np.concatenate([detail, np.ones((size, size, 1), dtype=np.float32)], axis=2)
    img.pixels.foreach_set(out.ravel())
    img.pack()
    return img


def _skin_material(name="M_Skin", tone_hex="#B07C57", detail=None):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    tone = hex_to_linear(tone_hex)
    bsdf.inputs["Base Color"].default_value = (*tone, 1.0)
    if detail is not None:
        nodes, links = mat.node_tree.nodes, mat.node_tree.links
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = detail
        rgb = nodes.new("ShaderNodeRGB")
        rgb.name = "Tone"
        rgb.outputs[0].default_value = (*tone, 1.0)
        mul = nodes.new("ShaderNodeMix")
        mul.data_type = "RGBA"
        mul.blend_type = "MULTIPLY"
        mul.inputs["Factor"].default_value = 1.0
        links.new(rgb.outputs[0], mul.inputs[6])
        scale = nodes.new("ShaderNodeVectorMath")
        scale.operation = "SCALE"
        scale.inputs["Scale"].default_value = 2.0
        links.new(tex.outputs["Color"], scale.inputs[0])
        links.new(scale.outputs["Vector"], mul.inputs[7])
        links.new(mul.outputs[2], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 0.52
    for key in ("Subsurface Weight", "Subsurface"):
        if key in bsdf.inputs:
            bsdf.inputs[key].default_value = 0.25 if key == "Subsurface Weight" else 0.08
            break
    if "Subsurface Radius" in bsdf.inputs:
        bsdf.inputs["Subsurface Radius"].default_value = (1.0, 0.35, 0.2)
    if "Subsurface Scale" in bsdf.inputs:
        bsdf.inputs["Subsurface Scale"].default_value = 0.01
    # Pores and fine wrinkles: a little noise in the normal.
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    noise = nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 900.0
    noise.inputs["Detail"].default_value = 6.0
    bump = nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.08
    bump.inputs["Distance"].default_value = 0.0005
    links.new(noise.outputs["Fac"], bump.inputs["Height"])
    links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return mat


def _eye_material(iris=(0.18, 0.11, 0.06)):
    mat = bpy.data.materials.new("M_Eye")
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = nodes.get("Principled BSDF")
    bsdf.inputs["Roughness"].default_value = 0.05
    # Sclera, iris and pupil by latitude on the eyeball (UV v = 1 at the front pole).
    coord = nodes.new("ShaderNodeTexCoord")
    sep = nodes.new("ShaderNodeSeparateXYZ")
    links.new(coord.outputs["UV"], sep.inputs["Vector"])
    ramp = nodes.new("ShaderNodeValToRGB")
    els = ramp.color_ramp.elements
    els[0].position, els[0].color = 0.0, (0.92, 0.9, 0.86, 1)
    els[1].position, els[1].color = 0.80, (0.9, 0.86, 0.82, 1)
    e = els.new(0.815)
    e.color = (*[c * 0.5 for c in iris], 1)
    e = els.new(0.84)
    e.color = (*iris, 1)
    e = els.new(0.9)
    e.color = (*[c * 0.7 for c in iris], 1)
    e = els.new(0.915)
    e.color = (0.01, 0.01, 0.01, 1)
    links.new(sep.outputs["Y"], ramp.inputs["Fac"])
    links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
    return mat


# Body regions, one material slot each, so clothing can cover them (placeholder painted-on clothes until garment
# meshes exist, and always at least underwear). Order is the material index Unity sees.
REGIONS = ["Head", "Hands", "Torso", "UpperArms", "LowerArms", "Hips", "Thighs", "Shins", "Feet", "Eyes"]
BONE_REGION = {
    "Head": "Head", "Neck": "Head",
    "Spine": "Torso", "Chest": "Torso", "UpperChest": "Torso", "LeftShoulder": "Torso", "RightShoulder": "Torso",
    "LeftUpperArm": "UpperArms", "RightUpperArm": "UpperArms",
    "LeftLowerArm": "LowerArms", "RightLowerArm": "LowerArms",
    "Hips": "Hips",
    "LeftUpperLeg": "Thighs", "RightUpperLeg": "Thighs",
    "LeftLowerLeg": "Shins", "RightLowerLeg": "Shins",
    "LeftFoot": "Feet", "RightFoot": "Feet", "LeftToes": "Feet", "RightToes": "Feet",
}


def region_of_bone(bone):
    if bone in BONE_REGION:
        return BONE_REGION[bone]
    return "Hands" if ("Hand" in bone or any(f in bone for f in ("Thumb", "Index", "Middle", "Ring", "Little"))) else "Torso"


def vertex_regions(data, used_arr):
    """Region index per used vertex: the region of the bone with the largest skin weight."""
    n = data.vertices.shape[0]
    best_w = np.zeros(n, dtype=np.float32)
    best_r = np.full(n, REGIONS.index("Torso"), dtype=np.int32)
    per_region = {}
    for mh_bone, (vidx, w) in data.weights.items():
        r = REGIONS.index(region_of_bone(mh.humanoid_bone_for(mh_bone)))
        acc = per_region.setdefault(r, np.zeros(n, dtype=np.float32))
        np.add.at(acc, vidx, w)
    for r, acc in per_region.items():
        better = acc > best_w
        best_w[better] = acc[better]
        best_r[better] = r
    return best_r[used_arr]


def set_tone(body, tone_hex, clothes=None):
    """Preview helper: skin tone everywhere, then garment colours by region ({region: hex})."""
    lin = (*hex_to_linear(tone_hex), 1.0)
    for mat in body.data.materials:
        if mat is None or mat.name.startswith("M_Human_Eyes"):
            continue
        region = mat.name.replace("M_Human_", "").split(".")[0]
        colour = (*hex_to_linear(clothes[region]), 1.0) if clothes and region in clothes else lin
        mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = colour
        if "Tone" in mat.node_tree.nodes:
            mat.node_tree.nodes["Tone"].outputs[0].default_value = colour
        bsdf = mat.node_tree.nodes["Principled BSDF"]
        bsdf.inputs["Roughness"].default_value = 0.8 if clothes and region in clothes else 0.52


def _eyeballs(centres, radius=0.0118, segments=20, rings=14):
    """Two UV spheres (vertices, faces, uvs, owner eye index per vertex) facing +Y."""
    verts, faces, uvs, owner = [], [], [], []
    for e, c in enumerate(centres):
        base = len(verts)
        for r in range(rings + 1):
            th = math.pi * r / rings
            for s in range(segments):
                ph = 2 * math.pi * s / segments
                # Pole at +Y (the front of the eye, where the iris is).
                x = math.sin(th) * math.cos(ph)
                z = math.sin(th) * math.sin(ph)
                y = math.cos(th)
                verts.append((c[0] + x * radius, c[1] + y * radius, c[2] + z * radius))
                uvs.append((s / segments, 1 - r / rings))
                owner.append(e)
        for r in range(rings):
            for s in range(segments):
                a = base + r * segments + s
                b = base + r * segments + (s + 1) % segments
                faces.append((a, b, b + segments, a + segments))
    return verts, faces, uvs, owner


def build_body(data, name="SK_Human", with_rig=True, detail=None):
    """The base body with every blend shape and the humanoid rig. Returns (mesh object, armature object or None)."""
    mh.MakeHumanData.missing = set()
    neutral_offsets = data.dense(mh.NEUTRAL)
    neutral = data.vertices + neutral_offsets
    used = sorted({v for f in data.faces for v in f})
    remap = {v: i for i, v in enumerate(used)}
    used_arr = np.asarray(used)
    floor_y = float(neutral[used_arr, 1].min())
    verts = mh.to_blender(neutral[used_arr], floor_y)
    faces = [tuple(remap[v] for v in f) for f in data.faces]

    # Eyeballs are part of the mesh so every blend shape that moves an eye socket moves the eye with it.
    eye_keys = ["eye.L", "eye.R"]
    eye_centres = [mh.to_blender(data.joint_position(k, neutral_offsets)[None, :], floor_y)[0] for k in eye_keys]
    # The socket's joint sits at the back of the eyeball; push the sphere forward to fill the lids.
    eye_centres = [(c[0], c[1] + 0.004, c[2]) for c in eye_centres]
    ev, ef, euv, eowner = _eyeballs(eye_centres)
    body_count = len(verts)
    all_verts = [tuple(v) for v in verts] + ev
    all_faces = faces + [tuple(i + body_count for i in f) for f in ef]

    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(all_verts, [], all_faces)
    mesh.update()
    uv = mesh.uv_layers.new(name="UVMap")
    loop = 0
    for f, fuv in zip(data.faces, data.face_uvs):
        for k in range(len(f)):
            u, v = data.uvs[fuv[k]]
            uv.data[loop].uv = (float(u), float(v))
            loop += 1
    for f in ef:
        for idx in f:
            uv.data[loop].uv = euv[idx]
            loop += 1
    vreg = vertex_regions(data, used_arr)
    eye_slot = REGIONS.index("Eyes")
    for i, p in enumerate(mesh.polygons):
        p.use_smooth = True
        if i < len(faces):
            counts = np.bincount(vreg[list(faces[i])], minlength=len(REGIONS))
            p.material_index = int(np.argmax(counts))
        else:
            p.material_index = eye_slot
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    skin = _skin_material(detail=detail)
    for region in REGIONS:
        if region == "Eyes":
            eye = _eye_material()
            eye.name = "M_Human_Eyes"
            obj.data.materials.append(eye)
        else:
            m = skin.copy()
            m.name = "M_Human_" + region
            obj.data.materials.append(m)
    all_co = np.asarray(all_verts, dtype=np.float32)
    eye_joint_verts = [np.asarray(data.joints[k]) for k in eye_keys]
    eye_owner = np.asarray(eowner)

    # Blend shapes.
    obj.shape_key_add(name="Basis", from_mix=False)
    made = []

    def add_shape(key, delta_mh):
        d = mh.delta_to_blender(delta_mh[used_arr])
        if float(np.abs(d).max()) < 1e-6:
            return
        eye_d = np.stack([mh.delta_to_blender(delta_mh[jv].mean(axis=0)[None, :])[0] for jv in eye_joint_verts])
        sk = obj.shape_key_add(name=key, from_mix=False)
        full = np.concatenate([d, eye_d[eye_owner]], axis=0)
        co = (all_co + full).astype(np.float32).ravel()
        sk.data.foreach_set("co", co)
        made.append(key)

    for morph, (lo, hi) in mh.MORPH_TARGETS.items():
        if lo:
            add_shape(morph + "_lo", data.dense([(t, 1.0) for t in lo]))
        if hi:
            add_shape(morph + "_hi", data.dense([(t, 1.0) for t in hi]))
    for macro, pairs in mh.MACROS.items():
        delta = data.dense(pairs)
        if macro in mh.MACRO_ABSOLUTE:
            delta = delta - neutral_offsets
        add_shape(macro, delta)
    obj["hg_shapes"] = ",".join(made)
    obj["hg_missing_targets"] = ",".join(sorted(mh.MakeHumanData.missing))

    arm_obj = None
    if with_rig:
        arm_obj = _build_rig(data, obj, neutral_offsets, used_arr, remap, floor_y)
        head = obj.vertex_groups.get("Head")
        if head is not None:
            head.add(list(range(body_count, len(all_verts))), 1.0, "REPLACE")
    return obj, arm_obj


def _joint(data, key, offsets, floor_y):
    p = data.joint_position(key, offsets)
    return None if p is None else Vector(mh.to_blender(p[None, :], floor_y)[0])


def _build_rig(data, body, offsets, used_arr, remap, floor_y):
    arm = bpy.data.armatures.new("Rig")
    arm_obj = bpy.data.objects.new("Rig", arm)
    bpy.context.scene.collection.objects.link(arm_obj)
    bpy.context.view_layer.objects.active = arm_obj
    bpy.ops.object.mode_set(mode="EDIT")
    bones = {}
    for name, parent, _, (head_key, tail_key) in mh.HUMANOID:
        head = _joint(data, head_key, offsets, floor_y)
        tail = _joint(data, tail_key, offsets, floor_y)
        if head is None:
            continue
        if tail is None or (tail - head).length < 0.005:
            tail = head + Vector((0, 0, 0.05))
        b = arm.edit_bones.new(name)
        b.head, b.tail = head, tail
        if parent and parent in bones:
            b.parent = bones[parent]
        bones[name] = b
    bpy.ops.object.mode_set(mode="OBJECT")

    # Skin weights: MakeHuman's per-bone weights folded into our bones.
    groups = {name: body.vertex_groups.new(name=name) for name in bones}
    acc = {}
    for mh_bone, (vidx, w) in data.weights.items():
        target = mh.humanoid_bone_for(mh_bone)
        if target not in groups:
            continue
        for v, weight in zip(vidx, w):
            i = remap.get(int(v))
            if i is None:
                continue
            key = (target, i)
            acc[key] = acc.get(key, 0.0) + float(weight)
    for (target, i), weight in acc.items():
        groups[target].add([i], min(1.0, weight), "REPLACE")
    mod = body.modifiers.new("Armature", "ARMATURE")
    mod.object = arm_obj
    body.parent = arm_obj
    return arm_obj


def set_appearance(body, values):
    """Preview helper: slider values 0..1 by morph id plus macro weights, the way the Unity avatar applies them."""
    keys = body.data.shape_keys.key_blocks
    for kb in keys[1:]:
        kb.value = 0.0
    for morph, v in values.items():
        if morph in mh.MORPH_TARGETS:
            if v < 0.5 and morph + "_lo" in keys:
                keys[morph + "_lo"].value = (0.5 - v) * 2
            elif v > 0.5 and morph + "_hi" in keys:
                keys[morph + "_hi"].value = (v - 0.5) * 2
        elif morph in keys:
            keys[morph].value = v


def export_fbx(path, objects):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    for o in objects:
        o.select_set(True)
        for c in o.children:
            c.select_set(True)
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={"ARMATURE", "MESH"}, add_leaf_bones=False,
                             bake_anim=False, use_mesh_modifiers=False, axis_forward="-Z", axis_up="Y",
                             apply_scale_options="FBX_SCALE_UNITS", mesh_smooth_type="FACE", use_armature_deform_only=True)


def find_skin_texture(root):
    """A light, even skin diffuse texture under ``root`` to derive the detail map from (None if there is none)."""
    best, best_score = None, -1
    for dirpath, _, files in os.walk(root):
        for f in files:
            low = f.lower()
            if not low.endswith((".png", ".jpg")) or "diffuse" not in low and "skin" not in dirpath.lower():
                continue
            if any(bad in low for bad in ("suit", "normal", "_nm", "spec", "thumb", "bump")):
                continue
            score = ("young" in low) * 4 + ("light" in low or "caucasian" in low) * 2 + ("diffuse" in low) * 3 + ("female" in low)
            if score > best_score:
                best, best_score = os.path.join(dirpath, f), score
    return best


def build_and_export(data, out_dir, skin_texture=None):
    """SK_Human.fbx (body, blend shapes, rig), T_Skin_Detail.png and HumanShapes.json in ``out_dir``."""
    import json
    _new_scene()
    detail = skin_detail_image(skin_texture) if skin_texture else None
    body, rig = build_body(data, name="SK_Human", with_rig=True, detail=detail)
    os.makedirs(out_dir, exist_ok=True)
    if detail is not None:
        path = os.path.join(out_dir, "T_Skin_Detail.png")
        detail.filepath_raw = path
        detail.file_format = "PNG"
        detail.save()
    export_fbx(os.path.join(out_dir, "SK_Human.fbx"), [rig])
    shapes = body["hg_shapes"].split(",")
    manifest = {
        "Mesh": "SK_Human",
        "Vertices": len(body.data.vertices),
        "Bones": [b.name for b in rig.data.bones],
        "Morphs": {m: {"Low": m + "_lo" if m + "_lo" in shapes else "", "High": m + "_hi" if m + "_hi" in shapes else ""}
                   for m in mh.MORPH_TARGETS},
        "Macros": [m for m in mh.MACROS if m in shapes],
        "SkinDetail": "T_Skin_Detail" if detail is not None else "",
        "Regions": REGIONS,
        "DetailScale": 2.0,
        "MissingTargets": sorted(mh.MakeHumanData.missing),
    }
    with open(os.path.join(out_dir, "HumanShapes.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=1)
    return manifest
