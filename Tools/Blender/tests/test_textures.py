"""Kit textures: seamless, well-formed and named by convention (numpy only; no bpy)."""
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

try:
    import numpy as np
    from hg_pipeline import conventions, textures
except ImportError:  # numpy missing
    np = None


@unittest.skipIf(np is None, "numpy not installed")
class TextureTests(unittest.TestCase):
    def test_every_surface_tiles_seamlessly(self):
        for material in textures.SURFACES:
            colour, normal = textures.build(material)
            # The step across a tile edge must be no bigger than the steps inside the image (joints included): a pattern
            # that does not repeat a whole number of times per tile shows up as an outlier here.
            for axis, label in ((1, "vertical"), (0, "horizontal")):
                steps = np.abs(np.diff(colour, axis=axis)).mean(axis=(1 - axis, 2) if axis == 1 else (1, 2))
                first = colour[:, :1] if axis == 1 else colour[:1]
                last = colour[:, -1:] if axis == 1 else colour[-1:]
                wrap = np.abs(first - last).mean()
                self.assertLessEqual(wrap, steps.max() * 1.1 + 1e-3, "%s has a visible %s seam" % (material, label))

    def test_normals_are_unit_and_point_out(self):
        _, normal = textures.build("M_Brick_Red")
        n = normal * 2 - 1
        self.assertTrue(np.allclose(np.linalg.norm(n, axis=2), 1.0, atol=1e-6))
        self.assertTrue((n[..., 2] > 0).all())

    def test_names_follow_conventions(self):
        for material in textures.SURFACES:
            self.assertRegex(material, conventions.MATERIAL_NAME)
            for tex in textures.texture_names(material):
                self.assertRegex(tex, conventions.TEXTURE_NAME)


if __name__ == "__main__":
    unittest.main()
