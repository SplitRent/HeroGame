"""Street furniture and props: parametric, UV-mapped, multi-material, pivot at ground level."""
import math

import bmesh
from mathutils import Matrix, Vector

from .. import geometry as g


def _cylinder(bm, center, radius, height, segments, slot):
    geom = bmesh.ops.create_cone(bm, cap_ends=True, segments=segments, radius1=radius, radius2=radius, depth=height)
    bmesh.ops.translate(bm, vec=Vector(center), verts=geom["verts"])
    for f in {f for v in geom["verts"] for f in v.link_faces}:
        f.material_index = slot
    return geom["verts"]


def _finish(name, bm, mats, collection):
    obj = g.new_object(name, bm, collection)
    for m in mats:
        obj.data.materials.append(m)
    g.box_uv_project(obj, scale=1.0)
    return obj


def street_light(name="SM_StreetLight_A", height=8.0, arm=1.8, collection=None):
    bm = bmesh.new()
    metal = g.material("M_Metal_Painted_Dark", (0.12, 0.13, 0.14), 0.45, 0.8)
    lamp = g.material("M_Lamp_Emissive", (1.0, 0.85, 0.55), 0.3)
    concrete = g.material("M_Concrete_Grey", (0.55, 0.55, 0.53), 0.8)
    _cylinder(bm, (0, 0, 0.2), 0.22, 0.4, 12, 2)
    _cylinder(bm, (0, 0, height / 2), 0.09, height, 10, 0)
    g.add_box(bm, (arm / 2, 0, height - 0.15), (arm, 0.08, 0.08), 0)
    g.add_box(bm, (arm, 0, height - 0.28), (0.6, 0.3, 0.14), 0)
    g.add_box(bm, (arm, 0, height - 0.37), (0.5, 0.24, 0.04), 1)
    return _finish(name, bm, [metal, lamp, concrete], collection)


def bench(name="SM_Bench_A", length=1.9, collection=None):
    bm = bmesh.new()
    wood = g.material("M_Wood_Slats", (0.45, 0.3, 0.18), 0.7)
    metal = g.material("M_Metal_Painted_Dark", (0.12, 0.13, 0.14), 0.45, 0.8)
    for i in range(3):
        g.add_box(bm, (0, -0.18 + i * 0.16, 0.45), (length, 0.12, 0.04), 0)
    for i in range(2):
        g.add_box(bm, (0, 0.27, 0.62 + i * 0.14), (length, 0.04, 0.1), 0)
    for sx in (-length / 2 + 0.15, length / 2 - 0.15):
        g.add_box(bm, (sx, 0.02, 0.22), (0.06, 0.5, 0.44), 1)
        g.add_box(bm, (sx, 0.28, 0.6), (0.06, 0.04, 0.4), 1)
    return _finish(name, bm, [wood, metal], collection)


def fire_hydrant(name="SM_FireHydrant_A", collection=None):
    bm = bmesh.new()
    paint = g.material("M_Paint_Red", (0.6, 0.08, 0.06), 0.5)
    brass = g.material("M_Metal_Brass", (0.6, 0.48, 0.22), 0.35, 1.0)
    _cylinder(bm, (0, 0, 0.05), 0.16, 0.1, 12, 0)
    _cylinder(bm, (0, 0, 0.4), 0.12, 0.6, 12, 0)
    _cylinder(bm, (0, 0, 0.75), 0.13, 0.1, 12, 0)
    geom = bmesh.ops.create_uvsphere(bm, u_segments=12, v_segments=6, radius=0.11)
    bmesh.ops.translate(bm, vec=Vector((0, 0, 0.82)), verts=geom["verts"])
    for sx in (-1, 1):
        verts = _cylinder(bm, (0, 0, 0), 0.05, 0.14, 8, 1)
        bmesh.ops.rotate(bm, verts=verts, cent=(0, 0, 0), matrix=Matrix.Rotation(math.pi / 2, 3, "Y"))
        bmesh.ops.translate(bm, vec=Vector((sx * 0.16, 0, 0.5)), verts=verts)
    return _finish(name, bm, [paint, brass], collection)


def dumpster(name="SM_Dumpster_A", collection=None):
    bm = bmesh.new()
    body = g.material("M_Paint_Green_Worn", (0.12, 0.3, 0.18), 0.6, 0.5)
    lid = g.material("M_Plastic_Black", (0.05, 0.05, 0.05), 0.5)
    rubber = g.material("M_Rubber", (0.03, 0.03, 0.03), 0.9)
    g.add_box(bm, (0, 0, 0.75), (1.9, 1.1, 1.2), 0)
    g.add_box(bm, (0, 0.04, 1.4), (1.95, 1.2, 0.08), 1)
    for sx in (-0.8, 0.8):
        for sy in (-0.4, 0.4):
            _cylinder(bm, (sx, sy, 0.08), 0.08, 0.12, 8, 2)
    return _finish(name, bm, [body, lid, rubber], collection)


def bollard(name="SM_Bollard_A", collection=None):
    bm = bmesh.new()
    paint = g.material("M_Paint_Yellow", (0.85, 0.68, 0.1), 0.55)
    _cylinder(bm, (0, 0, 0.5), 0.1, 1.0, 12, 0)
    geom = bmesh.ops.create_uvsphere(bm, u_segments=12, v_segments=6, radius=0.1)
    bmesh.ops.translate(bm, vec=Vector((0, 0, 1.0)), verts=geom["verts"])
    return _finish(name, bm, [paint], collection)


def bus_shelter(name="SM_BusShelter_A", collection=None):
    bm = bmesh.new()
    frame = g.material("M_Metal_Painted_Dark", (0.12, 0.13, 0.14), 0.45, 0.8)
    glass = g.material("M_Glass_Clear", (0.6, 0.7, 0.72), 0.05)
    roof = g.material("M_Metal_Grey", (0.5, 0.52, 0.55), 0.4, 0.8)
    w, d, h = 3.6, 1.5, 2.5
    for sx in (-w / 2, w / 2):
        for sy in (-d / 2, d / 2):
            g.add_box(bm, (sx, sy, h / 2), (0.08, 0.08, h), 0)
    g.add_box(bm, (0, 0, h + 0.05), (w + 0.3, d + 0.3, 0.1), 2)
    g.add_box(bm, (0, d / 2, h / 2 + 0.2), (w, 0.02, h - 0.6), 1)
    for sx in (-w / 2, w / 2):
        g.add_box(bm, (sx, 0, h / 2 + 0.2), (0.02, d, h - 0.6), 1)
    g.add_box(bm, (0, d / 2 - 0.25, 0.45), (w * 0.7, 0.35, 0.05), 0)
    return _finish(name, bm, [frame, glass, roof], collection)
