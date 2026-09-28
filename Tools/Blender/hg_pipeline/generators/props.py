"""Street furniture and props: parametric, UV-mapped, multi-material, pivot at ground level.

Modelled at street-level detail: tapered poles with base plates and anchor bolts, cobra-head luminaires with
lenses, slatted benches on cast legs, hydrants with bonnet, caps and operating nuts, bus shelters with framed glass,
an advert lightbox and a bench, dumpsters with ribs, lids, fork pockets and casters.
"""
import math

import bmesh
from mathutils import Matrix, Vector

from .. import geometry as g


def _cylinder(bm, center, radius, height, segments, slot, radius_top=None):
    geom = bmesh.ops.create_cone(bm, cap_ends=True, segments=segments, radius1=radius, radius2=radius if radius_top is None else radius_top, depth=height)
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
    """Tapered steel pole on a base plate with anchor bolts and a concrete footing; a curved arm made of segments;
    a cobra-head luminaire with a lens and a photocell."""
    bm = bmesh.new()
    metal = g.material("M_Metal_Painted_Dark", (0.12, 0.13, 0.14), 0.45, 0.8)
    lamp = g.material("M_Lamp_Emissive", (1.0, 0.85, 0.55), 0.3)
    concrete = g.material("M_Concrete_Grey", (0.55, 0.55, 0.53), 0.8)
    _cylinder(bm, (0, 0, 0.1), 0.3, 0.2, 16, 2)                                   # footing
    g.add_box(bm, (0, 0, 0.215), (0.36, 0.36, 0.03), 0)                            # base plate
    for (bx, by) in ((0.13, 0.13), (-0.13, 0.13), (0.13, -0.13), (-0.13, -0.13)):
        _cylinder(bm, (bx, by, 0.25), 0.015, 0.05, 6, 0)                           # anchor bolts
    _cylinder(bm, (0, 0, 0.45), 0.13, 0.45, 12, 0, radius_top=0.1)                 # base cover
    _cylinder(bm, (0, 0, (height + 0.6) / 2), 0.095, height - 0.6, 12, 0, radius_top=0.065)
    # Curved arm: segments rising then levelling out.
    pts = [Vector((0.0, 0.0, height - 0.6))]
    for k in range(1, 7):
        t = k / 6
        pts.append(Vector((arm * t, 0.0, height - 0.6 + 0.45 * math.sin(t * math.pi / 2))))
    for a, c in zip(pts, pts[1:]):
        mid = (a + c) / 2
        d = c - a
        verts = _cylinder(bm, (0, 0, 0), 0.04, d.length + 0.02, 8, 0)
        rot = Vector((0, 0, 1)).rotation_difference(d.normalized()).to_matrix()
        bmesh.ops.rotate(bm, verts=verts, cent=(0, 0, 0), matrix=rot)
        bmesh.ops.translate(bm, vec=mid, verts=verts)
    head = pts[-1] + Vector((0.25, 0, -0.05))
    g.add_box(bm, (head.x, 0, head.z), (0.75, 0.32, 0.16), 0)                      # housing
    g.add_box(bm, (head.x + 0.05, 0, head.z - 0.1), (0.55, 0.26, 0.05), 1)         # lens
    g.add_box(bm, (head.x - 0.15, 0, head.z + 0.1), (0.08, 0.08, 0.05), 0)         # photocell
    return _finish(name, bm, [metal, lamp, concrete], collection)


def bench(name="SM_Bench_A", length=1.9, collection=None):
    """Slatted park bench: seat and back slats on three cast legs with armrests."""
    bm = bmesh.new()
    wood = g.material("M_Wood_Slats", (0.45, 0.3, 0.18), 0.7)
    metal = g.material("M_Metal_Painted_Dark", (0.12, 0.13, 0.14), 0.45, 0.8)
    for i in range(5):
        g.add_box(bm, (0, -0.2 + i * 0.095, 0.45), (length, 0.08, 0.035), 0)
    for i in range(4):
        z = 0.56 + i * 0.1
        g.add_box(bm, (0, 0.26 + i * 0.025, z), (length, 0.035, 0.075), 0)
    for sx in (-length / 2 + 0.12, 0.0, length / 2 - 0.12):
        g.add_box(bm, (sx, -0.16, 0.2), (0.05, 0.05, 0.4), 1)                      # front leg
        g.add_box(bm, (sx, 0.22, 0.2), (0.05, 0.05, 0.4), 1)                       # back leg
        g.add_box(bm, (sx, 0.03, 0.415), (0.05, 0.5, 0.04), 1)                     # seat rail
        g.add_box(bm, (sx, 0.29, 0.72), (0.05, 0.04, 0.55), 1)                     # back rail
        g.add_box(bm, (sx, 0.03, 0.06), (0.05, 0.5, 0.04), 1)                      # foot
    for sx in (-length / 2 + 0.12, length / 2 - 0.12):
        g.add_box(bm, (sx, 0.02, 0.64), (0.06, 0.5, 0.04), 1)                      # armrest
        g.add_box(bm, (sx, -0.2, 0.55), (0.04, 0.04, 0.2), 1)
    return _finish(name, bm, [wood, metal], collection)


def fire_hydrant(name="SM_FireHydrant_A", collection=None):
    """Dry-barrel hydrant: flange with bolts, barrel, bonnet with operating nut, pumper and hose nozzles with caps."""
    bm = bmesh.new()
    paint = g.material("M_Paint_Red", (0.6, 0.08, 0.06), 0.5)
    brass = g.material("M_Metal_Brass", (0.6, 0.48, 0.22), 0.35, 1.0)
    _cylinder(bm, (0, 0, 0.04), 0.17, 0.08, 16, 0)                                 # ground flange
    for k in range(8):
        a = k * math.pi / 4
        _cylinder(bm, (0.14 * math.cos(a), 0.14 * math.sin(a), 0.09), 0.012, 0.03, 6, 1)
    _cylinder(bm, (0, 0, 0.42), 0.12, 0.66, 16, 0)                                  # barrel
    _cylinder(bm, (0, 0, 0.1), 0.135, 0.06, 16, 0)
    _cylinder(bm, (0, 0, 0.77), 0.14, 0.06, 16, 0)                                  # bonnet flange
    _cylinder(bm, (0, 0, 0.84), 0.12, 0.1, 16, 0, radius_top=0.07)                  # bonnet
    _cylinder(bm, (0, 0, 0.92), 0.03, 0.06, 5, 1)                                   # operating nut (pentagon)
    nozzles = ((0.0, -1.0, 0.075), (1.0, 0.0, 0.05), (-1.0, 0.0, 0.05))  # pumper nozzle to the street, hose nozzles
    for (nx, ny, r) in nozzles:
        verts = _cylinder(bm, (0, 0, 0), r, 0.12, 12, 0)
        bmesh.ops.rotate(bm, verts=verts, cent=(0, 0, 0), matrix=Vector((0, 0, 1)).rotation_difference(Vector((nx, ny, 0))).to_matrix())
        bmesh.ops.translate(bm, vec=Vector((nx * 0.17, ny * 0.17, 0.55)), verts=verts)
        verts = _cylinder(bm, (0, 0, 0), r + 0.012, 0.04, 12, 1)                    # cap
        bmesh.ops.rotate(bm, verts=verts, cent=(0, 0, 0), matrix=Vector((0, 0, 1)).rotation_difference(Vector((nx, ny, 0))).to_matrix())
        bmesh.ops.translate(bm, vec=Vector((nx * 0.24, ny * 0.24, 0.55)), verts=verts)
        g.add_box(bm, (nx * 0.25, ny * 0.25, 0.55), (0.03 if nx == 0 else 0.02, 0.02 if nx == 0 else 0.03, 0.03), 1)  # cap nut
    return _finish(name, bm, [paint, brass], collection)


def dumpster(name="SM_Dumpster_A", collection=None):
    """Front-load dumpster: sloped front, reinforcing ribs, fork pockets, two split plastic lids, casters."""
    bm = bmesh.new()
    body = g.material("M_Paint_Green_Worn", (0.12, 0.3, 0.18), 0.6, 0.5)
    lid = g.material("M_Plastic_Black", (0.05, 0.05, 0.05), 0.5)
    rubber = g.material("M_Rubber", (0.03, 0.03, 0.03), 0.9)
    g.add_box(bm, (0, 0.05, 0.78), (1.9, 1.0, 1.16), 0)
    g.add_box(bm, (0, -0.52, 0.55), (1.9, 0.12, 0.7), 0)                            # sloped lower front
    for sx in (-0.9, -0.3, 0.3, 0.9):
        g.add_box(bm, (sx, -0.46, 0.85), (0.06, 0.06, 1.1), 0)                      # ribs
        g.add_box(bm, (sx, 0.56, 0.85), (0.06, 0.06, 1.1), 0)
    for sx in (-0.99, 0.99):
        g.add_box(bm, (sx, 0.05, 1.05), (0.1, 0.9, 0.18), 0)                        # fork pockets
    g.add_box(bm, (0, -0.5, 1.37), (1.96, 0.08, 0.06), 0)                           # top rim
    for sx in (-0.48, 0.48):
        g.add_box(bm, (sx, 0.08, 1.43), (0.94, 1.12, 0.05), 1)                      # lids
        g.add_box(bm, (sx, -0.49, 1.41), (0.9, 0.06, 0.05), 1)
    for sx in (-0.8, 0.8):
        for sy in (-0.35, 0.45):
            g.add_box(bm, (sx, sy, 0.17), (0.1, 0.1, 0.06), 0)                     # caster fork
            verts = _cylinder(bm, (0, 0, 0), 0.07, 0.05, 12, 2)
            bmesh.ops.rotate(bm, verts=verts, cent=(0, 0, 0), matrix=Matrix.Rotation(math.pi / 2, 3, "Y"))
            bmesh.ops.translate(bm, vec=Vector((sx, sy, 0.07)), verts=verts)
    return _finish(name, bm, [body, lid, rubber], collection)


def bollard(name="SM_Bollard_A", collection=None):
    """Steel pipe bollard with a domed cap, reflective bands and a concrete collar."""
    bm = bmesh.new()
    paint = g.material("M_Paint_Yellow", (0.85, 0.68, 0.1), 0.55)
    band = g.material("M_Reflective_White", (0.92, 0.92, 0.9), 0.3)
    concrete = g.material("M_Concrete_Grey", (0.55, 0.55, 0.53), 0.8)
    _cylinder(bm, (0, 0, 0.03), 0.17, 0.06, 16, 2)
    _cylinder(bm, (0, 0, 0.53), 0.1, 0.94, 16, 0)
    geom = bmesh.ops.create_uvsphere(bm, u_segments=16, v_segments=8, radius=0.1)
    bmesh.ops.translate(bm, vec=Vector((0, 0, 1.0)), verts=geom["verts"])
    for z in (0.78, 0.9):
        _cylinder(bm, (0, 0, z), 0.103, 0.06, 16, 1)
    return _finish(name, bm, [paint, band, concrete], collection)


def bus_shelter(name="SM_BusShelter_A", collection=None):
    """Bus shelter: steel posts on base plates, framed glass back and ends, a roof with fascia and gutter, an
    illuminated advert panel at one end, a perforated bench and a route sign."""
    bm = bmesh.new()
    frame = g.material("M_Metal_Painted_Dark", (0.12, 0.13, 0.14), 0.45, 0.8)
    glass = g.material("M_Glass_Clear", (0.6, 0.7, 0.72), 0.05)
    roof = g.material("M_Metal_Grey", (0.5, 0.52, 0.55), 0.4, 0.8)
    light = g.material("M_Lamp_Emissive", (1.0, 0.85, 0.55), 0.3)
    w, d, h = 3.6, 1.5, 2.5
    for sx in (-w / 2, -w / 6, w / 6, w / 2):
        for sy in (-d / 2, d / 2):
            if sy < 0 and abs(sx) < w / 2:
                continue
            g.add_box(bm, (sx, sy, h / 2), (0.08, 0.08, h), 0)
            g.add_box(bm, (sx, sy, 0.01), (0.2, 0.2, 0.02), 0)
    g.add_box(bm, (0, 0.05, h + 0.06), (w + 0.4, d + 0.5, 0.06), 2)                 # roof deck
    g.add_box(bm, (0, -d / 2 - 0.2, h + 0.02), (w + 0.4, 0.06, 0.2), 0)             # fascia
    g.add_box(bm, (0, d / 2 + 0.3, h), (w + 0.4, 0.1, 0.12), 2)                     # gutter
    # Glass back wall in three framed panes, glass end walls.
    for k in range(3):
        cx = -w / 2 + w * (k + 0.5) / 3
        g.add_box(bm, (cx, d / 2, 1.35), (w / 3 - 0.1, 0.012, 2.0), 1)
        g.add_box(bm, (cx, d / 2, 0.33), (w / 3 - 0.08, 0.05, 0.05), 0)
        g.add_box(bm, (cx, d / 2, 2.37), (w / 3 - 0.08, 0.05, 0.05), 0)
    g.add_box(bm, (w / 2, 0, 1.35), (0.012, d - 0.1, 2.0), 1)
    # Advert lightbox at the other end.
    g.add_box(bm, (-w / 2, 0, 1.3), (0.2, d - 0.1, 2.0), 0)
    for sx in (-w / 2 - 0.101, -w / 2 + 0.101):
        g.add_box(bm, (sx, 0, 1.3), (0.002, d - 0.3, 1.8), 3)
    # Perforated bench and route sign.
    g.add_box(bm, (0.3, d / 2 - 0.25, 0.46), (w * 0.55, 0.36, 0.03), 0)
    for sx in (0.3 - w * 0.25, 0.3 + w * 0.25):
        g.add_box(bm, (sx, d / 2 - 0.1, 0.23), (0.04, 0.04, 0.46), 0)
    g.add_box(bm, (w / 2 + 0.25, -d / 2, 2.1), (0.05, 0.05, 1.6), 0)
    g.add_box(bm, (w / 2 + 0.25, -d / 2, 2.75), (0.45, 0.03, 0.35), 2)
    return _finish(name, bm, [frame, glass, roof, light], collection)
