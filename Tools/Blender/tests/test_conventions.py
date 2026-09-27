"""Pure-Python tests (no Blender needed): python3 -m unittest discover -s Tools/Blender/tests"""
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from hg_pipeline import conventions as c  # noqa: E402


class ConventionTests(unittest.TestCase):
    def test_valid_names(self):
        for name in ("SM_Storefront_2F_A", "SM_House_Shotgun_B_LOD2", "SK_Pedestrian_Female_A"):
            self.assertEqual([], c.validate_asset_name(name), name)

    def test_invalid_names(self):
        for name in ("storefront", "SM_storefront", "Mesh_Box", "SM_Box_LOD9"):
            self.assertTrue(c.validate_asset_name(name), name)

    def test_lod_and_collision_parsing(self):
        self.assertEqual("SM_Bench_A", c.base_name("SM_Bench_A_LOD3"))
        self.assertEqual(3, c.lod_level("SM_Bench_A_LOD3"))
        self.assertIsNone(c.lod_level("SM_Bench_A"))
        self.assertEqual("SM_Bench_A", c.collision_target("UCX_SM_Bench_A_01"))
        self.assertEqual([], c.validate_asset_name("UCX_SM_Bench_A_01"))
        self.assertTrue(c.validate_asset_name("UCX_bench_01"))

    def test_material_and_texture_names(self):
        self.assertEqual([], c.validate_material_name("M_Brick_Red"))
        self.assertTrue(c.validate_material_name("Material.001"))
        self.assertEqual([], c.validate_texture_name("T_Brick_Red_BC"))
        self.assertEqual([], c.validate_texture_name("T_Brick_Red_N"))
        self.assertTrue(c.validate_texture_name("T_Brick_Red"))

    def test_every_category_has_a_unity_folder(self):
        self.assertEqual(set(c.BUDGETS), set(c.UNITY_FOLDERS))


if __name__ == "__main__":
    unittest.main()
