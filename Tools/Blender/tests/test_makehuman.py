"""The human pipeline: MakeHuman data parsing, slider mapping and the Blender build (synthetic data, no MakeHuman files)."""
import gzip
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

try:
    import numpy as np
    from hg_pipeline import makehuman as mh
except ImportError:
    np = None

try:
    import bpy  # noqa: F401
    HAVE_BPY = True
except ImportError:
    HAVE_BPY = False

APPEARANCE = os.path.join(os.path.dirname(__file__), "..", "..", "..", "Game", "Assets", "StreamingAssets", "Data", "appearance.json")


def synthetic_mpfb(root):
    """A tiny MPFB-shaped data folder: a strip of quads (group body), a joint cube per bone end, targets, weights."""
    data = os.path.join(root, "data")
    os.makedirs(os.path.join(data, "3dobjs"))
    lines = []
    # 2 columns x 18 rows of body vertices (a column-shaped 'person' 17 dm tall).
    for r in range(18):
        for c in range(2):
            lines.append("v %.3f %.3f %.3f" % (c * 2 - 1, r - 8.5, 0.0))
    body_count = 36
    joint_names = sorted({j for _, _, _, (h, t) in mh.HUMANOID for j in (h, t)} | {"eye.L", "eye.R", "eye.L:tail", "eye.R:tail"})
    for i, _ in enumerate(joint_names):
        lines.append("v %.3f %.3f %.3f" % (0.1 * (i % 7), 7.0 - 0.1 * i, 0.2))
    lines.append("vt 0 0")
    lines.append("g body")
    for r in range(17):
        a = r * 2 + 1
        lines.append("f %d/1 %d/1 %d/1 %d/1" % (a, a + 1, a + 3, a + 2))
    for i, name in enumerate(joint_names):
        lines.append("g joint-" + name.replace(":", "-"))
        v = body_count + i + 1
        lines.append("f %d/1 %d/1 %d/1" % (v, v, v))
    with open(os.path.join(data, "3dobjs", "base.obj"), "w") as f:
        f.write("\n".join(lines) + "\n")
    rigs = os.path.join(data, "rigs", "standard")
    os.makedirs(rigs)
    rig = {}
    for name in joint_names:
        bone, _, end = name.partition(":")
        rig.setdefault(bone, {})["tail" if end else "head"] = {"strategy": "CUBE", "cube_name": "joint-" + name.replace(":", "-")}
    with open(os.path.join(rigs, "rig.default.json"), "w") as f:
        json.dump(rig, f)
    weights = {"spine04": [[i, 1.0] for i in range(0, 12)], "upperleg01.L": [[i, 1.0] for i in range(12, 24)],
               "head": [[i, 1.0] for i in range(24, 36)]}
    with open(os.path.join(rigs, "weights.default.json"), "w") as f:
        json.dump({"weights": weights}, f)
    # Every target the body uses: a small push on one vertex (so each makes a real blend shape).
    wanted = set()
    for lo, hi in mh.MORPH_TARGETS.values():
        wanted.update(lo)
        wanted.update(hi)
    for pairs in list(mh.MACROS.values()) + [mh.NEUTRAL]:
        wanted.update(n for n, _ in pairs)
    for k, name in enumerate(sorted(wanted)):
        path = os.path.join(data, "targets", name + ".target.gz")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with gzip.open(path, "wt") as f:
            f.write("# synthetic\n%d 0.0 %.3f 0.0\n" % (k % body_count, 0.01 + (k % 5) * 0.01))
    return root


@unittest.skipIf(np is None, "numpy not installed")
class MakeHumanDataTests(unittest.TestCase):
    def test_target_text_parses_and_skips_comments(self):
        idx, vec = mh.parse_target_text("# header\n12 0.1 -0.2 0.3\n\n40 1 2 3\n")
        self.assertEqual(list(idx), [12, 40])
        self.assertAlmostEqual(float(vec[0, 1]), -0.2, places=5)

    def test_every_creator_slider_has_targets_or_a_macro(self):
        with open(APPEARANCE) as f:
            morphs = [m["Id"] for m in json.load(f)["Morphs"]]
        missing = [m for m in morphs if m not in mh.MORPH_TARGETS and m not in mh.MACRO_MORPHS]
        self.assertEqual(missing, [], "appearance.json sliders with nothing to drive them on the body")

    def test_skeleton_maps_makehuman_bones_to_unity_humanoid_names(self):
        self.assertEqual(mh.humanoid_bone_for("upperarm02.L"), "LeftUpperArm")
        self.assertEqual(mh.humanoid_bone_for("lowerarm01.R"), "RightLowerArm")
        self.assertEqual(mh.humanoid_bone_for("finger2-3.L"), "LeftIndexDistal")
        self.assertEqual(mh.humanoid_bone_for("toe4-2.R"), "RightToes")
        self.assertEqual(mh.humanoid_bone_for("oris05"), "Head")
        self.assertEqual(mh.humanoid_bone_for("spine05"), "Hips")
        names = [b[0] for b in mh.HUMANOID]
        self.assertEqual(len(names), len(set(names)))
        for required in ("Hips", "Spine", "Chest", "Neck", "Head", "LeftUpperArm", "RightLowerLeg", "LeftFoot", "RightHand"):
            self.assertIn(required, names)

    def test_mpfb_folder_reads_mesh_joints_weights_and_targets(self):
        root = synthetic_mpfb(tempfile.mkdtemp(prefix="hg-mpfb-"))
        data = mh.MakeHumanData.from_mpfb(root)
        self.assertEqual(len(data.faces), 17, "only group 'body' faces")
        self.assertIn("LeftUpperArm", [b for b, *_ in mh.HUMANOID])
        self.assertIsNotNone(data.joint_position("upperarm01.L"))
        self.assertIsNotNone(data.target("nose/nose-scale-horiz-incr"))
        self.assertIn("targets: 209 of 209 found", data.report().replace("\\n", "\n"))
        conv = mh.to_blender(np.array([[1.0, 8.5, 2.0]]), floor_y=-8.5)
        self.assertAlmostEqual(float(conv[0, 2]), 1.7, places=5, msg="feet on the ground, metres, Z up")
        self.assertAlmostEqual(float(conv[0, 1]), 0.2, places=5, msg="MakeHuman +Z (front) is Blender +Y")


@unittest.skipUnless(HAVE_BPY and np is not None, "bpy not installed")
class HumanBuildTests(unittest.TestCase):
    def test_body_builds_with_shapes_rig_regions_and_exports(self):
        from hg_pipeline import humans
        root = synthetic_mpfb(tempfile.mkdtemp(prefix="hg-mpfb-"))
        data = mh.MakeHumanData.from_mpfb(root)
        out = tempfile.mkdtemp(prefix="hg-human-")
        manifest = humans.build_and_export(data, out, None)
        self.assertTrue(os.path.exists(os.path.join(out, "SK_Human.fbx")))
        self.assertEqual(manifest["MissingTargets"], [])
        self.assertEqual(manifest["Regions"], humans.REGIONS)
        self.assertEqual(len(manifest["Bones"]), len(mh.HUMANOID))
        body = bpy.data.objects["SK_Human"]
        keys = body.data.shape_keys.key_blocks
        self.assertIn("nose_width_hi", keys)
        self.assertIn("feminine", keys)
        self.assertEqual(len(body.data.materials), len(humans.REGIONS))
        used = {p.material_index for p in body.data.polygons}
        self.assertIn(humans.REGIONS.index("Eyes"), used, "eyeballs are in the mesh")
        self.assertIn(humans.REGIONS.index("Thighs"), used, "leg faces take the thigh region")
        self.assertTrue(all(v.groups for v in body.data.vertices if v.index < 36), "body vertices are weighted")


if __name__ == "__main__":
    unittest.main()
