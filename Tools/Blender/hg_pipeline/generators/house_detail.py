"""Detailed single-family house: real 3D construction instead of painted-on detail.

Every element a viewer reads at street level is modelled:
  * lap siding as individual tapered boards (thick bottom lip, thin top) cut around every opening and carried up into
    the gable ends under the roof;
  * boxed eaves: grooved vinyl soffit panels, a vent strip, fascia, frieze board, K-style gutters and downspouts;
  * a sloped roof deck with rake boards on the gables, stepped 3D shingle courses and a ridge cap;
  * recessed windows (jamb liners, glass behind double-hung sashes with muntins), casings with drip caps, sills and
    louvered shutters; a six-panel front door; a sectional garage door;
  * a porch under a gabled portico, posts with bases and caps, a railing with balusters, steps; corner boards; a brick
    chimney with a crown; an AC condenser.

Coordinates: Blender Z up, metres, the footprint centred on the origin, the front facing -Y. Closed solids only
(``hexa``/boxes), so normals can be recalculated reliably.
"""
import math
import random

import bmesh
from mathutils import Matrix, Vector

from .buildings import _Builder

EXPOSURE = 0.16      # visible height of one siding board
BOARD_LIP = 0.022    # board thickness at its bottom edge
CORE = 0.0           # wall core face (siding sits on top of it, 0..BOARD_LIP)


def hexa(b, c, slot):
    """A closed solid from 8 corners indexed a*4 + o*2 + t (a: along 0/1, o: inner/outer, t: bottom/top)."""
    v = [b.bm.verts.new(tuple(p)) for p in c]
    for idx in ((0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)):
        f = b.bm.faces.new([v[i] for i in idx])
        f.material_index = slot


class Wall:
    """A wall's local frame: s along the wall, z up, d outwards from the core face."""

    def __init__(self, origin, along, normal, length):
        self.o, self.a, self.n, self.length = Vector(origin), Vector(along), Vector(normal), length

    def p(self, s, z, d):
        return self.o + self.a * s + Vector((0, 0, z)) + self.n * d

    def box(self, b, s0, s1, z0, z1, d0, d1, slot):
        """Axis-aligned (in wall space) solid."""
        c = [None] * 8
        for ai, s in enumerate((s0, s1)):
            for oi, d in enumerate((d0, d1)):
                for ti, z in enumerate((z0, z1)):
                    c[ai * 4 + oi * 2 + ti] = self.p(s, z, d)
        hexa(b, c, slot)

    def board(self, b, s0, s1, z0, z1, slot):
        """One siding board: lip BOARD_LIP proud at the bottom, tapering to 3 mm at the top."""
        c = [None] * 8
        for ai, s in enumerate((s0, s1)):
            c[ai * 4 + 0] = self.p(s, z0, CORE)
            c[ai * 4 + 1] = self.p(s, z1, CORE)
            c[ai * 4 + 2] = self.p(s, z0, BOARD_LIP)
            c[ai * 4 + 3] = self.p(s, z1 + 0.012, 0.003)  # top edge tucks under the board above
        hexa(b, c, slot)


def sloped_roof(b, edge_a, edge_b, ridge_a, ridge_b, th, s, courses=None, rakes=False, rake_gap=0.0):
    """A roof plane from an eave line (edge_a..edge_b) up to a ridge line: deck slab of thickness th plus stepped
    shingle courses (each thick at its butt, lapping the next); ``rakes`` adds trim boards over both slab ends."""
    n = (ridge_a - edge_a).cross(edge_b - edge_a).normalized()
    if n.z < 0:
        n = -n
    def top(t, a, lift=0.0):
        e = edge_a.lerp(edge_b, a)
        r = ridge_a.lerp(ridge_b, a)
        return e.lerp(r, t) + n * lift
    c = [None] * 8
    for ai in (0, 1):
        for ti in (0, 1):
            c[ai * 4 + 0 * 2 + ti] = top(ti, ai, -th)
            c[ai * 4 + 1 * 2 + ti] = top(ti, ai, 0.0)
    hexa(b, c, s["deck"])
    run = (ridge_a - edge_a).length
    courses = courses or max(4, int(run / 0.14))
    along = (edge_b - edge_a).normalized() * 0.02
    for i in range(courses):
        t0, t1 = i / courses, min(1.0, (i + 1.35) / courses)
        c = [None] * 8
        for ai, pad in ((0, -along), (1, along)):
            c[ai * 4 + 0] = top(t0, ai, 0.0) + pad
            c[ai * 4 + 1] = top(t1, ai, 0.0) + pad
            c[ai * 4 + 2] = top(t0, ai, 0.022) + pad
            c[ai * 4 + 3] = top(t1, ai, 0.006) + pad
        hexa(b, c, s["roof"])
    if rakes:
        out = (edge_b - edge_a).normalized()
        for ai, direction in ((0, -out), (1, out)):
            c = [None] * 8
            for k, (off, lift) in enumerate(((0.0, -th - 0.1), (0.0, 0.035), (0.03, -th - 0.1), (0.03, 0.035))):
                for ti in (0, 1):
                    p = top(ti, ai, lift) + direction * (off + 0.021 + rake_gap)
                    c[(k // 2) * 4 + (k % 2) * 2 + ti] = p
            hexa(b, c, s["trim"])


def _subtract(intervals, cut0, cut1):
    out = []
    for a, b in intervals:
        if cut1 <= a or cut0 >= b:
            out.append((a, b))
            continue
        if cut0 > a:
            out.append((a, cut0))
        if cut1 < b:
            out.append((cut1, b))
    return out


def siding(b, wall, z_from, z_to, openings, slot, span=None):
    """Lap siding rows between z_from and z_to, skipping openings [(s0, s1, z0, z1)].
    ``span(z_top)`` optionally limits a row to (s_min, s_max) (gable triangles)."""
    z = z_from
    while z < z_to - 0.02:
        z1 = min(z + EXPOSURE, z_to)
        runs = [(0.0, wall.length)] if span is None else [span(z1)]
        for (s0, s1, oz0, oz1) in openings:
            if oz0 < z1 and oz1 > z:
                runs = _subtract(runs, s0, s1)
        for s0, s1 in runs:
            if s1 - s0 > 0.05:
                wall.board(b, s0, s1, z, z1, slot)
        z = z1


def window(b, wall, sc, zc, w, h, s):
    """Double-hung window with casing, drip cap, sill, jamb liners, sashes, muntins and optional shutters."""
    trim, glass, shutter = s["trim"], s["glass"], s.get("shutter")
    s0, s1, z0, z1 = sc - w / 2, sc + w / 2, zc - h / 2, zc + h / 2
    wall.box(b, s0, s1, z0, z1, -0.06, -0.04, glass)                        # glass, recessed behind the siding
    # Jamb liners give the opening depth (pieces butt, never overlap: coincident faces render as dark specks).
    wall.box(b, s0, s0 + 0.04, z0, z1, -0.13, 0.0, trim)
    wall.box(b, s1 - 0.04, s1, z0, z1, -0.13, 0.0, trim)
    wall.box(b, s0 + 0.04, s1 - 0.04, z1 - 0.04, z1, -0.13, 0.0, trim)
    wall.box(b, s0 + 0.04, s1 - 0.04, z0, z0 + 0.04, -0.13, 0.0, trim)
    # Sashes: stiles, rails between them, meeting rail, muntins.
    a0, a1 = s0 + 0.09, s1 - 0.09
    wall.box(b, s0 + 0.04, s0 + 0.09, z0 + 0.04, z1 - 0.04, -0.045, -0.02, trim)
    wall.box(b, s1 - 0.09, s1 - 0.04, z0 + 0.04, z1 - 0.04, -0.045, -0.02, trim)
    wall.box(b, a0, a1, z0 + 0.04, z0 + 0.11, -0.045, -0.02, trim)
    wall.box(b, a0, a1, zc - 0.03, zc + 0.03, -0.045, -0.015, trim)
    wall.box(b, a0, a1, z1 - 0.1, z1 - 0.04, -0.045, -0.02, trim)
    for (m0, m1) in ((z0 + 0.11, zc - 0.03), (zc + 0.03, z1 - 0.1)):
        wall.box(b, sc - 0.012, sc + 0.012, m0, m1, -0.05, -0.035, trim)
        zm = (m0 + m1) / 2
        wall.box(b, a0, sc - 0.012, zm - 0.012, zm + 0.012, -0.05, -0.035, trim)
        wall.box(b, sc + 0.012, a1, zm - 0.012, zm + 0.012, -0.05, -0.035, trim)
    # Casing proud of the siding, head with a drip cap, sloped-looking sill with horns.
    cw = 0.1
    wall.box(b, s0 - cw, s0, z0 - 0.01, z1, 0.0, 0.045, trim)
    wall.box(b, s1, s1 + cw, z0 - 0.01, z1, 0.0, 0.045, trim)
    wall.box(b, s0 - cw, s1 + cw, z1, z1 + 0.14, 0.0, 0.045, trim)
    wall.box(b, s0 - cw - 0.03, s1 + cw + 0.03, z1 + 0.14, z1 + 0.17, 0.0, 0.075, trim)
    wall.box(b, s0 - cw - 0.04, s1 + cw + 0.04, z0 - 0.06, z0 - 0.01, -0.02, 0.085, trim)
    if shutter is not None:
        for side in (-1, 1):
            a0 = s0 - cw - 0.46 if side < 0 else s1 + cw + 0.02
            louvered_shutter(b, wall, a0, a0 + 0.44, z0, z1, shutter)
    return (s0 - cw, s1 + cw, z0 - 0.06, z1 + 0.17)


def louvered_shutter(b, wall, s0, s1, z0, z1, slot):
    zm = (z0 + z1) / 2
    for (a0, a1, b0, b1) in ((s0, s0 + 0.05, z0, z1), (s1 - 0.05, s1, z0, z1), (s0 + 0.05, s1 - 0.05, z0, z0 + 0.06),
                             (s0 + 0.05, s1 - 0.05, z1 - 0.06, z1), (s0 + 0.05, s1 - 0.05, zm - 0.03, zm + 0.03)):
        wall.box(b, a0, a1, b0, b1, 0.03, 0.065, slot)
    z = z0 + 0.06
    while z + 0.055 < z1 - 0.06:
        if abs(z + 0.03 - (z0 + z1) / 2) > 0.05:
            # A slat tilted down and out, so each casts a thin shadow line.
            c = [None] * 8
            for ai, s in enumerate((s0 + 0.05, s1 - 0.05)):
                c[ai * 4 + 0] = wall.p(s, z + 0.012, 0.035)
                c[ai * 4 + 1] = wall.p(s, z + 0.055, 0.035)
                c[ai * 4 + 2] = wall.p(s, z, 0.058)
                c[ai * 4 + 3] = wall.p(s, z + 0.043, 0.058)
            hexa(b, c, slot)
        z += 0.05


def panel_door(b, wall, sc, z0, w, h, s):
    trim, door = s["trim"], s["door"]
    s0, s1, z1 = sc - w / 2, sc + w / 2, z0 + h
    wall.box(b, s0, s1, z0, z1, -0.07, -0.03, door)
    for (a0, a1, b0, b1) in ((s0 - 0.03, s0, z0, z1), (s1, s1 + 0.03, z0, z1), (s0, s1, z1, z1 + 0.03)):
        wall.box(b, a0, a1, b0, b1, -0.13, 0.0, trim)
    # Six raised panels: two tall at the top, two short in the middle, two tall at the bottom.
    rows = ((0.12, 0.8), (0.95, 1.25), (1.4, 1.95))
    for (r0, r1) in rows:
        for (c0, c1) in ((0.1, w / 2 - 0.06), (w / 2 + 0.06, w - 0.1)):
            wall.box(b, s0 + c0, s0 + c1, z0 + r0 * h / 2.07, z0 + r1 * h / 2.07, -0.03, -0.018, door)
    wall.box(b, s1 - 0.14, s1 - 0.09, z0 + 0.95, z0 + 1.02, -0.03, 0.03, s["metal"])  # handle
    cw = 0.11
    wall.box(b, s0 - cw, s0 - 0.03, z0, z1 + 0.03, 0.0, 0.045, trim)
    wall.box(b, s1 + 0.03, s1 + cw, z0, z1 + 0.03, 0.0, 0.045, trim)
    wall.box(b, s0 - cw, s1 + cw, z1 + 0.03, z1 + 0.17, 0.0, 0.045, trim)
    wall.box(b, s0 - cw - 0.03, s1 + cw + 0.03, z1 + 0.17, z1 + 0.2, 0.0, 0.075, trim)
    wall.box(b, s1 + cw + 0.08, s1 + cw + 0.2, z1 - 0.4, z1 - 0.18, 0.0, 0.12, s["lamp"])  # porch light
    return (s0 - cw, s1 + cw, z0, z1 + 0.2)


def garage_door(b, wall, sc, z0, w, h, s):
    trim, gd = s["trim"], s["garage"]
    s0, s1 = sc - w / 2, sc + w / 2
    sections = 4
    sh = h / sections
    for i in range(sections):
        zb = z0 + i * sh
        wall.box(b, s0, s1, zb + 0.008, zb + sh - 0.008, -0.08, -0.05, gd)
        panels = 4
        pw = (w - 0.2) / panels
        for j in range(panels):
            wall.box(b, s0 + 0.1 + j * pw + 0.05, s0 + 0.1 + (j + 1) * pw - 0.05, zb + 0.1, zb + sh - 0.1, -0.05, -0.04, gd)
    cw = 0.13
    wall.box(b, s0 - cw, s0, z0, z0 + h, 0.0, 0.045, trim)
    wall.box(b, s1, s1 + cw, z0, z0 + h, 0.0, 0.045, trim)
    wall.box(b, s0 - cw, s1 + cw, z0 + h, z0 + h + 0.17, 0.0, 0.045, trim)
    for (a0, a1, b0, b1) in ((s0, s0 + 0.02, z0, z0 + h), (s1 - 0.02, s1, z0, z0 + h), (s0 + 0.02, s1 - 0.02, z0 + h - 0.02, z0 + h)):
        wall.box(b, a0, a1, b0, b1, -0.13, 0.0, trim)
    return (s0 - cw, s1 + cw, z0, z0 + h + 0.17)


def detailed_house(name, width=11.0, depth=13.0, floors=1, board="M_Board_White", board_color=(0.9, 0.9, 0.87),
                   roof="M_Roof_Shingle", garage=False, seed=6, collection=None, entry="front", raised=False, chimney=True):
    """``entry="left"`` puts the door and porch on a gable end and turns the house so that end faces the street
    (-Y): with ``width`` along the street being the long side before turning, this makes a shotgun house.
    ``raised`` stands the house on brick piers with a lattice skirt (Gulf Coast flood habit)."""
    rng = random.Random(seed)
    b = _Builder(name)
    s = {
        "board": b.slot(board, board_color, 0.7),
        "trim": b.slot("M_Trim_White", (0.9, 0.9, 0.88), 0.55),
        "glass": b.slot("M_Glass_Dark", (0.06, 0.08, 0.1), 0.05, 0.2),
        "soffit": b.slot("M_Soffit_White", (0.88, 0.88, 0.86), 0.6),
        "vent": b.slot("M_Vent_Dark", (0.12, 0.12, 0.12), 0.8),
        "gutter": b.slot("M_Gutter_White", (0.86, 0.86, 0.84), 0.35, 0.3),
        "roof": b.slot(roof, (0.24, 0.22, 0.21), 0.9),
        "deck": b.slot("M_Soffit_White", (0.88, 0.88, 0.86), 0.6),  # the deck underside is the painted rake/eave soffit
        "slab": b.slot("M_Concrete_Grey", (0.58, 0.58, 0.56), 0.8),
        "brick": b.slot("M_Brick_Red", (0.46, 0.2, 0.14), 0.85),
        "metal": b.slot("M_Metal_Grey", (0.5, 0.52, 0.55), 0.4, 0.8),
        "lamp": b.slot("M_Lamp_Brass", (0.55, 0.42, 0.2), 0.35, 0.9),
        "door": b.slot("M_Door_Painted_%02d" % (seed % 100), rng.choice(((0.5, 0.1, 0.08), (0.08, 0.2, 0.3), (0.12, 0.26, 0.15), (0.16, 0.16, 0.18))), 0.45),
        "shutter": b.slot("M_Shutter_%02d" % (seed % 100), rng.choice(((0.08, 0.15, 0.1), (0.1, 0.12, 0.18), (0.16, 0.16, 0.16), (0.3, 0.1, 0.08))), 0.55),
        "garage": b.slot("M_Door_Garage", (0.9, 0.9, 0.88), 0.5),
    }
    base = 0.9 if raised else 0.45
    wall_h = 2.9 if not raised else 3.2
    z_e = base + floors * wall_h                  # top of the walls (plate)
    x0, x1, y0, y1 = -width / 2, width / 2, -depth / 2, depth / 2
    ym = 0.0
    pitch = min(3.0, depth * 0.26)                 # ridge height above the plate
    k = pitch / (ym - y0)                          # roof rise per metre
    oh, rake = 0.45, 0.3                           # eave and gable overhangs
    th = 0.16                                      # roof deck thickness
    z_r = z_e + pitch

    # --- structure: foundation, wall core, gable core -------------------------------------------------------
    if raised:
        # Brick piers on a grid, a dark void behind a lattice skirt between them.
        b.box((0, 0, base / 2), (width - 0.44, depth - 0.44, base), s["vent"])
        for px in [x0 + 0.25 + i * (width - 0.5) / max(1, round(width / 2.4)) for i in range(round(width / 2.4) + 1)]:
            for py in (y0 + 0.25, y1 - 0.25):
                b.box((px, py, base / 2), (0.42, 0.42, base), s["brick"])
        for py in [y0 + 0.25 + i * (depth - 0.5) / max(1, round(depth / 2.4)) for i in range(1, round(depth / 2.4))]:
            for px in (x0 + 0.25, x1 - 0.25):
                b.box((px, py, base / 2), (0.42, 0.42, base), s["brick"])
        for key_, (ax, ay, nx, ny, length) in {"front": (x0, y0, 0, -1, width), "back": (x0, y1, 0, 1, width), "left": (x0, y0, -1, 0, depth), "right": (x1, y0, 1, 0, depth)}.items():
            for i in range(int(length / 0.09)):
                t = (i + 0.5) * 0.09
                cx_ = ax + (t if ny else 0) + nx * 0.18
                cy_ = ay + (t if nx else 0) + ny * 0.18
                b.box((cx_, cy_, base / 2 - 0.02), (0.045 if ny else 0.02, 0.02 if ny else 0.045, base - 0.16), s["trim"])
            # Top and bottom rails frame the lattice.
            mid = Vector((ax, ay, 0)) + Vector((ny != 0, nx != 0, 0)) * (length / 2) + Vector((nx, ny, 0)) * 0.19
            for zz in (0.06, base - 0.16):
                b.box((mid.x, mid.y, zz), (length if ny else 0.04, 0.04 if ny else length, 0.08), s["trim"])
        b.box((0, 0, base - 0.06), (width + 0.1, depth + 0.1, 0.12), s["trim"])  # sill beam
    else:
        b.box((0, 0, base / 2), (width + 0.1, depth + 0.1, base), s["slab"])
    # The wall core sits 12 cm behind the siding plane so window glass, doors and the garage door can be recessed
    # (the siding, casings and jamb liners hide the gap).
    b.box((0, 0, (base + z_e) / 2), (width - 0.24, depth - 0.24, z_e - base), s["board"])
    b.gable_roof_x(x0, x1, y0, y1, z_e, pitch - 0.02, s["board"], overhang=0.0)

    walls = {
        "front": Wall((x0, y0, 0), (1, 0, 0), (0, -1, 0), width),
        "back": Wall((x1, y1, 0), (-1, 0, 0), (0, 1, 0), width),
        "left": Wall((x0, y1, 0), (0, -1, 0), (-1, 0, 0), depth),
        "right": Wall((x1, y0, 0), (0, 1, 0), (1, 0, 0), depth),
    }
    openings = {k_: [] for k_ in walls}

    # --- doors and windows ------------------------------------------------------------------------------------
    ent = walls[entry]
    garage_w = 3.0 if garage and entry == "front" else 0.0
    if garage_w:
        openings[entry].append(garage_door(b, ent, 0.35 + garage_w / 2 + 0.15, base - 0.3, garage_w, 2.25, s))
        c_ = ent.p(0.5 + garage_w / 2, 0.0, 2.2)
        b.box((c_.x, c_.y, 0.04), (garage_w + 0.6, 4.4, 0.08), s["slab"])  # driveway apron
    living0 = 0.35 + garage_w + (0.6 if garage_w else 0.0)
    span_len = ent.length - living0
    door_s = living0 + span_len * (0.35 if rng.random() < 0.5 and entry == "front" else 0.5 if entry == "front" else 0.3)
    openings[entry].append(panel_door(b, ent, door_s, base, 0.95, 2.1, s))
    tall = 1.35 if not raised else 1.75
    for floor in range(floors):
        zc = base + floor * wall_h + (1.5 if not raised else 1.6)
        n = max(2 if entry == "front" else 1, int(span_len // 2.6))
        for i in range(n):
            sc = living0 + span_len * (i + 0.5) / n if entry == "front" else ent.length * 0.72
            if floor == 0 and abs(sc - door_s) < 1.4:
                continue
            if floor > 0 and garage_w and sc < living0:
                continue
            openings[entry].append(window(b, ent, sc, zc, 0.9, tall, s))
        for key_ in walls:
            if key_ == entry:
                continue
            wall = walls[key_]
            shutters = entry != "front" and key_ in ("front", "back")  # shotgun: shuttered side windows
            m = max(2, int(wall.length // 3.2))
            fr = [0.3, 0.72] if (entry == "front" and key_ in ("left", "right")) else [(i + 0.5) / m for i in range(m)]
            for f in fr:
                openings[key_].append(window(b, wall, wall.length * f, zc, 0.85, tall - 0.1, s if shutters else dict(s, shutter=None)))

    # --- siding, corner boards, frieze -------------------------------------------------------------------------
    z_soffit = z_e - oh * k - 0.2                  # underside of the boxed eave
    for key in ("front", "back"):
        siding(b, walls[key], base, z_soffit - 0.12, openings[key], s["board"])
        walls[key].box(b, 0.0, walls[key].length, z_soffit - 0.14, z_soffit + 0.02, 0.0, 0.03, s["trim"])  # frieze
    for key in ("left", "right"):
        wall = walls[key]
        siding(b, wall, base, z_e, openings[key], s["board"])

        def span(z_top, wall=wall):
            # Gable: stay under the roof deck (its underside at the wall is th/cos above the plate line).
            under = z_top - z_e + th * math.sqrt(1 + k * k)
            inset = under / k
            return (max(0.0, inset), min(wall.length, wall.length - inset))
        siding(b, wall, z_e, z_r - 0.1, [], s["board"], span=span)
    # Corner boards cover both walls' board ends at each corner.
    for (cx, cy) in ((x0, y0), (x1, y0), (x0, y1), (x1, y1)):
        top = z_soffit
        b.box((cx + math.copysign(0.02, cx), cy + math.copysign(0.02, cy), (base + top) / 2), (0.15, 0.15, top - base), s["trim"])

    # --- roof: deck slabs, fascia, soffit, gutters, rakes, shingles, ridge -------------------------------------
    def slope_point(side, u, x, lift=0.0):
        """Point on the top of the roof deck: side -1 front / +1 back, u 0 at the eave edge .. 1 at the ridge."""
        edge_y = y0 - oh if side < 0 else y1 + oh
        edge_z = z_e - oh * k
        y = edge_y + (ym - edge_y) * u
        z = edge_z + (z_r - edge_z) * u
        n = Vector((0, side * k, 1)).normalized()
        return Vector((x, y, z)) + n * lift

    xa, xb = x0 - rake, x1 + rake
    for side in (-1, 1):
        c = [None] * 8
        for ai, x in enumerate((xa, xb)):
            for ti, u in enumerate((0.0, 1.0)):
                c[ai * 4 + 0 * 2 + ti] = slope_point(side, u, x, -th)
                c[ai * 4 + 1 * 2 + ti] = slope_point(side, u, x, 0.0)
        hexa(b, c, s["deck"])
        # Shingle courses: each a wedge, thick at its butt edge, lapping the one above.
        edge = slope_point(side, 0.0, 0.0)
        ridge = slope_point(side, 1.0, 0.0)
        run = (ridge - edge).length
        courses = max(4, int(run / 0.14))
        for i in range(courses):
            u0, u1 = i / courses, min(1.0, (i + 1.35) / courses)
            c = [None] * 8
            for ai, x in enumerate((xa - 0.02, xb + 0.02)):
                c[ai * 4 + 0] = slope_point(side, u0, x, 0.0)
                c[ai * 4 + 1] = slope_point(side, u1, x, 0.0)
                c[ai * 4 + 2] = slope_point(side, u0, x, 0.022)
                c[ai * 4 + 3] = slope_point(side, u1, x, 0.006)
            hexa(b, c, s["roof"])
        # Fascia, boxed soffit (grooved panels + vent strip), gutter.
        ey = y0 - oh if side < 0 else y1 + oh
        wy = y0 if side < 0 else y1
        ez = z_e - oh * k
        fy = ey + side * 0.0
        b.box((0, fy - side * 0.015, ez - 0.1 - th / 2), (xb - xa + 0.06, 0.03, 0.22 + th), s["trim"])
        panel = 0.3
        n_panels = int((xb - xa) / panel)
        for i in range(n_panels):
            px = xa + (i + 0.5) * (xb - xa) / n_panels
            b.box((px, (ey + wy) / 2, z_soffit + 0.01), ((xb - xa) / n_panels - 0.018, abs(wy - ey), 0.02), s["soffit"])
        b.box((0, (ey + wy) / 2 + side * 0.08, z_soffit + 0.025), (xb - xa - 0.1, 0.08, 0.025), s["vent"])
        b.box((0, (ey + wy) / 2, z_soffit + 0.045), (xb - xa, abs(wy - ey), 0.02), s["deck"])  # closes the eave box
        gy = ey + side * 0.08
        b.box((0, gy, ez - 0.1), (xb - xa, 0.03, 0.14), s["gutter"])                     # gutter face
        b.box((0, gy - side * 0.065, ez - 0.16), (xb - xa, 0.13, 0.02), s["gutter"])      # gutter bottom
        for dx in (x0 + 0.3, x1 - 0.3):
            dy = wy + side * 0.1
            b.box((dx, (gy + dy) / 2, z_soffit - 0.06), (0.08, abs(gy - dy) + 0.08, 0.07), s["gutter"])  # offset under the soffit
            b.box((dx, dy, (z_soffit - 0.06) / 2 + 0.1), (0.08, 0.07, z_soffit - 0.26), s["gutter"])      # downspout on the wall
            b.box((dx, dy + side * 0.2, 0.1), (0.08, 0.4, 0.07), s["gutter"])                          # kick-out
    # Rake boards follow the slope at both gable ends; ridge cap along the top.
    for x, outward in ((xa - 0.015, -1), (xb + 0.015, 1)):
        for side in (-1, 1):
            c = [None] * 8
            shift = outward * (0.006 if side > 0 else 0.0)  # the two slopes' rakes must not share a plane at the peak
            for ai, xx in enumerate((x - 0.015 + shift, x + 0.015 + shift)):
                for ti, u in enumerate((0.0, 1.0)):
                    c[ai * 4 + 0 * 2 + ti] = slope_point(side, u, xx, -th - 0.12)
                    c[ai * 4 + 1 * 2 + ti] = slope_point(side, u, xx, 0.04)
            hexa(b, c, s["trim"])
    b.box((0, ym, z_r + 0.03), (xb - xa - 0.12, 0.3, 0.06), s["roof"])  # ridge cap, stopping inside the rakes
    for x, outward in ((xa - 0.015, -1), (xb + 0.015, 1)):
        b.box((x + outward * 0.012, ym, z_r - 0.02), (0.05, 0.42, 0.2), s["trim"])  # apex block, proud of both rakes

    # --- chimney, porch, AC ----------------------------------------------------------------------------------
    if chimney:
        cx, cy = x0 + width * (0.22 if entry == "front" else 0.6), y0 + depth * 0.62
        ch_top = z_r + 1.0
        b.box((cx, cy, (z_e + ch_top) / 2), (0.9, 0.7, ch_top - z_e), s["brick"])
        b.box((cx, cy, ch_top + 0.05), (1.05, 0.85, 0.1), s["slab"])
        b.box((cx, cy, ch_top + 0.25), (0.22, 0.22, 0.3), s["metal"])

    porch_w, porch_d = (3.2, 1.9) if entry == "front" else (min(ent.length - 0.4, 4.6), 2.2)
    pc = door_s if entry == "front" else ent.length / 2
    W = ent

    def wbox(s0, s1, z0, z1, d0, d1, slot):
        W.box(b, s0, s1, z0, z1, d0, d1, slot)

    wbox(pc - porch_w / 2, pc + porch_w / 2, base - 0.16, base, 0.0, porch_d, s["slab"])
    # Foundation under the porch: a brick base on slab houses; piers and lattice skirt on raised houses.
    ps0, ps1, pd1 = pc - porch_w / 2 + 0.04, pc + porch_w / 2 - 0.04, porch_d - 0.04
    if not raised:
        wbox(ps0, ps1, 0.0, base - 0.16, 0.0, pd1, s["brick"])
    else:
        wbox(ps0 + 0.2, ps1 - 0.2, 0.0, base - 0.16, 0.0, pd1 - 0.2, s["vent"])  # dark void behind the lattice
        for ps_ in (ps0 + 0.2, (ps0 + ps1) / 2, ps1 - 0.2):
            wbox(ps_ - 0.2, ps_ + 0.2, 0.0, base - 0.16, pd1 - 0.4, pd1, s["brick"])
        for ps_ in (ps0 + 0.2, ps1 - 0.2):
            wbox(ps_ - 0.2, ps_ + 0.2, 0.0, base - 0.16, 0.0, 0.4, s["brick"])
        # Lattice: front face between the piers, and both sides.
        a = ps0 + 0.42
        while a < ps1 - 0.44:
            if abs(a - (ps0 + ps1) / 2) > 0.22:
                wbox(a - 0.0225, a + 0.0225, 0.04, base - 0.2, pd1 - 0.2, pd1 - 0.18, s["trim"])
            a += 0.09
        for ps_ in (ps0 + 0.19, ps1 - 0.21):
            dd = 0.42
            while dd < pd1 - 0.42:
                wbox(ps_, ps_ + 0.02, 0.04, base - 0.2, dd - 0.0225, dd + 0.0225, s["trim"])
                dd += 0.09
    for i, step_h in enumerate((base * 2 / 3, base / 3)):
        wbox(pc - 0.75, pc + 0.75, 0.0, step_h, porch_d + i * 0.3, porch_d + (i + 1) * 0.3, s["slab"])
    post_h = 2.55
    post_d = porch_d - 0.15
    posts = (pc - porch_w / 2 + 0.1, pc + porch_w / 2 - 0.1)
    for ps in posts:
        wbox(ps - 0.12, ps + 0.12, base, base + 0.2, post_d - 0.12, post_d + 0.12, s["trim"])
        wbox(ps - 0.07, ps + 0.07, base, base + post_h, post_d - 0.07, post_d + 0.07, s["trim"])
        wbox(ps - 0.11, ps + 0.11, base + post_h - 0.1, base + post_h, post_d - 0.11, post_d + 0.11, s["trim"])
        # Railing along the porch side, balusters every 11 cm.
        wbox(ps - 0.03, ps + 0.03, base + 0.87, base + 0.93, 0.0, post_d, s["trim"])
        wbox(ps - 0.03, ps + 0.03, base + 0.08, base + 0.13, 0.0, post_d, s["trim"])
        dd = 0.1
        while dd < post_d - 0.1:
            wbox(ps - 0.0175, ps + 0.0175, base + 0.13, base + 0.87, dd - 0.0175, dd + 0.0175, s["trim"])
            dd += 0.11
    # Gabled portico over the porch (ridge away from the wall); on a one-storey house it ties into the main roof.
    z_beam = base + post_h
    wbox(pc - porch_w / 2 - 0.05, pc + porch_w / 2 + 0.05, z_beam, z_beam + 0.2, post_d - 0.09, post_d + 0.09, s["trim"])
    for ps in posts:
        wbox(ps - 0.09, ps + 0.09, z_beam, z_beam + 0.2, -0.1, post_d, s["trim"])
    rise = 0.95
    df, db = post_d + 0.35, -0.25
    zl = z_beam + 0.22
    for side in (-1, 1):
        es = pc + side * (porch_w / 2 + 0.25)
        sloped_roof(b, W.p(es, zl, df), W.p(es, zl, db), W.p(pc, zl + rise, df), W.p(pc, zl + rise, db), 0.12, s, 7, rakes=True,
                    rake_gap=0.006 if side > 0 else 0.0)  # never coplanar where the two rakes overlap at the peak
        wbox(es - 0.015 + side * 0.015, es + 0.015 + side * 0.015, zl - 0.18, zl + 0.02, db, df, s["trim"])  # fascia
    wbox(pc - 0.2, pc + 0.2, zl + rise - 0.12, zl + rise + 0.08, df + 0.018, df + 0.07, s["trim"])  # apex block, proud of both rakes
    # Pediment: the triangle under the front of the portico roof.
    v = [b.bm.verts.new(tuple(p)) for p in (W.p(pc - porch_w / 2 - 0.05, zl, post_d + 0.02), W.p(pc + porch_w / 2 + 0.05, zl, post_d + 0.02), W.p(pc, zl + rise - 0.1, post_d + 0.02),
                                           W.p(pc - porch_w / 2 - 0.05, zl, post_d - 0.12), W.p(pc + porch_w / 2 + 0.05, zl, post_d - 0.12), W.p(pc, zl + rise - 0.1, post_d - 0.12))]
    for idx in ((0, 1, 2), (5, 4, 3), (0, 2, 5, 3), (1, 4, 5, 2), (0, 3, 4, 1)):
        f = b.bm.faces.new([v[i] for i in idx])
        f.material_index = s["board"]
    wbox(pc - porch_w / 2 - 0.2, pc + porch_w / 2 + 0.2, zl - 0.06, zl - 0.03, db, df, s["soffit"])  # porch ceiling

    ac_x = x1 + 0.6
    b.box((ac_x, y0 + depth * 0.5, 0.05), (0.9, 0.9, 0.1), s["slab"])
    b.box((ac_x, y0 + depth * 0.5, 0.45), (0.75, 0.75, 0.7), s["metal"])
    b.box((ac_x, y0 + depth * 0.5, 0.81), (0.55, 0.55, 0.02), s["vent"])
    if entry == "left":
        # Turn the house so its entrance gable faces the street (-Y).
        bmesh.ops.rotate(b.bm, verts=b.bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(90), 3, "Z"))
    return b.finish(collection)
