"""Pipeline tests that need Blender's Python module (pip install bpy==4.2.0 on Python 3.11).
Skipped automatically when bpy is unavailable."""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

try:
    import bpy  # noqa: F401
    HAVE_BPY = True
except ImportError:
    HAVE_BPY = False


@unittest.skipUnless(HAVE_BPY, "bpy not installed")
class PipelineTests(unittest.TestCase):
    def setUp(self):
        from hg_pipeline import kit
        self.kit = kit
        self.out = tempfile.mkdtemp(prefix="hg-art-")

    def _catalog(self, name):
        return next(entry for entry in self.kit.CATALOG if entry[0] == name)

    def test_every_catalog_asset_validates(self):
        from hg_pipeline import validate
        for name, category, factory in self.kit.CATALOG:
            objects = self.kit.build_asset(name, category, factory)
            issues = validate.validate_asset(objects, category)
            errors = [i for i in issues if i["severity"] == "error"]
            self.assertEqual([], errors, name)

    def test_lods_decrease_and_collision_exists(self):
        from hg_pipeline.geometry import triangle_count
        objects = self.kit.build_asset(*self._catalog("SM_Storefront_2F_A"))
        lods = sorted((o for o in objects if "_LOD" in o.name), key=lambda o: o.name)
        self.assertEqual(4, len(lods))
        counts = [triangle_count(o) for o in lods]
        self.assertTrue(all(a > b for a, b in zip(counts, counts[1:])), counts)
        self.assertTrue(any(o.name.startswith("UCX_SM_Storefront_2F_A") for o in objects))
        self.assertFalse(any(o.data.name.endswith(".001") for o in objects))

    def test_validation_catches_unapplied_scale_and_bad_names(self):
        from hg_pipeline import validate
        objects = self.kit.build_asset(*self._catalog("SM_Bench_A"))
        lod0 = next(o for o in objects if o.name.endswith("_LOD0"))
        lod0.scale = (2.0, 2.0, 2.0)
        lod0.name = "bench"
        messages = " ".join(i["message"] for i in validate.validate_asset(objects, "StreetFurniture"))
        self.assertIn("unapplied scale", messages)
        self.assertIn("expected SM_", messages)

    def test_fbx_round_trip_preserves_metric_dimensions(self):
        from hg_pipeline import export
        from hg_pipeline.geometry import world_bounds
        import bpy as b
        name, category, factory = self._catalog("SM_Storefront_2F_A")
        objects = self.kit.build_asset(name, category, factory)
        lod0 = next(o for o in objects if o.name.endswith("_LOD0"))
        lo, hi = world_bounds(lod0)
        expected = tuple(hi - lo)
        path, report = export.export_asset(objects, category, self.out, name=name)
        self.assertTrue(os.path.getsize(path) > 10000)
        self.assertTrue(os.path.exists(path.replace(".fbx", ".pipeline.json")))
        self.kit.reset_scene()
        b.ops.import_scene.fbx(filepath=path)
        imported = b.data.objects[name + "_LOD0"]
        b.context.view_layer.update()
        lo, hi = world_bounds(imported)
        # Same metric size in world space, and still Z-up in Blender after the Y-up round trip.
        for got, want in zip(tuple(hi - lo), expected):
            self.assertAlmostEqual(want, got, places=2)
        self.assertAlmostEqual(0.0, lo.z, places=2)


if __name__ == "__main__":
    unittest.main()
