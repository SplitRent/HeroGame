"""Procedural modular building generator (Gulf Coast vernacular).

Produces blockout-to-midpoly architecture from parameters: brick/stucco storefront blocks, concrete
offices, corrugated warehouses and raised shotgun houses. Output is real, UV-mapped, multi-material
geometry intended as the *first pass* of the environment kit — silhouettes, scale, window rhythm and
material zones are production-correct; surface detail comes later from artists (see ASSET_TRACKER).
"""
import math
import random

import bmesh
from mathutils import Vector

from .. import geometry as g

FLOOR_HEIGHT = 3.4
GROUND_FLOOR_HEIGHT = 4.2

STYLES = {
    "brick": ("M_Brick_Red", (0.46, 0.2, 0.14), 0.85),
    "brick_brown": ("M_Brick_Brown", (0.36, 0.24, 0.17), 0.85),
    "stucco": ("M_Stucco_Cream", (0.83, 0.77, 0.63), 0.9),
    "stucco_teal": ("M_Stucco_Teal", (0.36, 0.6, 0.58), 0.9),
    "concrete": ("M_Concrete_Grey", (0.55, 0.55, 0.53), 0.8),
    "metal": ("M_Metal_Corrugated", (0.62, 0.64, 0.66), 0.45),
    "siding_white": ("M_Siding_White", (0.88, 0.88, 0.85), 0.7),
    "siding_yellow": ("M_Siding_Yellow", (0.86, 0.76, 0.42), 0.7),
    "siding_blue": ("M_Siding_Blue", (0.45, 0.6, 0.72), 0.7),
    "siding_green": ("M_Siding_Green", (0.5, 0.62, 0.5), 0.7),
    "stucco_white": ("M_Stucco_White", (0.86, 0.85, 0.82), 0.9),
}


class _Builder:
    """Accumulates boxes/quads into one bmesh with material slots."""

    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.materials = []

    def slot(self, mat_name, color, roughness=0.6, metallic=0.0):
        mat = g.material(mat_name, color, roughness, metallic)
        if mat not in self.materials:
            self.materials.append(mat)
        return self.materials.index(mat)

    def box(self, center, size, slot):
        g.add_box(self.bm, center, size, slot)

    def prism_roof(self, x0, x1, y0, y1, z, height, slot, overhang=0.35):
        """Gable roof running along Y."""
        x0 -= overhang
        x1 += overhang
        y0 -= overhang
        y1 += overhang
        xm = (x0 + x1) / 2
        v = [self.bm.verts.new(p) for p in (
            (x0, y0, z), (x1, y0, z), (xm, y0, z + height),
            (x0, y1, z), (x1, y1, z), (xm, y1, z + height))]
        for idx in ((0, 1, 2), (5, 4, 3), (0, 2, 5, 3), (1, 4, 5, 2), (0, 3, 4, 1)):
            f = self.bm.faces.new([v[i] for i in idx])
            f.material_index = slot

    def gable_roof_x(self, x0, x1, y0, y1, z, height, slot, overhang=0.4, gable_slot=None):
        """Gable roof with its ridge along X (gable ends on the sides, eaves facing the street)."""
        x0 -= overhang
        x1 += overhang
        y0 -= overhang
        y1 += overhang
        ym = (y0 + y1) / 2
        v = [self.bm.verts.new(p) for p in (
            (x0, y0, z), (x0, y1, z), (x0, ym, z + height),
            (x1, y0, z), (x1, y1, z), (x1, ym, z + height))]
        for idx, sl in (((0, 2, 1), gable_slot), ((3, 4, 5), gable_slot), ((0, 3, 5, 2), slot), ((1, 2, 5, 4), slot), ((0, 1, 4, 3), slot)):
            f = self.bm.faces.new([v[i] for i in idx])
            f.material_index = slot if sl is None else sl

    def pyramid(self, cx, cy, z, half, height, slot):
        v = [self.bm.verts.new(p) for p in (
            (cx - half, cy - half, z), (cx + half, cy - half, z), (cx + half, cy + half, z), (cx - half, cy + half, z), (cx, cy, z + height))]
        for idx in ((0, 1, 4), (1, 2, 4), (2, 3, 4), (3, 0, 4), (3, 2, 1, 0)):
            f = self.bm.faces.new([v[i] for i in idx])
            f.material_index = slot

    def finish(self, collection=None):
        obj = g.new_object(self.name, self.bm, collection)
        for m in self.materials:
            obj.data.materials.append(m)
        bmesh_obj = bmesh.new()
        bmesh_obj.from_mesh(obj.data)
        bmesh.ops.recalc_face_normals(bmesh_obj, faces=bmesh_obj.faces)
        bmesh_obj.to_mesh(obj.data)
        bmesh_obj.free()
        g.box_uv_project(obj, scale=2.0)
        return obj


def _facade_windows(b, width, depth, floors, ground_h, floor_h, bay, slots, storefront, rng, window_w=1.3, window_h=1.7):
    """Windows, frames and sills on all four facades; storefront glazing on the front ground floor."""
    wall, glass, trim, awning = slots["wall"], slots["glass"], slots["trim"], slots.get("awning")
    t = 0.06  # frame depth
    facades = (
        ("front", Vector((0, -1, 0)), width, lambda s: Vector((s, -depth / 2, 0))),
        ("back", Vector((0, 1, 0)), width, lambda s: Vector((s, depth / 2, 0))),
        ("left", Vector((-1, 0, 0)), depth, lambda s: Vector((-width / 2, s, 0))),
        ("right", Vector((1, 0, 0)), depth, lambda s: Vector((width / 2, s, 0))),
    )
    for side, normal, length, at in facades:
        bays = max(1, int(length // bay))
        spacing = length / bays
        along = Vector((1, 0, 0)) if side in ("front", "back") else Vector((0, 1, 0))
        for i in range(bays):
            s = -length / 2 + spacing * (i + 0.5)
            base = at(s)
            for floor in range(floors):
                z0 = 0 if floor == 0 else ground_h + (floor - 1) * floor_h
                h = ground_h if floor == 0 else floor_h
                if floor == 0 and side == "front" and storefront:
                    # Storefront: full-height glazing with mullions and an awning.
                    pane = base + normal * 0.02 + Vector((0, 0, 0.35 + (h - 0.9) / 2))
                    size = along * (spacing - 0.5) + Vector((0, 0, h - 0.9)) + normal * 0.02
                    b.box(pane, _abs(size), glass)
                    b.box(base + normal * 0.08 + Vector((0, 0, 0.18)), _abs(along * spacing + Vector((0, 0, 0.36)) + normal * 0.16), trim)
                    b.box(base + normal * 0.06 + Vector((0, 0, h - 0.3)), _abs(along * spacing + Vector((0, 0, 0.35)) + normal * 0.12), trim)
                    if awning is not None and rng.random() < 0.75:
                        b.box(base + normal * 0.9 + Vector((0, 0, h - 0.65)), _abs(along * (spacing - 0.2) + Vector((0, 0, 0.12)) + normal * 1.7), awning)
                    continue
                if floor == 0 and side == "front" and i == bays // 2:
                    # Entrance door.
                    b.box(base + normal * 0.03 + Vector((0, 0, 1.15)), _abs(along * 1.2 + Vector((0, 0, 2.3)) + normal * 0.06), slots["door"])
                    continue
                if rng.random() < 0.08:
                    continue  # occasional blank bay breaks repetition
                cz = z0 + h * 0.55
                b.box(base + normal * 0.015 + Vector((0, 0, cz)), _abs(along * window_w + Vector((0, 0, window_h)) + normal * 0.03), glass)
                # Frame: head, sill, jambs.
                b.box(base + normal * t + Vector((0, 0, cz + window_h / 2 + 0.06)), _abs(along * (window_w + 0.3) + Vector((0, 0, 0.12)) + normal * t * 2), trim)
                b.box(base + normal * (t + 0.03) + Vector((0, 0, cz - window_h / 2 - 0.05)), _abs(along * (window_w + 0.35) + Vector((0, 0, 0.1)) + normal * (t * 2 + 0.06)), trim)
                for side_sign in (-1, 1):
                    b.box(base + along * side_sign * (window_w / 2 + 0.05) + normal * t + Vector((0, 0, cz)), _abs(along * 0.1 + Vector((0, 0, window_h)) + normal * t * 2), trim)
        # Floor bands.
        for floor in range(1, floors):
            z = ground_h + (floor - 1) * floor_h
            b.box(at(0) + normal * 0.05 + Vector((0, 0, z)), _abs(along * (length + 0.1) + Vector((0, 0, 0.22)) + normal * 0.1), trim)


def _abs(v):
    return Vector((abs(v.x), abs(v.y), abs(v.z)))


def storefront_block(name, width=16.0, depth=14.0, floors=2, style="brick", awning_color=(0.12, 0.35, 0.25), seed=1, collection=None):
    rng = random.Random(seed)
    b = _Builder(name)
    mat, color, rough = STYLES[style]
    slots = {
        "wall": b.slot(mat, color, rough),
        "glass": b.slot("M_Glass_Dark", (0.08, 0.1, 0.12), 0.08, 0.2),
        "trim": b.slot("M_Trim_White", (0.85, 0.84, 0.8), 0.6),
        "door": b.slot("M_Door_Wood", (0.25, 0.16, 0.1), 0.7),
        "awning": b.slot("M_Awning_%02d" % (seed % 100), awning_color, 0.8),
        "roof": b.slot("M_Roof_Tar", (0.12, 0.12, 0.12), 0.95),
        "metal": b.slot("M_Metal_Grey", (0.5, 0.52, 0.55), 0.4, 0.8),
    }
    height = GROUND_FLOOR_HEIGHT + (floors - 1) * FLOOR_HEIGHT
    b.box((0, 0, height / 2), (width, depth, height), slots["wall"])
    b.box((0, 0, height + 0.05), (width - 0.3, depth - 0.3, 0.1), slots["roof"])
    parapet = 0.9
    for sx, sy, sw, sd in ((0, -depth / 2 + 0.15, width, 0.3), (0, depth / 2 - 0.15, width, 0.3), (-width / 2 + 0.15, 0, 0.3, depth), (width / 2 - 0.15, 0, 0.3, depth)):
        b.box((sx, sy, height + parapet / 2), (sw, sd, parapet), slots["wall"])
    b.box((0, -depth / 2 - 0.05, height + parapet - 0.1), (width + 0.2, 0.3, 0.25), slots["trim"])  # cornice
    for _ in range(rng.randint(1, 3)):
        b.box((rng.uniform(-width / 3, width / 3), rng.uniform(-depth / 4, depth / 3), height + 0.6), (1.4, 1.1, 1.1), slots["metal"])
    _facade_windows(b, width, depth, floors, GROUND_FLOOR_HEIGHT, FLOOR_HEIGHT, 3.2, slots, True, rng)
    return b.finish(collection)


def office_tower(name, width=26.0, depth=22.0, floors=8, seed=2, collection=None):
    rng = random.Random(seed)
    b = _Builder(name)
    slots = {
        "wall": b.slot("M_Concrete_Grey", (0.58, 0.58, 0.56), 0.75),
        "glass": b.slot("M_Glass_Blue", (0.14, 0.2, 0.26), 0.05, 0.4),
        "trim": b.slot("M_Metal_Mullion", (0.32, 0.34, 0.36), 0.35, 0.9),
        "door": b.slot("M_Glass_Dark", (0.08, 0.1, 0.12), 0.08, 0.2),
        "roof": b.slot("M_Roof_Tar", (0.12, 0.12, 0.12), 0.95),
        "metal": b.slot("M_Metal_Grey", (0.5, 0.52, 0.55), 0.4, 0.8),
    }
    height = GROUND_FLOOR_HEIGHT + (floors - 1) * FLOOR_HEIGHT
    b.box((0, 0, height / 2), (width, depth, height), slots["wall"])
    b.box((0, 0, height + 0.05), (width - 0.4, depth - 0.4, 0.1), slots["roof"])
    b.box((0, 0, height + 1.8), (width * 0.35, depth * 0.35, 3.5), slots["metal"])  # mechanical penthouse
    _facade_windows(b, width, depth, floors, GROUND_FLOOR_HEIGHT, FLOOR_HEIGHT, 2.6, slots, True, rng, window_w=2.2, window_h=2.3)
    return b.finish(collection)


def warehouse(name, width=60.0, depth=40.0, height=10.0, seed=3, collection=None):
    rng = random.Random(seed)
    b = _Builder(name)
    wall = b.slot("M_Metal_Corrugated", (0.62, 0.64, 0.66), 0.45, 0.6)
    roof = b.slot("M_Metal_Roof", (0.5, 0.52, 0.5), 0.5, 0.7)
    door = b.slot("M_Door_Rollup", (0.35, 0.38, 0.42), 0.5, 0.7)
    trim = b.slot("M_Trim_Yellow", (0.85, 0.7, 0.15), 0.6)
    b.box((0, 0, height / 2), (width, depth, height), wall)
    b.prism_roof(-width / 2, width / 2, -depth / 2, depth / 2, height, 2.2, roof, overhang=0.4)
    # Loading docks on the front.
    docks = max(2, int(width // 9))
    for i in range(docks):
        x = -width / 2 + (i + 0.5) * width / docks
        b.box((x, -depth / 2 - 0.05, 2.4), (4.0, 0.12, 4.2), door)
        b.box((x, -depth / 2 - 1.0, 0.6), (4.6, 2.0, 1.2), trim if rng.random() < 0.2 else wall)
    b.box((0, -depth / 2 - 0.08, height - 1.2), (width * 0.6, 0.1, 1.2), trim)  # sign band
    return b.finish(collection)


def shotgun_house(name, width=5.2, depth=14.0, style="siding_white", seed=4, collection=None):
    """Raised single-storey shotgun house with front porch and gable roof (Gulf Coast vernacular)."""
    rng = random.Random(seed)
    b = _Builder(name)
    mat, color, rough = STYLES[style]
    wall = b.slot(mat, color, rough)
    trim = b.slot("M_Trim_White", (0.9, 0.9, 0.88), 0.6)
    glass = b.slot("M_Glass_Dark", (0.08, 0.1, 0.12), 0.08, 0.2)
    roof = b.slot("M_Roof_Shingle", (0.22, 0.2, 0.2), 0.9)
    pier = b.slot("M_Brick_Brown", (0.36, 0.24, 0.17), 0.85)
    door = b.slot("M_Door_Painted", (0.55, 0.12, 0.1) if rng.random() < 0.5 else (0.1, 0.25, 0.35), 0.6)
    raise_h = 0.8
    wall_h = 3.2
    porch = 2.2
    body_d = depth - porch
    y0 = -depth / 2 + porch
    # Piers.
    for px in (-width / 2 + 0.3, width / 2 - 0.3):
        for py in (y0 + 0.3, y0 + body_d / 2, depth / 2 - 0.3, -depth / 2 + 0.3):
            b.box((px, py, raise_h / 2), (0.4, 0.4, raise_h), pier)
    b.box((0, y0 + body_d / 2, raise_h + wall_h / 2), (width, body_d, wall_h), wall)
    # Porch deck, steps, columns.
    b.box((0, -depth / 2 + porch / 2, raise_h - 0.08), (width, porch, 0.16), trim)
    for step in range(3):
        b.box((0, -depth / 2 - 0.3 - step * 0.3, (raise_h - 0.1) * (2 - step) / 3 + 0.05), (1.4, 0.3, 0.1 + (raise_h - 0.1) * (2 - step) / 3), trim)
    for px in (-width / 2 + 0.2, width / 2 - 0.2):
        b.box((px, -depth / 2 + 0.2, raise_h + wall_h / 2), (0.18, 0.18, wall_h), trim)
    b.prism_roof(-width / 2, width / 2, -depth / 2, depth / 2, raise_h + wall_h, 2.2, roof)
    # Front door and windows (front + sides).
    b.box((-width / 4, y0 - 0.03, raise_h + 1.1), (0.95, 0.08, 2.2), door)
    b.box((width / 4, y0 - 0.03, raise_h + 1.5), (0.9, 0.06, 1.7), glass)
    for i in range(max(2, int(body_d // 3.2))):
        y = y0 + 1.6 + i * 3.2
        if y > depth / 2 - 1:
            break
        for sx in (-1, 1):
            b.box((sx * (width / 2 + 0.02), y, raise_h + 1.6), (0.05, 0.9, 1.6), glass)
            b.box((sx * (width / 2 + 0.05), y, raise_h + 0.72), (0.12, 1.1, 0.1), trim)
    obj = b.finish(collection)
    return obj


def apartment_block(name, width=40.0, depth=26.0, floors=4, style="brick_brown", seed=5, collection=None):
    rng = random.Random(seed)
    b = _Builder(name)
    mat, color, rough = STYLES[style]
    slots = {
        "wall": b.slot(mat, color, rough),
        "glass": b.slot("M_Glass_Dark", (0.08, 0.1, 0.12), 0.08, 0.2),
        "trim": b.slot("M_Trim_White", (0.85, 0.84, 0.8), 0.6),
        "door": b.slot("M_Door_Wood", (0.25, 0.16, 0.1), 0.7),
        "roof": b.slot("M_Roof_Tar", (0.12, 0.12, 0.12), 0.95),
        "rail": b.slot("M_Metal_Grey", (0.5, 0.52, 0.55), 0.4, 0.8),
    }
    height = FLOOR_HEIGHT * floors + 0.6
    b.box((0, 0, height / 2), (width, depth, height), slots["wall"])
    b.box((0, 0, height + 0.05), (width - 0.3, depth - 0.3, 0.1), slots["roof"])
    # Exterior walkways with railings (typical Gulf Coast garden apartments).
    for f in range(1, floors):
        z = FLOOR_HEIGHT * f + 0.3
        b.box((0, -depth / 2 - 0.9, z), (width, 1.8, 0.2), slots["trim"])
        b.box((0, -depth / 2 - 1.75, z + 0.55), (width, 0.06, 0.9), slots["rail"])
    for sx in (-width / 2 - 1.2, width / 2 + 1.2):
        b.box((sx, -depth / 2 - 0.9, height / 2 - 0.3), (2.2, 2.2, height - 0.6), slots["wall"])  # stair towers
    _facade_windows(b, width, depth, floors, FLOOR_HEIGHT + 0.6, FLOOR_HEIGHT, 3.4, slots, False, rng)
    return b.finish(collection)


def _window(b, center, along, normal, w, h, glass, trim, shutters=None):
    """A framed window with a sill (and optional shutters) on a wall facing ``normal``."""
    b.box(center + normal * 0.015, _abs(along * w + Vector((0, 0, h)) + normal * 0.03), glass)
    b.box(center + normal * 0.05 + Vector((0, 0, h / 2 + 0.05)), _abs(along * (w + 0.24) + Vector((0, 0, 0.1)) + normal * 0.1), trim)
    b.box(center + normal * 0.07 + Vector((0, 0, -h / 2 - 0.04)), _abs(along * (w + 0.3) + Vector((0, 0, 0.08)) + normal * 0.14), trim)
    for side in (-1, 1):
        b.box(center + along * side * (w / 2 + 0.04) + normal * 0.05, _abs(along * 0.08 + Vector((0, 0, h)) + normal * 0.1), trim)
        if shutters is not None:
            b.box(center + along * side * (w / 2 + 0.3) + normal * 0.04, _abs(along * 0.42 + Vector((0, 0, h + 0.1)) + normal * 0.05), shutters)
    # Muntin cross.
    b.box(center + normal * 0.035, _abs(along * 0.05 + Vector((0, 0, h)) + normal * 0.02), trim)
    b.box(center + normal * 0.035, _abs(along * w + Vector((0, 0, 0.05)) + normal * 0.02), trim)


def suburban_house(name, width=11.0, depth=13.0, floors=1, style="siding_white", roof="M_Roof_Shingle", garage=False, seed=6, collection=None):
    """Gulf Coast single-family house: raised slab, lap siding or brick, street-facing eaves, porch, chimney,
    shuttered windows, optional attached garage. Pivot at the footprint centre, front towards -Y."""
    rng = random.Random(seed)
    b = _Builder(name)
    mat, color, rough = STYLES[style]
    wall = b.slot(mat, color, rough)
    trim = b.slot("M_Trim_White", (0.9, 0.9, 0.88), 0.6)
    glass = b.slot("M_Glass_Dark", (0.08, 0.1, 0.12), 0.08, 0.2)
    roof_slot = b.slot(roof, (0.24, 0.22, 0.21) if roof == "M_Roof_Shingle" else (0.38, 0.18, 0.14), 0.9)
    slab = b.slot("M_Concrete_Grey", (0.58, 0.58, 0.56), 0.8)
    door = b.slot("M_Door_Painted_%02d" % (seed % 100), rng.choice(((0.55, 0.12, 0.1), (0.1, 0.25, 0.35), (0.15, 0.3, 0.18), (0.2, 0.2, 0.22))), 0.5)
    shutter = b.slot("M_Shutter_%02d" % (seed % 100), rng.choice(((0.1, 0.18, 0.12), (0.12, 0.14, 0.2), (0.2, 0.2, 0.2), (0.35, 0.12, 0.1))), 0.6)
    brick = b.slot("M_Brick_Red", (0.46, 0.2, 0.14), 0.85)
    raise_h, wall_h = 0.45, 2.9
    height = raise_h + floors * wall_h
    x0, x1, y0, y1 = -width / 2, width / 2, -depth / 2, depth / 2
    garage_w = 3.6 if garage else 0.0
    b.box((0, 0, raise_h / 2), (width + 0.2, depth + 0.2, raise_h), slab)
    b.box((0, 0, raise_h + floors * wall_h / 2), (width, depth, floors * wall_h), wall)
    pitch = min(3.2, depth * 0.28)
    b.gable_roof_x(x0, x1, y0, y1, height, pitch, roof_slot, overhang=0.45, gable_slot=wall)
    b.box((x0 + width * 0.22, y0 + depth * 0.62, height + pitch * 0.55), (0.9, 0.7, pitch * 1.3), brick)  # chimney
    front, along = Vector((0, -1, 0)), Vector((1, 0, 0))
    # Porch with posts and a shed roof over the door.
    door_x = x0 + garage_w + (width - garage_w) * (0.3 if rng.random() < 0.5 else 0.5)
    b.box((door_x, y0 - 0.02, raise_h + 1.1), (1.0, 0.06, 2.2), door)
    b.box((door_x, y0 - 0.05, raise_h + 2.28), (1.3, 0.1, 0.14), trim)
    porch_w = 3.2
    b.box((door_x, y0 - 0.9, raise_h - 0.08), (porch_w, 1.8, 0.16), slab)
    for step in range(2):
        b.box((door_x, y0 - 1.95 - step * 0.3, (raise_h - 0.05) * (1 - step / 2) / 2), (1.6, 0.3, (raise_h - 0.05) * (1 - step / 2)), slab)
    for px in (door_x - porch_w / 2 + 0.12, door_x + porch_w / 2 - 0.12):
        b.box((px, y0 - 1.7, raise_h + 1.3), (0.14, 0.14, 2.6), trim)
    b.box((door_x, y0 - 0.95, raise_h + 2.7), (porch_w + 0.3, 2.1, 0.14), roof_slot)
    if garage:
        b.box((x0 + garage_w / 2 + 0.2, y0 - 0.03, 1.2), (garage_w - 0.6, 0.06, 2.3), b.slot("M_Door_Garage", (0.88, 0.88, 0.86), 0.5))
        b.box((x0 + garage_w / 2 + 0.2, y0 - 1.5, 0.03), (garage_w, 3.0, 0.06), slab)  # driveway apron
    # Front windows on each floor, skipping the door and garage.
    for floor in range(floors):
        cz = raise_h + floor * wall_h + 1.55
        slots_x = []
        n = max(2, int((width - garage_w) // 2.8))
        for i in range(n):
            x = x0 + garage_w + (width - garage_w) * (i + 0.5) / n
            if floor == 0 and abs(x - door_x) < 1.6:
                continue
            slots_x.append(x)
        for x in slots_x:
            _window(b, Vector((x, y0, cz)), along, front, 1.1, 1.4, glass, trim, shutter)
    # Side and back windows.
    for floor in range(floors):
        cz = raise_h + floor * wall_h + 1.55
        for sx in (-1, 1):
            for y in (y0 + depth * 0.3, y0 + depth * 0.72):
                _window(b, Vector((sx * width / 2, y, cz)), Vector((0, 1, 0)), Vector((sx, 0, 0)), 1.0, 1.3, glass, trim)
        for i in range(max(2, int(width // 3.5))):
            _window(b, Vector((x0 + width * (i + 0.5) / max(2, int(width // 3.5)), y1, cz)), along, Vector((0, 1, 0)), 1.0, 1.3, glass, trim)
    b.box((0, y0 - 0.12, height - 0.12), (width + 0.9, 0.14, 0.18), trim)  # gutter/fascia
    b.box((0, y1 + 0.12, height - 0.12), (width + 0.9, 0.14, 0.18), trim)
    return b.finish(collection)


def gas_station(name, width=34.0, depth=26.0, seed=7, collection=None):
    """Canopy over two pump islands, a small convenience store behind, and a price sign."""
    rng = random.Random(seed)
    b = _Builder(name)
    concrete = b.slot("M_Concrete_Grey", (0.58, 0.58, 0.56), 0.8)
    canopy = b.slot("M_Canopy_White", (0.92, 0.92, 0.9), 0.5)
    band = b.slot("M_Canopy_Band_%02d" % (seed % 100), rng.choice(((0.75, 0.12, 0.1), (0.1, 0.35, 0.6), (0.12, 0.45, 0.25))), 0.5)
    pump = b.slot("M_Pump", (0.85, 0.85, 0.85), 0.4)
    glass = b.slot("M_Glass_Dark", (0.08, 0.1, 0.12), 0.08, 0.2)
    trim = b.slot("M_Trim_White", (0.85, 0.84, 0.8), 0.6)
    wall = b.slot("M_Stucco_White", (0.86, 0.85, 0.82), 0.9)
    roof = b.slot("M_Roof_Tar", (0.12, 0.12, 0.12), 0.95)
    metal = b.slot("M_Metal_Grey", (0.5, 0.52, 0.55), 0.4, 0.8)
    x0, y0 = -width / 2, -depth / 2
    b.box((0, 0, 0.05), (width, depth, 0.1), concrete)  # forecourt
    # Store at the back.
    sw, sd, sh = width * 0.55, depth * 0.35, 4.6
    sy = depth / 2 - sd / 2
    b.box((0, sy, sh / 2), (sw, sd, sh), wall)
    b.box((0, sy, sh + 0.05), (sw - 0.3, sd - 0.3, 0.1), roof)
    b.box((0, sy - sd / 2 - 0.02, 1.7), (sw * 0.7, 0.04, 2.6), glass)
    b.box((0, sy - sd / 2 - 0.06, sh - 0.5), (sw + 0.2, 0.12, 0.7), band)
    b.box((0, sy - sd / 2 - 0.05, 3.1), (sw * 0.72, 0.1, 0.12), trim)
    # Canopy and islands.
    cy, cw, cd, ch = y0 + depth * 0.33, width * 0.7, depth * 0.42, 5.2
    b.box((0, cy, ch), (cw, cd, 0.35), canopy)
    b.box((0, cy - cd / 2 - 0.03, ch), (cw + 0.06, 0.06, 0.5), band)
    b.box((0, cy + cd / 2 + 0.03, ch), (cw + 0.06, 0.06, 0.5), band)
    for sx in (-1, 1):
        b.box((sx * (cw / 2 + 0.03), cy, ch), (0.06, cd, 0.5), band)
    for ix in (-cw * 0.25, cw * 0.25):
        b.box((ix, cy, 0.15), (1.4, cd * 0.7, 0.3), concrete)
        for py in (-cd * 0.2, cd * 0.2):
            b.box((ix, cy + py, 1.05), (0.7, 0.5, 1.8), pump)
        b.box((ix, cy, ch / 2), (0.35, 0.35, ch), metal)
    # Price sign by the road.
    b.box((x0 + 1.5, y0 + 1.0, 3.0), (0.3, 0.3, 6.0), metal)
    b.box((x0 + 1.5, y0 + 1.0, 5.4), (2.2, 0.3, 1.6), band)
    return b.finish(collection)


def church(name, width=16.0, depth=28.0, seed=8, collection=None):
    """Brick nave with a front steeple, tall arched-style windows and double doors."""
    b = _Builder(name)
    wall = b.slot("M_Brick_Red", (0.46, 0.2, 0.14), 0.85)
    trim = b.slot("M_Stucco_White", (0.86, 0.85, 0.82), 0.9)
    glass = b.slot("M_Glass_Stained", (0.25, 0.2, 0.35), 0.15, 0.1)
    roof = b.slot("M_Roof_Shingle", (0.24, 0.22, 0.21), 0.9)
    door = b.slot("M_Door_Wood", (0.3, 0.19, 0.11), 0.7)
    wall_h = 8.0
    tower = 5.0
    y0 = -depth / 2 + tower
    b.box((0, y0 + (depth - tower) / 2, wall_h / 2), (width, depth - tower, wall_h), wall)
    b.prism_roof(-width / 2, width / 2, y0, depth / 2, wall_h, width * 0.45, roof, overhang=0.4)
    # Steeple: square tower, belfry band, spire.
    b.box((0, -depth / 2 + tower / 2, 7.0), (tower, tower, 14.0), wall)
    b.box((0, -depth / 2 + tower / 2, 14.2), (tower + 0.3, tower + 0.3, 0.4), trim)
    b.box((0, -depth / 2 + tower / 2, 15.6), (tower - 1.0, tower - 1.0, 2.6), trim)
    b.pyramid(0, -depth / 2 + tower / 2, 16.9, (tower - 0.8) / 2, 9.0, roof)
    b.box((0, -depth / 2 - 0.03, 1.8), (2.4, 0.08, 3.6), door)
    b.box((0, -depth / 2 - 0.06, 3.8), (3.0, 0.1, 0.4), trim)
    b.box((0, -depth / 2 - 0.03, 9.5), (1.4, 0.06, 2.4), glass)
    for i in range(5):
        y = y0 + (depth - tower) * (i + 0.5) / 5
        for sx in (-1, 1):
            b.box((sx * (width / 2 + 0.02), y, 4.4), (0.05, 1.2, 4.2), glass)
            b.box((sx * (width / 2 + 0.06), y, 6.6), (0.12, 1.5, 0.3), trim)
            b.box((sx * (width / 2 + 0.07), y, 2.2), (0.14, 1.5, 0.16), trim)
    return b.finish(collection)
