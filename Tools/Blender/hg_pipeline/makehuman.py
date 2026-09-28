"""MakeHuman data for the character pipeline: base mesh, morph targets, skeleton weights.

Two sources give the same data:

* **MPFB** (the production source): the free MPFB extension for Blender ships MakeHuman's base mesh and targets
  under CC0. ``MakeHumanData.from_mpfb(path)`` reads ``data/3dobjs/base.obj``, ``data/targets/**/*.target(.gz)``
  and the default skeleton's weights straight from the extension folder (no MPFB code runs).
* **three.js dump** (development only): the ``makehuman-data`` npm package, used on machines without Blender
  extensions. Never commit its files; build production humans from MPFB.

Units: MakeHuman works in decimetres, Y up, facing +Z. ``to_blender`` converts to metres, Z up, facing +Y (so the
exported FBX faces +Z in Unity), feet on the ground.
"""
import gzip
import json
import os

import numpy as np

# Our creator sliders (appearance.json morph ids) → MakeHuman targets. Each entry: (low targets, high targets); a
# slider at 0 applies the low set fully, 1 the high set, 0.5 neither. Targets are relative names
# ("group/name" without extension); several targets in a set are applied together.
MORPH_TARGETS = {
    # Head
    "head_size": (["head/head-scale-horiz-less", "head/head-scale-vert-less", "head/head-scale-depth-less"],
                  ["head/head-scale-horiz-more", "head/head-scale-vert-more", "head/head-scale-depth-more"]),
    "head_width": (["head/head-scale-horiz-less"], ["head/head-scale-horiz-more"]),
    "face_length": (["head/head-scale-vert-less"], ["head/head-scale-vert-more"]),
    "temple_width": (["forehead/forehead-temple-in"], ["forehead/forehead-temple-out"]),
    "forehead_height": (["forehead/forehead-scale-vert-less"], ["forehead/forehead-scale-vert-more"]),
    "forehead_slope": (["forehead/forehead-trans-depth-backward"], ["forehead/forehead-trans-depth-forward"]),
    # Brow
    "brow_height": (["eyebrows/eyebrows-trans-vert-less"], ["eyebrows/eyebrows-trans-vert-more"]),
    "brow_ridge": (["eyebrows/eyebrows-trans-depth-less", "forehead/forehead-nubian-less"],
                   ["eyebrows/eyebrows-trans-depth-more", "forehead/forehead-nubian-more"]),
    "brow_angle": (["eyebrows/eyebrows-angle-down"], ["eyebrows/eyebrows-angle-up"]),
    # Eyes (both sides)
    "eye_size": (["eyes/l-eye-size-small", "eyes/r-eye-size-small"], ["eyes/l-eye-size-big", "eyes/r-eye-size-big"]),
    "eye_spacing": (["eyes/l-eye-move-in", "eyes/r-eye-move-in"], ["eyes/l-eye-move-out", "eyes/r-eye-move-out"]),
    "eye_height": (["eyes/l-eye-move-down", "eyes/r-eye-move-down"], ["eyes/l-eye-move-up", "eyes/r-eye-move-up"]),
    "eye_tilt": (["eyes/l-eye-corner2-down", "eyes/r-eye-corner2-down"], ["eyes/l-eye-corner2-up", "eyes/r-eye-corner2-up"]),
    "eye_depth": (["eyes/l-eye-push1-out", "eyes/r-eye-push1-out"], ["eyes/l-eye-push1-in", "eyes/r-eye-push1-in"]),
    "eyelid_hood": (["eyes/l-eye-eyefold-up", "eyes/r-eye-eyefold-up"], ["eyes/l-eye-eyefold-down", "eyes/r-eye-eyefold-down"]),
    "epicanthic_fold": (["eyes/l-eye-epicanthus-out", "eyes/r-eye-epicanthus-out"], ["eyes/l-eye-epicanthus-in", "eyes/r-eye-epicanthus-in"]),
    "eye_bags": (["eyes/l-eye-bag-min", "eyes/r-eye-bag-min"], ["eyes/l-eye-bag-max", "eyes/r-eye-bag-max"]),
    # Nose
    "nose_width": (["nose/nose-scale-horiz-decr"], ["nose/nose-scale-horiz-incr"]),
    "nose_length": (["nose/nose-scale-vert-decr"], ["nose/nose-scale-vert-incr"]),
    "nose_bridge_height": (["nose/nose-scale-depth-decr"], ["nose/nose-scale-depth-incr"]),
    "nose_bridge_curve": (["nose/nose-hump-lesshump", "nose/nose-curve-concave"], ["nose/nose-hump-morehump", "nose/nose-curve-convex"]),
    "nose_tip_height": (["nose/nose-point-down"], ["nose/nose-point-up"]),
    "nose_tip_size": (["nose/nose-point-width-less"], ["nose/nose-point-width-more", "nose/nose-volume-potato"]),
    "nostril_flare": (["nose/nose-flaring-decr"], ["nose/nose-flaring-incr"]),
    "nose_crookedness": ([], ["asym/asym-nose-1-l"]),
    # Cheeks
    "cheekbone_height": (["cheek/l-cheek-trans-vert-down", "cheek/r-cheek-trans-vert-down"], ["cheek/l-cheek-trans-vert-up", "cheek/r-cheek-trans-vert-up"]),
    "cheekbone_width": (["cheek/l-cheek-bones-in", "cheek/r-cheek-bones-in"], ["cheek/l-cheek-bones-out", "cheek/r-cheek-bones-out"]),
    "cheek_fullness": (["cheek/l-cheek-volume-deflate", "cheek/r-cheek-volume-deflate"], ["cheek/l-cheek-volume-inflate", "cheek/r-cheek-volume-inflate"]),
    "cheek_hollow": ([], ["cheek/l-cheek-inner-deflate", "cheek/r-cheek-inner-deflate"]),
    "dimples": ([], ["mouth/mouth-dimples-in"]),
    # Mouth
    "mouth_width": (["mouth/mouth-scale-horiz-decr"], ["mouth/mouth-scale-horiz-incr"]),
    "mouth_height": (["mouth/mouth-trans-down"], ["mouth/mouth-trans-up"]),
    "lip_upper_fullness": (["mouth/mouth-upperlip-volume-deflate"], ["mouth/mouth-upperlip-volume-inflate"]),
    "lip_lower_fullness": (["mouth/mouth-lowerlip-volume-deflate"], ["mouth/mouth-lowerlip-volume-inflate"]),
    "lip_corner_tilt": (["mouth/mouth-angles-down"], ["mouth/mouth-angles-up"]),
    "philtrum_depth": (["mouth/mouth-philtrum-volume-decrease"], ["mouth/mouth-philtrum-volume-increase"]),
    "overbite": (["chin/chin-prognathism-more"], ["chin/chin-prognathism-less"]),
    # Jaw and chin
    "jaw_width": (["chin/chin-bones-in"], ["chin/chin-bones-out"]),
    "jaw_angle": (["chin/chin-jaw-drop-less"], ["chin/chin-jaw-drop-more"]),
    "jaw_definition": (["head/head-round"], ["head/head-square"]),
    "double_chin": (["neck/neck-double-less"], ["neck/neck-double-more"]),
    "chin_length": (["chin/chin-height-min"], ["chin/chin-height-max"]),
    "chin_width": (["chin/chin-width-min"], ["chin/chin-width-max"]),
    "chin_projection": (["chin/chin-prominent-less"], ["chin/chin-prominent-more"]),
    "chin_cleft": ([], ["chin/chin-cleft-in"]),
    # Ears (both)
    "ear_size": (["ears/l-ear-size-small", "ears/r-ear-size-small"], ["ears/l-ear-size-big", "ears/r-ear-size-big"]),
    "ear_angle": (["ears/l-ear-wing-in", "ears/r-ear-wing-in"], ["ears/l-ear-wing-out", "ears/r-ear-wing-out"]),
    "ear_lobe": (["ears/l-ear-lobe-min", "ears/r-ear-lobe-min"], ["ears/l-ear-lobe-max", "ears/r-ear-lobe-max"]),
    "ear_point": (["ears/l-ear-shape1-triangle", "ears/r-ear-shape1-triangle"], ["ears/l-ear-shape1-pointed", "ears/r-ear-shape1-pointed"]),
    # Neck
    "neck_thickness": (["neck/neck-scale-horiz-less", "neck/neck-scale-depth-less"], ["neck/neck-scale-horiz-more", "neck/neck-scale-depth-more"]),
    "adams_apple": ([], ["neck/neck-trans-depth-forward"]),
    "neck_length": (["neck/neck-scale-vert-less"], ["neck/neck-scale-vert-more"]),
    # Torso and hips
    "shoulder_width": (["measure/measure-shoulder-decrease"], ["measure/measure-shoulder-increase"]),
    "chest_size": (["measure/measure-frontchest-decrease", "torso/torso-muscle-pectoral-decr"], ["measure/measure-frontchest-increase", "torso/torso-muscle-pectoral-incr"]),
    "waist_width": (["measure/measure-waist-decrease"], ["measure/measure-waist-increase"]),
    "stomach": (["stomach/stomach-tone-incr"], ["stomach/stomach-pregnant-incr"]),
    "torso_length": (["measure/measure-napetowaist-decrease"], ["measure/measure-napetowaist-increase"]),
    "back_width": (["torso/torso-vshape-less"], ["torso/torso-vshape-more"]),
    "hip_width": (["hip/hip-scale-horiz-decr"], ["hip/hip-scale-horiz-incr"]),
    "glutes": (["buttocks/buttocks-volume-decr"], ["buttocks/buttocks-volume-incr"]),
    # Limbs
    "arm_length": (["measure/measure-upperarmlenght-decrease", "measure/measure-lowerarmlenght-decrease"],
                   ["measure/measure-upperarmlenght-increase", "measure/measure-lowerarmlenght-increase"]),
    "upper_arm_size": (["measure/measure-upperarm-decrease"], ["measure/measure-upperarm-increase"]),
    "forearm_size": (["armslegs/l-lowerarm-skinny", "armslegs/r-lowerarm-skinny"], ["armslegs/l-lowerarm-fat", "armslegs/r-lowerarm-fat"]),
    "hand_size": (["armslegs/l-hand-scale-decr", "armslegs/r-hand-scale-decr"], ["armslegs/l-hand-scale-incr", "armslegs/r-hand-scale-incr"]),
    "leg_length": (["measure/measure-upperlegheight-decrease", "measure/measure-lowerlegheight-decrease"],
                   ["measure/measure-upperlegheight-increase", "measure/measure-lowerlegheight-increase"]),
    "thigh_size": (["measure/measure-thighcirc-decrease"], ["measure/measure-thighcirc-increase"]),
    "calf_size": (["measure/measure-calf-decrease"], ["measure/measure-calf-increase"]),
    "foot_size": (["armslegs/l-foot-scale-decr", "armslegs/r-foot-scale-decr"], ["armslegs/l-foot-scale-incr", "armslegs/r-foot-scale-incr"]),
}

# Sliders driven by macro shapes (whole-body changes), not by the table above.
MACRO_MORPHS = {"body_fat", "muscle", "bust_size", "posture"}

# Whole-body shapes. In MakeHuman, gender and age live in the ancestry targets ({ancestry}-{gender}-{age}); the
# "universal" targets only add muscle and weight on top. We average the three ancestry sets, so the base face is a
# blend and the creator's sliders (not a label) make each face distinct. The neutral body is half female, half male,
# young adult; gender and age shapes are differences from it, muscle/weight/bust targets are already differences.
ANCESTRIES = ("african", "asian", "caucasian")


def _mix(gender_ages):
    """Average of the ancestry targets over the given (gender, age, weight) list."""
    out = []
    for gender, age, weight in gender_ages:
        for a in ANCESTRIES:
            out.append(("macrodetails/{}-{}-{}".format(a, gender, age), weight / len(ANCESTRIES)))
    return out


def _pair(fmt):
    return [(fmt.format(g="female"), 0.5), (fmt.format(g="male"), 0.5)]


NEUTRAL = _mix([("female", "young", 0.5), ("male", "young", 0.5)])

MACROS = {
    "feminine": _mix([("female", "young", 1.0)]),
    "masculine": _mix([("male", "young", 1.0)]),
    "age_child": _mix([("female", "child", 0.5), ("male", "child", 0.5)]),
    "age_old": _mix([("female", "old", 0.5), ("male", "old", 0.5)]),
    "weight_min": _pair("macrodetails/universal-{g}-young-averagemuscle-minweight"),
    "weight_max": _pair("macrodetails/universal-{g}-young-averagemuscle-maxweight"),
    "muscle_min": _pair("macrodetails/universal-{g}-young-minmuscle-averageweight"),
    "muscle_max": _pair("macrodetails/universal-{g}-young-maxmuscle-averageweight"),
    "bust_min": [("breast/female-young-averagemuscle-averageweight-mincup-averagefirmness", 1.0)],
    "bust_max": [("breast/female-young-averagemuscle-averageweight-maxcup-averagefirmness", 1.0)],
}
# Shapes built as "this body minus the neutral body"; the rest are differences already.
MACRO_ABSOLUTE = {"feminine", "masculine", "age_child", "age_old"}

# MakeHuman default skeleton → Unity humanoid bones. Each of our bones takes the weights of the MakeHuman bones
# listed (prefix match on the name before the side suffix). Joint positions come from MakeHuman's joint vertices.
HUMANOID = [
    # name, parent, MakeHuman bones merged into it, (head joint, tail joint)
    ("Hips", None, ["root", "spine05", "pelvis"], ("spine05", "spine04")),
    ("Spine", "Hips", ["spine04", "spine03"], ("spine04", "spine03")),
    ("Chest", "Spine", ["spine02"], ("spine03", "spine02")),
    ("UpperChest", "Chest", ["spine01", "breast"], ("spine02", "spine01")),
    ("Neck", "UpperChest", ["neck01", "neck02", "neck03"], ("neck01", "neck03")),
    ("Head", "Neck", ["head", "jaw", "eye", "orbicularis", "oculi", "levator", "oris", "risorius", "special", "temporalis", "tongue"], ("head", "head:tail")),
]
for side, s in (("Left", "L"), ("Right", "R")):
    HUMANOID += [
        (side + "Shoulder", "UpperChest", ["clavicle." + s, "shoulder01." + s], ("clavicle." + s, "shoulder01." + s + ":tail")),
        (side + "UpperArm", side + "Shoulder", ["upperarm01." + s, "upperarm02." + s], ("upperarm01." + s, "upperarm02." + s + ":tail")),
        (side + "LowerArm", side + "UpperArm", ["lowerarm01." + s, "lowerarm02." + s], ("lowerarm01." + s, "lowerarm02." + s + ":tail")),
        (side + "Hand", side + "LowerArm", ["wrist." + s, "metacarpal1." + s, "metacarpal2." + s, "metacarpal3." + s, "metacarpal4." + s], ("wrist." + s, "wrist." + s + ":tail")),
        (side + "UpperLeg", "Hips", ["upperleg01." + s, "upperleg02." + s], ("upperleg01." + s, "upperleg02." + s + ":tail")),
        (side + "LowerLeg", side + "UpperLeg", ["lowerleg01." + s, "lowerleg02." + s], ("lowerleg01." + s, "lowerleg02." + s + ":tail")),
        (side + "Foot", side + "LowerLeg", ["foot." + s], ("foot." + s, "foot." + s + ":tail")),
        (side + "Toes", side + "Foot", ["toe"], ("toe3-1." + s, "toe3-3." + s + ":tail")),
    ]
    for f, finger in enumerate(("Thumb", "Index", "Middle", "Ring", "Little"), start=1):
        parent = side + "Hand"
        for p, part in enumerate(("Proximal", "Intermediate", "Distal"), start=1):
            name = side + finger + part
            key = "finger{}-{}.{}".format(f, p, s)
            HUMANOID.append((name, parent, [key], (key, key + ":tail")))
            parent = name


def humanoid_bone_for(mh_bone):
    """Which of our bones a MakeHuman bone's weights go to (longest matching prefix wins; side must match)."""
    side = mh_bone[-2:] if mh_bone[-2:] in (".L", ".R") else ""
    best, best_len = "Hips", -1
    for name, _, sources, _ in HUMANOID:
        for src in sources:
            src_side = src[-2:] if src[-2:] in (".L", ".R") else ""
            base = src[:-2] if src_side else src
            mh_base = mh_bone[:-2] if side else mh_bone
            if src_side and src_side != side:
                continue
            if not src_side and name.startswith(("Left", "Right")):
                # e.g. "toe" for LeftToes: only bones on that side.
                if (name.startswith("Left") and side != ".L") or (name.startswith("Right") and side != ".R"):
                    continue
            if mh_base == base or mh_base.startswith(base):
                if len(base) > best_len:
                    best, best_len = name, len(base)
    return best


class MakeHumanData:
    """Base mesh (metres, Z up), faces of the skin only, UVs, targets as sparse deltas, bone weights, joint vertices."""

    def __init__(self):
        self.vertices = None        # (N, 3) float32, MakeHuman units (dm, Y up)
        self.faces = []             # list of vertex index tuples (skin only)
        self.face_uvs = []          # list of uv index tuples, parallel to faces
        self.uvs = None             # (M, 2) float32
        self.joints = {}            # "bone" / "bone:tail" → list of vertex indices
        self.weights = {}           # MakeHuman bone name → (vertex indices, weights)
        self._target_loader = None
        self._cache = {}

    # ------------------------------------------------------------------ sources

    @staticmethod
    def from_threejs_dump(package_dir):
        """Development source: the makehuman-data npm package (public/data/...). Not for production assets."""
        data = MakeHumanData()
        model = json.load(open(os.path.join(package_dir, "public", "data", "models", "human_full_size.json")))
        data.vertices = np.asarray(model["vertices"], dtype=np.float32).reshape(-1, 3)
        data.uvs = np.asarray(model["uvs"][0], dtype=np.float32).reshape(-1, 2)
        materials = [m["DbgName"] for m in model["materials"]]
        data._parse_threejs_faces(model["faces"], materials)
        bones = [b["name"] for b in model["bones"]]
        idx = np.asarray(model["skinIndices"]).reshape(-1, model["influencesPerVertex"])
        wts = np.asarray(model["skinWeights"], dtype=np.float32).reshape(-1, model["influencesPerVertex"])
        by_bone = {}
        for v in range(idx.shape[0]):
            for k in range(idx.shape[1]):
                if wts[v, k] <= 0:
                    continue
                name = bones[idx[v, k]].replace("____head", "")
                by_bone.setdefault(name, ([], []))
                by_bone[name][0].append(v)
                by_bone[name][1].append(float(wts[v, k]))
        data.weights = {k: (np.asarray(a), np.asarray(b, dtype=np.float32)) for k, (a, b) in by_bone.items()}
        for key, verts in model["metadata"]["joint_pos_idxs"].items():
            bone, end = key.split("____")
            data.joints[bone if end == "head" else bone + ":tail"] = verts
        names = json.load(open(os.path.join(package_dir, "src", "json", "targets", "target-list.json")))["targets"]
        # Rows follow the sorted target paths (how the dump was written), not the index file's key order.
        order = [k.replace("data/targets/", "").replace(".target", "") for k in sorted(names)]
        blob = np.memmap(os.path.join(package_dir, "public", "data", "targets", "targets.bin"), dtype=np.int16, mode="r")
        width = data.vertices.shape[0] * 3
        index = {name: i for i, name in enumerate(order)}

        def load(name):
            i = index.get(name)
            if i is None:
                return None
            row = np.asarray(blob[i * width:(i + 1) * width], dtype=np.float32).reshape(-1, 3) * 1e-3
            nz = np.nonzero(np.any(row != 0, axis=1))[0]
            return nz, row[nz]

        data._target_loader = load
        return data

    @staticmethod
    def find_mpfb_data(path):
        """The MPFB 'data' folder under ``path`` (the extension folder, its parent, or the data folder itself)."""
        for cand in (path, os.path.join(path, "data"), os.path.join(path, "mpfb", "data"), os.path.join(path, "src", "mpfb", "data")):
            if os.path.exists(os.path.join(cand, "3dobjs", "base.obj")):
                return cand
        return None

    @staticmethod
    def from_mpfb(mpfb_dir):
        """Production source: an installed MPFB extension folder (contains data/3dobjs and data/targets)."""
        root = MakeHumanData.find_mpfb_data(mpfb_dir)
        if root is None:
            raise FileNotFoundError("No MPFB data (3dobjs/base.obj) under " + mpfb_dir)
        data = MakeHumanData()
        groups = data._parse_obj(os.path.join(root, "3dobjs", "base.obj"))
        rigs = os.path.join(root, "rigs", "standard")
        weights_path = os.path.join(rigs, "weights.default.json")
        if os.path.exists(weights_path):
            w = json.load(open(weights_path))
            for bone, pairs in w.get("weights", w).items():
                if isinstance(pairs, list) and pairs:
                    data.weights[bone] = (np.asarray([p[0] for p in pairs]), np.asarray([p[1] for p in pairs], dtype=np.float32))
        rig_path = os.path.join(rigs, "rig.default.json")
        if os.path.exists(rig_path):
            rig = json.load(open(rig_path))
            bones = rig.get("bones", rig)
            for bone, info in bones.items():
                if not isinstance(info, dict):
                    continue
                for end, key in (("head", bone), ("tail", bone + ":tail")):
                    verts = _joint_vertices(info.get(end), groups)
                    if verts:
                        data.joints[key] = verts
        # Targets can live in several places depending on the MPFB version and the asset packs installed: the
        # extension's own data folder, MPFB's user-data folder (asset packs), loose .target(.gz) files or packed .npz.
        data.data_roots = data_roots(mpfb_dir, root)
        data.index = TargetIndex(data.data_roots)
        data._target_loader = data.index.load
        data.skin_roots = data.data_roots
        return data

    def report(self):
        """What a build from this data would lack: targets, joints and weights our body needs."""
        lines = []
        wanted = set()
        for lo, hi in MORPH_TARGETS.values():
            wanted.update(lo)
            wanted.update(hi)
        for pairs in list(MACROS.values()) + [NEUTRAL]:
            wanted.update(n for n, _ in pairs)
        missing = sorted(n for n in wanted if self.target(n) is None)
        lines.append("vertices %d, skin faces %d, joints %d, weighted bones %d" % (len(self.vertices), len(self.faces), len(self.joints), len(self.weights)))
        lines.append("targets: %d of %d found" % (len(wanted) - len(missing), len(wanted)) + (" — missing: " + ", ".join(missing[:40]) if missing else ""))
        index = getattr(self, "index", None)
        if index is not None:
            lines.append("target files seen: %d loose, %d packed, in: %s" % (index.loose_count, index.packed_count, "; ".join(index.roots)))
            if missing:
                import difflib
                names = index.names()
                for n in missing[:12]:
                    close = difflib.get_close_matches(n, names, n=3, cutoff=0.5)
                    lines.append("  %s -> closest: %s" % (n, ", ".join(close) if close else "(nothing similar)"))
        joints = [j for _, _, _, (h, t) in HUMANOID for j in (h, t)]
        lost = [j for j in joints if j not in self.joints]
        lines.append("joints: %d of %d found" % (len(joints) - len(lost), len(joints)) + (" — missing: " + ", ".join(lost[:40]) if lost else ""))
        covered = {humanoid_bone_for(b) for b in self.weights}
        lines.append("bones with weights: %d of %d" % (len(covered), len(HUMANOID)))
        return "\n".join(lines)

    # ------------------------------------------------------------------ parsing

    def _parse_threejs_faces(self, faces, materials):
        i = 0
        while i < len(faces):
            kind = faces[i]
            i += 1
            quad = kind & 1
            n = 4 if quad else 3
            verts = faces[i:i + n]
            i += n
            mat = 0
            if kind & 2:
                mat = faces[i]
                i += 1
            if kind & 4:
                i += 1
            uv = None
            if kind & 8:
                uv = faces[i:i + n]
                i += n
            if kind & 16:
                i += 1
            if kind & 32:
                i += n
            if kind & 64:
                i += 1
            if kind & 128:
                i += n
            if materials[mat] == "body":
                self.faces.append(tuple(verts))
                self.face_uvs.append(tuple(uv) if uv is not None else tuple(verts))

    def _parse_obj(self, path):
        """base.obj: skin faces are in group 'body'; helper and joint geometry is skipped. Returns vertices per group."""
        verts, uvs = [], []
        group = "body"
        groups = {}
        with open(path) as f:
            for line in f:
                parts = line.split()
                if not parts:
                    continue
                tag = parts[0]
                if tag == "v":
                    verts.append([float(x) for x in parts[1:4]])
                elif tag == "vt":
                    uvs.append([float(x) for x in parts[1:3]])
                elif tag == "g":
                    group = parts[1] if len(parts) > 1 else "body"
                elif tag == "f":
                    vi, ti = [], []
                    for p in parts[1:]:
                        sp = p.split("/")
                        vi.append(int(sp[0]) - 1)
                        ti.append(int(sp[1]) - 1 if len(sp) > 1 and sp[1] else int(sp[0]) - 1)
                    groups.setdefault(group, set()).update(vi)
                    if group == "body":
                        self.faces.append(tuple(vi))
                        self.face_uvs.append(tuple(ti))
        self.vertices = np.asarray(verts, dtype=np.float32)
        self.uvs = np.asarray(uvs, dtype=np.float32) if uvs else np.zeros((len(verts), 2), dtype=np.float32)
        return {k: sorted(v) for k, v in groups.items()}

    # ------------------------------------------------------------------ targets

    def target(self, name):
        """(vertex indices, deltas (K, 3) in MakeHuman units) or None if the data set has no such target."""
        if name not in self._cache:
            self._cache[name] = self._target_loader(name) if self._target_loader else None
        return self._cache[name]

    def dense(self, pairs):
        """Sum of weighted targets as a dense (N, 3) delta; missing targets are skipped (reported via .missing)."""
        out = np.zeros_like(self.vertices)
        for name, weight in pairs:
            t = self.target(name)
            if t is None:
                self.missing.add(name)
                continue
            out[t[0]] += t[1] * weight
        return out

    missing = set()

    def joint_position(self, key, offsets=None):
        """Centre of a joint's vertices (MakeHuman places bones exactly like this)."""
        verts = self.joints.get(key)
        if not verts:
            return None
        pos = self.vertices[verts]
        if offsets is not None:
            pos = pos + offsets[verts]
        return pos.mean(axis=0)


def data_roots(mpfb_dir, main_data):
    """Every folder that may hold MakeHuman data for this MPFB install (its data folder and its user-data folders)."""
    roots = [main_data]
    ext_dir = os.path.dirname(os.path.dirname(os.path.abspath(mpfb_dir.rstrip("/\\"))))  # .../extensions
    blender_ver = os.path.dirname(ext_dir)
    candidates = [
        os.path.join(ext_dir, ".user", "blender_org", "mpfb"),
        os.path.join(ext_dir, ".user", "user_default", "mpfb"),
        os.path.join(blender_ver, "config", "mpfb"),
        os.path.join(blender_ver, "scripts", "mpfb"),
        os.path.join(os.path.expanduser("~"), "Documents", "makehuman", "v1py3", "data"),
        os.path.join(os.path.expanduser("~"), "Documents", "makehuman", "v1", "data"),
    ]
    user = os.path.join(ext_dir, ".user")
    if os.path.isdir(user):
        for repo in sorted(os.listdir(user)):
            candidates.append(os.path.join(user, repo, "mpfb"))
    for c in candidates:
        for d in (c, os.path.join(c, "data")):
            if os.path.isdir(d) and d not in roots:
                roots.append(d)
    return roots


class TargetIndex:
    """All target files under a set of roots, found by relative name ('group/name') or, failing that, by file name."""

    EXTS = (".target.gz", ".target", ".ptarget.gz", ".ptarget")

    def __init__(self, roots):
        self.roots = [r for r in roots if os.path.isdir(r)]
        self.by_rel = {}
        self.by_base = {}
        self.npz = []
        for root in self.roots:
            for dirpath, _, files in os.walk(root):
                for f in files:
                    low = f.lower()
                    path = os.path.join(dirpath, f)
                    if low.endswith(".npz") and "target" in low:
                        self.npz.append(path)
                        continue
                    ext = next((e for e in self.EXTS if low.endswith(e)), None)
                    if ext is None:
                        continue
                    rel = os.path.relpath(path, root).replace("\\", "/")[: -len(ext)]
                    if rel.startswith("targets/"):
                        rel = rel[len("targets/"):]
                    self.by_rel.setdefault(rel.lower(), path)
                    self.by_base.setdefault(os.path.basename(rel).lower(), path)
        self._packed = {}
        for path in self.npz:
            try:
                z = np.load(path, allow_pickle=True)
                for k in z.files:
                    if k.endswith(".index"):
                        name = k[: -len(".index")]
                        if name.startswith("targets/"):
                            name = name[len("targets/"):]
                        self._packed.setdefault(name.lower(), (z, k[: -len(".index")]))
            except Exception:  # noqa: BLE001 — a file that is not a target pack
                continue

    @property
    def loose_count(self):
        return len(self.by_rel)

    @property
    def packed_count(self):
        return len(self._packed)

    def names(self):
        return sorted(set(self.by_rel) | set(self._packed))

    def load(self, name):
        key = name.lower()
        path = self.by_rel.get(key) or self.by_base.get(os.path.basename(key))
        if path:
            opener = gzip.open if path.lower().endswith(".gz") else open
            with opener(path, "rt") as f:
                return parse_target_text(f.read())
        packed = self._packed.get(key)
        if packed is None:
            base = os.path.basename(key)
            packed = next((v for k, v in self._packed.items() if os.path.basename(k) == base), None)
        if packed:
            z, stem = packed
            idx = np.asarray(z[stem + ".index"]).astype(np.int64)
            vec = np.asarray(z[stem + ".vector"], dtype=np.float32).reshape(-1, 3) * 1e-3
            return idx, vec
        return None

    def write_inventory(self, path):
        with open(path, "w", newline="\n") as f:
            f.write("# MakeHuman targets seen by the HeroGame pipeline\n")
            for r in self.roots:
                f.write("# root: %s\n" % r)
            for n in self.names():
                f.write(n + "\n")


def _joint_vertices(ref, groups):
    """A rig joint reference (MPFB rig JSON): a joint cube's vertices, one vertex, or a vertex list."""
    if not isinstance(ref, dict):
        return None
    strategy = str(ref.get("strategy", "")).upper()
    if "cube_name" in ref and ref["cube_name"] in groups:
        return list(groups[ref["cube_name"]])
    if "vertex_indices" in ref:
        return list(ref["vertex_indices"])
    if "vertex_index" in ref:
        return [ref["vertex_index"]]
    if strategy == "CUBE" and "cube" in ref and ref["cube"] in groups:
        return list(groups[ref["cube"]])
    return None


def parse_target_text(text):
    """MakeHuman .target text: 'index dx dy dz' per line, '#' comments."""
    idx, vec = [], []
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        p = line.split()
        if len(p) < 4:
            continue
        idx.append(int(p[0]))
        vec.append([float(p[1]), float(p[2]), float(p[3])])
    return np.asarray(idx, dtype=np.int64), np.asarray(vec, dtype=np.float32).reshape(-1, 3)


def to_blender(points_mh, floor_y):
    """MakeHuman (dm, Y up, facing +Z) → Blender (m, Z up, facing +Y), feet at z = 0."""
    p = np.asarray(points_mh, dtype=np.float32)
    out = np.empty_like(p)
    out[..., 0] = -p[..., 0] * 0.1
    out[..., 1] = p[..., 2] * 0.1
    out[..., 2] = (p[..., 1] - floor_y) * 0.1
    return out


def delta_to_blender(delta_mh):
    d = np.asarray(delta_mh, dtype=np.float32)
    out = np.empty_like(d)
    out[..., 0] = -d[..., 0] * 0.1
    out[..., 1] = d[..., 2] * 0.1
    out[..., 2] = d[..., 1] * 0.1
    return out
