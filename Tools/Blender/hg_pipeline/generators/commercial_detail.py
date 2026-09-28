"""Detailed commercial, civic and apartment buildings: real construction instead of painted-on detail.

Shared vocabulary (all closed solids, never two faces sharing a plane where they overlap):
  * ``masonry``: a wall skin with true rectangular openings, so glass, doors and frames sit in real reveals;
  * ``storefront``: bulkhead, aluminium frame with mullions and transom, recessed entry vestibule with a glass door;
  * ``punched_window``: recessed glass with frame and meeting rail, stone sill and lintel (keystone on brick);
  * ``cornice``: frieze, dentil course, brackets and a stepped crown; parapets with metal coping;
  * ``rooftop``: membrane roof, packaged HVAC units with louvers and fan grilles, vent stacks, roof hatch.

Coordinates: Blender Z up, metres, footprint centred on the origin, front facing -Y.
"""
import math
import random

from mathutils import Vector

from .buildings import _Builder, STYLES
from .house_detail import Wall, hexa, sloped_roof, _subtract

GROUND = 4.2
FLOOR = 3.4


# ---------------------------------------------------------------------------------------------------------------- basics

def walls_of(width, depth, x0=None, y0=None):
    x0 = -width / 2 if x0 is None else x0
    y0 = -depth / 2 if y0 is None else y0
    x1, y1 = x0 + width, y0 + depth
    return {
        "front": Wall((x0, y0, 0), (1, 0, 0), (0, -1, 0), width),
        "back": Wall((x1, y1, 0), (-1, 0, 0), (0, 1, 0), width),
        "left": Wall((x0, y1, 0), (0, -1, 0), (-1, 0, 0), depth),
        "right": Wall((x1, y0, 0), (0, 1, 0), (1, 0, 0), depth),
    }


def masonry(b, wall, z0, z1, openings, slot, t=0.16):
    """Wall skin between z0 and z1 (depth -t..0) with rectangular holes [(s0, s1, z0, z1)]. Side walls (normal
    along X) stop t short of each end: the front and back skins own the corners, so no two skins overlap in a
    shared plane (that renders as a black seam in Cycles and z-fights in Unity)."""
    trim = t if abs(wall.n.x) > 0.5 else 0.0
    cuts = {z0, z1}
    for o in openings:
        cuts.add(min(z1, max(z0, o[2])))
        cuts.add(min(z1, max(z0, o[3])))
    cuts = sorted(cuts)
    for za, zb in zip(cuts, cuts[1:]):
        if zb - za < 1e-4:
            continue
        runs = [(trim, wall.length - trim)]
        for (s0, s1, oz0, oz1) in openings:
            if oz0 < zb - 1e-4 and oz1 > za + 1e-4:
                runs = _subtract(runs, s0, s1)
        for a, c in runs:
            if c - a > 1e-3:
                wall.box(b, a, c, za, zb, -t, 0.0, slot)


def frame_rect(b, wall, s0, s1, z0, z1, w, d0, d1, slot):
    """Four frame members inside an opening, butted (never overlapping)."""
    wall.box(b, s0, s0 + w, z0, z1, d0, d1, slot)
    wall.box(b, s1 - w, s1, z0, z1, d0, d1, slot)
    wall.box(b, s0 + w, s1 - w, z0, z0 + w, d0, d1, slot)
    wall.box(b, s0 + w, s1 - w, z1 - w, z1, d0, d1, slot)


def punched_window(b, wall, sc, zc, w, h, s, keystone=False, lintel=True):
    """Recessed one-over-one window with dark frame, meeting rail, stone sill and lintel. Returns its opening."""
    s0, s1, z0, z1 = sc - w / 2, sc + w / 2, zc - h / 2, zc + h / 2
    wall.box(b, s0, s1, z0, z1, -0.13, -0.115, s["glass"])
    frame_rect(b, wall, s0, s1, z0, z1, 0.06, -0.115, -0.07, s["frame"])
    wall.box(b, s0 + 0.06, s1 - 0.06, zc - 0.03, zc + 0.03, -0.115, -0.075, s["frame"])
    wall.box(b, s0 - 0.08, s1 + 0.08, z0 - 0.1, z0, -0.08, 0.06, s["stone"])            # sill
    if lintel:
        wall.box(b, s0 - 0.12, s1 + 0.12, z1, z1 + 0.24, 0.0, 0.03, s["stone"])           # lintel
        if keystone:
            wall.box(b, sc - 0.12, sc + 0.12, z1 - 0.04, z1 + 0.3, 0.03, 0.06, s["stone"])
    return (s0, s1, z0, z1)


def louvers(b, wall, s0, s1, z0, z1, slot, pitch=0.12, d_back=-0.05):
    """Tilted slats filling an opening (mechanical penthouses, belfries)."""
    z = z0 + 0.02
    while z + pitch * 0.9 < z1:
        c = [None] * 8
        for ai, s_ in enumerate((s0, s1)):
            c[ai * 4 + 0] = wall.p(s_, z + 0.02, d_back)
            c[ai * 4 + 1] = wall.p(s_, z + pitch * 0.95, d_back)
            c[ai * 4 + 2] = wall.p(s_, z, d_back + 0.06)
            c[ai * 4 + 3] = wall.p(s_, z + pitch * 0.75, d_back + 0.06)
        hexa(b, c, slot)
        z += pitch


def railing(b, wall, s0, s1, z, d, slot, height=1.05, pitch=0.12, post_every=3.0):
    """Steel railing along a wall-parallel line at offset d: top and bottom rails, posts, pickets."""
    wall.box(b, s0, s1, z + height - 0.05, z + height, d - 0.03, d + 0.03, slot)
    wall.box(b, s0, s1, z + 0.08, z + 0.12, d - 0.02, d + 0.02, slot)
    n_posts = max(1, int((s1 - s0) / post_every))
    for i in range(n_posts + 1):
        sp = s0 + (s1 - s0) * i / n_posts
        wall.box(b, sp - 0.03, sp + 0.03, z, z + height - 0.05, d - 0.035, d + 0.035, slot)
    x = s0 + pitch
    while x < s1 - pitch * 0.5:
        wall.box(b, x - 0.009, x + 0.009, z + 0.12, z + height - 0.05, d - 0.009, d + 0.009, slot)
        x += pitch


def cornice(b, wall, z, s, brackets=(), depth=1.0, s0=0.0, s1=None):
    """Frieze, dentil course, brackets and a stepped crown along a wall at height z (the crown's top)."""
    s1 = wall.length if s1 is None else s1
    wall.box(b, s0, s1, z - 0.62, z - 0.34, 0.0, 0.04 * depth, s["stone"])                    # frieze
    x = s0 + 0.12
    while x < s1 - 0.18:
        wall.box(b, x, x + 0.1, z - 0.34, z - 0.2, 0.0, 0.12 * depth, s["stone"])              # dentils
        x += 0.22
    wall.box(b, s0, s1, z - 0.2, z - 0.12, 0.0, 0.18 * depth, s["stone"])                     # bed moulding
    wall.box(b, s0 - 0.1, s1 + 0.1, z - 0.12, z - 0.04, 0.0, 0.3 * depth, s["stone"])          # corona
    wall.box(b, s0 - 0.14, s1 + 0.14, z - 0.04, z, 0.0, 0.36 * depth, s["stone"])              # crown
    for sp in brackets:
        wall.box(b, sp - 0.11, sp + 0.11, z - 0.7, z - 0.12, 0.04 * depth, 0.26 * depth, s["stone"])


def parapet_coping(b, width, depth, z, s, x0=None, y0=None):
    x0 = -width / 2 if x0 is None else x0
    y0 = -depth / 2 if y0 is None else y0
    cx, cy = x0 + width / 2, y0 + depth / 2
    b.box((cx, y0 + 0.08, z + 0.04), (width + 0.12, 0.36, 0.08), s["coping"])
    b.box((cx, y0 + depth - 0.08, z + 0.04), (width + 0.12, 0.36, 0.08), s["coping"])
    # Side runs fit between the front and back runs (touching, never overlapping).
    b.box((x0 + 0.08, cy, z + 0.04), (0.36, depth - 0.52, 0.08), s["coping"])
    b.box((x0 + width - 0.08, cy, z + 0.04), (0.36, depth - 0.52, 0.08), s["coping"])


def hvac_unit(b, cx, cy, z, s, w=1.8, d=1.2, h=1.1):
    """Packaged rooftop unit: curb, cabinet, louvered sides, fan shroud and grille on top."""
    b.box((cx, cy, z + 0.1), (w + 0.1, d + 0.1, 0.2), s["metal"])
    b.box((cx, cy, z + 0.2 + h / 2), (w, d, h), s["hvac"])
    side = Wall((cx - w / 2 + 0.1, cy - d / 2, 0), (1, 0, 0), (0, -1, 0), w - 0.2)
    louvers(b, side, 0.0, w * 0.55, z + 0.35, z + h - 0.05, s["hvac"], pitch=0.09, d_back=-0.03)
    for i, fx in enumerate((cx - w * 0.22, cx + w * 0.22)):
        b.box((fx, cy, z + 0.2 + h + 0.08), (0.62, 0.62, 0.16), s["hvac"])     # fan shroud
        b.box((fx, cy, z + 0.2 + h + 0.165), (0.5, 0.5, 0.01), s["vent"])       # opening
        for k in range(5):
            b.box((fx - 0.2 + k * 0.1, cy, z + 0.2 + h + 0.18), (0.012, 0.52, 0.012), s["metal"])  # grille bars
    b.box((cx + w / 2 + 0.02, cy, z + 0.6), (0.04, 0.3, 0.4), s["metal"])      # service panel


def rooftop(b, width, depth, z, s, rng, units=2, x0=None, y0=None):
    """Membrane roof inside the parapet with HVAC, vent stacks and a hatch."""
    x0 = -width / 2 if x0 is None else x0
    y0 = -depth / 2 if y0 is None else y0
    cx, cy = x0 + width / 2, y0 + depth / 2
    b.box((cx, cy, z + 0.02), (width - 0.34, depth - 0.34, 0.04), s["roof"])
    for i in range(units):
        hvac_unit(b, cx + rng.uniform(-width * 0.3, width * 0.3), cy + rng.uniform(-depth * 0.2, depth * 0.25), z + 0.04, s)
    for _ in range(3):
        vx, vy = cx + rng.uniform(-width * 0.4, width * 0.4), cy + rng.uniform(-depth * 0.4, depth * 0.4)
        b.box((vx, vy, z + 0.35), (0.12, 0.12, 0.62), s["metal"])
        b.box((vx, vy, z + 0.7), (0.2, 0.2, 0.08), s["metal"])
    hx, hy = cx - width * 0.35, cy + depth * 0.3
    b.box((hx, hy, z + 0.3), (0.95, 0.95, 0.5), s["metal"])
    b.box((hx, hy, z + 0.58), (1.05, 1.05, 0.06), s["metal"])


def slots_common(b, seed, rng):
    return {
        "stone": b.slot("M_Stone_Limestone", (0.78, 0.75, 0.68), 0.8),
        "frame": b.slot("M_Aluminium_Bronze", (0.16, 0.14, 0.12), 0.35, 0.8),
        "glass": b.slot("M_Glass_Shop", (0.05, 0.07, 0.08), 0.03, 0.3),
        "bulkhead": b.slot("M_Granite_Dark", (0.16, 0.15, 0.15), 0.35),
        "roof": b.slot("M_Roof_Membrane", (0.7, 0.7, 0.68), 0.85),
        "coping": b.slot("M_Metal_Coping", (0.55, 0.56, 0.56), 0.4, 0.8),
        "metal": b.slot("M_Metal_Grey", (0.5, 0.52, 0.55), 0.4, 0.8),
        "black": b.slot("M_Metal_Black", (0.04, 0.04, 0.045), 0.4, 0.8),
        "hvac": b.slot("M_Hvac_Beige", (0.72, 0.7, 0.62), 0.5, 0.3),
        "vent": b.slot("M_Vent_Dark", (0.12, 0.12, 0.12), 0.8),
        "interior": b.slot("M_Interior_Warm", (0.32, 0.28, 0.24), 0.9),
        "floor": b.slot("M_Concrete_Grey", (0.58, 0.58, 0.56), 0.8),
        "door": b.slot("M_Door_Steel", (0.3, 0.32, 0.34), 0.45, 0.6),
        "lamp": b.slot("M_Lamp_Emissive", (1.0, 0.85, 0.55), 0.3),
    }


def steel_door(b, wall, sc, z0, s, w=0.95, h=2.15):
    s0, s1 = sc - w / 2, sc + w / 2
    wall.box(b, s0, s1, z0, z0 + h, -0.1, -0.06, s["door"])
    frame_rect(b, wall, s0 - 0.06, s1 + 0.06, z0 - 0.001, z0 + h + 0.06, 0.06, -0.12, 0.02, s["frame"])
    wall.box(b, s1 - 0.2, s1 - 0.08, z0 + 1.0, z0 + 1.06, -0.06, -0.02, s["metal"])
    wall.box(b, sc - 0.12, sc + 0.12, z0 + h + 0.2, z0 + h + 0.36, 0.0, 0.14, s["black"])   # wall pack light
    wall.box(b, sc - 0.1, sc + 0.1, z0 + h + 0.19, z0 + h + 0.2, 0.02, 0.12, s["lamp"])
    return (s0 - 0.06, s1 + 0.06, z0, z0 + h + 0.06)


# ---------------------------------------------------------------------------------------------------------------- shops

def storefront(b, wall, s0, s1, top, s, entry=False):
    """One storefront bay between pilasters: granite bulkhead, bronze frame, mullions, transom; optionally a
    recessed entry with side lights and a glass door."""
    bh, tr = 0.5, 2.7
    if entry:
        dw = 1.1
        dc = (s0 + s1) / 2
        e0, e1 = dc - dw / 2 - 0.05, dc + dw / 2 + 0.05
        for (a, c) in ((s0, e0), (e1, s1)):
            if c - a > 0.3:
                storefront_panel(b, wall, a, c, top, s, bh, tr)
        rec = 1.2
        wall.box(b, e0, e1, 0.0, 0.03, -rec - 0.05, 0.0, s["bulkhead"])                   # vestibule floor
        wall.box(b, e0, e1, tr, top, -0.13, -0.115, s["glass"])                           # transom over the recess
        frame_rect(b, wall, e0, e1, tr - 0.001, top, 0.07, -0.115, -0.05, s["frame"])
        for a in (e0, e1 - 0.02):
            wall.box(b, a, a + 0.02, bh, tr, -rec, -0.13, s["glass"])                   # side lights
            wall.box(b, a - 0.03, a + 0.05, 0.0, bh, -rec, -0.13, s["frame"])
            wall.box(b, a - 0.03, a + 0.05, tr - 0.07, tr, -rec, -0.13, s["frame"])
        door = Wall(wall.p(e0 + 0.05, 0.0, -rec), wall.a, wall.n, e1 - e0 - 0.1)
        door.box(b, 0.1, door.length - 0.1, 0.1, 2.3, -0.02, 0.0, s["glass"])
        frame_rect(b, door, 0.0, door.length, 0.0, 2.45, 0.1, -0.03, 0.02, s["frame"])
        door.box(b, 0.1, door.length - 0.1, 0.9, 1.0, 0.02, 0.05, s["metal"])              # push bar
        door.box(b, 0.0, door.length, 2.45, tr, -0.03, 0.02, s["frame"])
    else:
        storefront_panel(b, wall, s0, s1, top, s, bh, tr)


def storefront_panel(b, wall, s0, s1, top, s, bh, tr):
    wall.box(b, s0, s1, 0.0, bh, -0.1, 0.03, s["bulkhead"])
    wall.box(b, s0, s1, bh, top, -0.13, -0.115, s["glass"])
    frame_rect(b, wall, s0, s1, bh, top, 0.07, -0.115, -0.05, s["frame"])
    wall.box(b, s0 + 0.07, s1 - 0.07, tr - 0.04, tr + 0.04, -0.115, -0.045, s["frame"])   # transom bar
    n = max(1, int((s1 - s0) / 1.5))
    for i in range(1, n):
        m = s0 + (s1 - s0) * i / n
        wall.box(b, m - 0.035, m + 0.035, bh + 0.07, tr - 0.04, -0.115, -0.05, s["frame"])
        wall.box(b, m - 0.035, m + 0.035, tr + 0.04, top - 0.07, -0.115, -0.05, s["frame"])


def awning(b, wall, s0, s1, top, slot, frame_slot, depth=1.35, drop=0.55, valance=0.25):
    """Sloped canvas awning with a valance and side cheeks on a steel frame."""
    c = [None] * 8
    for ai, s_ in enumerate((s0, s1)):
        c[ai * 4 + 0] = wall.p(s_, top - 0.03, 0.06)
        c[ai * 4 + 1] = wall.p(s_, top, 0.06)
        c[ai * 4 + 2] = wall.p(s_, top - drop - 0.03, depth)
        c[ai * 4 + 3] = wall.p(s_, top - drop, depth)
    hexa(b, c, slot)
    wall.box(b, s0, s1, top - drop - valance, top - drop, depth, depth + 0.025, slot)
    for s_ in (s0, s1 - 0.02):
        c = [None] * 8
        for ai, ss in enumerate((s_, s_ + 0.02)):
            c[ai * 4 + 0] = wall.p(ss, top - drop - 0.2, 0.06)
            c[ai * 4 + 1] = wall.p(ss, top - 0.035, 0.06)
            c[ai * 4 + 2] = wall.p(ss, top - drop - valance + 0.001, depth - 0.001)
            c[ai * 4 + 3] = wall.p(ss, top - drop - 0.035, depth - 0.001)
        hexa(b, c, slot)
    for s_ in (s0 + 0.05, s1 - 0.05):
        # Support arm from the wall to the front bar.
        c = [None] * 8
        for ai, ss in enumerate((s_ - 0.015, s_ + 0.015)):
            c[ai * 4 + 0] = wall.p(ss, top - drop - 0.45, 0.02)
            c[ai * 4 + 1] = wall.p(ss, top - drop - 0.42, 0.02)
            c[ai * 4 + 2] = wall.p(ss, top - drop - 0.07, depth - 0.05)
            c[ai * 4 + 3] = wall.p(ss, top - drop - 0.04, depth - 0.05)
        hexa(b, c, frame_slot)


def detailed_storefront(name, width=16.0, depth=14.0, floors=2, style="brick", awning_color=(0.12, 0.35, 0.25), seed=1, collection=None):
    """Main-street commercial block: storefronts between pilasters, sign band with a raised panel and gooseneck
    lamps, awnings, punched upper windows with sills and lintels, string courses, quoins (brick), cornice with
    dentils and brackets, parapet with coping, rooftop equipment, service door at the back."""
    rng = random.Random(seed)
    b = _Builder(name)
    mat, color, rough = STYLES[style]
    s = slots_common(b, seed, rng)
    s["wall"] = b.slot(mat, color, rough)
    s["sign"] = b.slot("M_Sign_%02d" % (seed % 100), rng.choice(((0.1, 0.18, 0.3), (0.35, 0.08, 0.06), (0.08, 0.2, 0.12), (0.12, 0.12, 0.12))), 0.4)
    s["awning"] = b.slot("M_Awning_%02d" % (seed % 100), awning_color, 0.85)
    s["letters"] = b.slot("M_Sign_Letters", (0.92, 0.88, 0.76), 0.4)
    brick = style.startswith("brick")
    H = GROUND + (floors - 1) * FLOOR
    par = 0.9
    x0, y0 = -width / 2, -depth / 2
    W = walls_of(width, depth)
    openings = {k: [] for k in W}

    # Interior backdrop (seen through the glass) and floor slab.
    b.box((0, 0.4, H / 2), (width - 1.8, depth - 2.6, H - 0.02), s["interior"])
    b.box((0, 0, 0.02), (width - 0.34, depth - 0.34, 0.04), s["floor"])

    # --- front: pilasters, storefront bays, sign band, awnings ---------------------------------------------
    front = W["front"]
    bays = max(2, round(width / 4.4))
    bay = width / bays
    pil = 0.5
    sf_top = 3.3
    entry_bay = bays // 2 if bays > 2 else rng.randint(0, bays - 1)
    for i in range(bays):
        a, c = i * bay + (pil / 2 if i else pil), (i + 1) * bay - (pil / 2 if i < bays - 1 else pil)
        openings["front"].append((a, c, 0.0, sf_top))
        storefront(b, front, a, c, sf_top, s, entry=(i == entry_bay))
        if i != entry_bay:
            awning(b, front, a - 0.05, c + 0.05, sf_top + 0.25, s["awning"], s["black"])
    for i in range(bays + 1):
        sp = min(max(i * bay, pil / 2), width - pil / 2)
        front.box(b, sp - pil / 2, sp + pil / 2, 0.0, 0.45, 0.0, 0.14, s["stone"])            # plinth
        front.box(b, sp - pil / 2 + 0.04, sp + pil / 2 - 0.04, 0.45, sf_top, 0.0, 0.08, s["wall"])  # pilaster
        front.box(b, sp - pil / 2, sp + pil / 2, sf_top, sf_top + 0.15, 0.0, 0.14, s["stone"])  # capital
    # Sign band: raised panel with a black border, gooseneck lamps above.
    front.box(b, 0.6, width - 0.6, sf_top + 0.3, GROUND - 0.3, 0.0, 0.06, s["sign"])
    frame_rect(b, front, 0.55, width - 0.55, sf_top + 0.25, GROUND - 0.25, 0.05, 0.0, 0.08, s["black"])
    # Raised channel letters: abstract blocks of varying width suggest a name without spelling one.
    letters = [rng.uniform(0.16, 0.34) if rng.random() > 0.15 else 0.0 for _ in range(rng.randint(7, 12))]
    run = sum(w_ if w_ else 0.22 for w_ in letters) + 0.07 * (len(letters) - 1)
    lx = width / 2 - run / 2
    lz = (sf_top + 0.3 + GROUND - 0.3) / 2
    for w_ in letters:
        if w_:
            front.box(b, lx, lx + w_, lz - 0.17, lz + 0.17, 0.06, 0.1, s["letters"])
        lx += (w_ if w_ else 0.22) + 0.07
    for i in range(bays):
        lx = (i + 0.5) * bay
        front.box(b, lx - 0.02, lx + 0.02, GROUND - 0.2, GROUND - 0.16, 0.0, 0.55, s["black"])  # arm
        front.box(b, lx - 0.13, lx + 0.13, GROUND - 0.3, GROUND - 0.2, 0.45, 0.72, s["black"])  # shade
        front.box(b, lx - 0.1, lx + 0.1, GROUND - 0.31, GROUND - 0.3, 0.48, 0.69, s["lamp"])
    front.box(b, 0.0, width, GROUND - 0.12, GROUND + 0.02, 0.0, 0.14, s["stone"])           # belt course

    # --- upper floors: windows aligned with the bays ---------------------------------------------------------
    for f in range(1, floors):
        zf = GROUND + (f - 1) * FLOOR
        for i in range(bays):
            for k in (0.3, 0.7):
                sc = (i + k) * bay
                openings["front"].append(punched_window(b, front, sc, zf + 1.65, 1.0, 1.8, s, keystone=brick))
        for key in ("left", "right"):
            n = max(1, int(depth / 4.0))
            for i in range(n):
                openings[key].append(punched_window(b, W[key], depth * (i + 0.5) / n, zf + 1.65, 0.9, 1.6, s, keystone=brick))
        n = max(2, int(width / 4.0))
        for i in range(n):
            openings["back"].append(punched_window(b, W["back"], width * (i + 0.5) / n, zf + 1.65, 0.9, 1.5, s, lintel=False))
        if f > 1:
            for key in W:
                W[key].box(b, 0.0, W[key].length, zf - 0.08, zf + 0.06, 0.0, 0.05, s["stone"])  # string course
    # Back: service door, meter box.
    openings["back"].append(steel_door(b, W["back"], width * 0.25, 0.0, s))
    W["back"].box(b, width * 0.55, width * 0.55 + 0.5, 1.2, 1.9, 0.0, 0.2, s["metal"])

    # --- wall skins, corners, cornice, parapet, roof --------------------------------------------------------
    for key, wall in W.items():
        z_from = sf_top if key == "front" else 0.0
        masonry(b, wall, z_from, H + par, [o for o in openings[key] if o[3] > z_from], s["wall"])
        if key == "front":
            masonry(b, wall, 0.0, sf_top, openings[key], s["wall"])
    for key in ("left", "right", "back"):
        W[key].box(b, 0.0, W[key].length, 0.0, 0.5, 0.0, 0.06, s["stone"])                    # water table
    if brick:
        for key in W:
            wall = W[key]
            z, k = 0.5, 0
            while z < H - 0.3:
                long = 0.5 if k % 2 == 0 else 0.3
                wall.box(b, 0.0, long, z, z + 0.28, 0.0, 0.035, s["stone"])                   # quoins
                wall.box(b, wall.length - long, wall.length, z, z + 0.28, 0.0, 0.035, s["stone"])
                z += 0.31
                k += 1
    cornice(b, front, H + 0.1, s, brackets=[min(max(i * bay, 0.3), width - 0.3) for i in range(bays + 1)])
    # Short cornice returns on the sides (slightly lower crown so no two top faces share a plane).
    cornice(b, W["right"], H + 0.095, s, depth=0.6, s0=0.0, s1=1.2)
    cornice(b, W["left"], H + 0.095, s, depth=0.6, s0=W["left"].length - 1.2, s1=W["left"].length)
    parapet_coping(b, width, depth, H + par, s)
    rooftop(b, width, depth, H, s, rng, units=1 + (width > 14) + (floors > 2))
    # Downspout with a conductor head at the back corner.
    b.box((x0 + 0.5, y0 + depth + 0.1, H - 0.3), (0.3, 0.2, 0.35), s["coping"])
    b.box((x0 + 0.5, y0 + depth + 0.08, (H - 0.45) / 2), (0.1, 0.08, H - 0.45), s["coping"])
    return b.finish(collection)


# ---------------------------------------------------------------------------------------------------------------- offices

def detailed_office(name, width=26.0, depth=22.0, floors=8, seed=2, collection=None):
    """Office building: stone-clad lobby with a recessed glass front and entrance canopy; curtain wall above with
    vertical mullion fins, floor transoms and spandrel bands; corner piers; crown parapet; louvered mechanical
    penthouse, rooftop units, antenna mast and window-washing davits."""
    rng = random.Random(seed)
    b = _Builder(name)
    s = slots_common(b, seed, rng)
    s["wall"] = b.slot("M_Concrete_Grey", (0.58, 0.58, 0.56), 0.75)
    s["spandrel"] = b.slot("M_Spandrel", (0.12, 0.14, 0.16), 0.2, 0.4)
    s["glass"] = b.slot("M_Glass_Blue", (0.1, 0.15, 0.2), 0.03, 0.4)
    s["interior"] = b.slot("M_Interior_Office", (0.42, 0.44, 0.46), 0.9)
    lobby = 4.6
    fh = 3.6
    H = lobby + (floors - 1) * fh
    crown = 1.4
    W = walls_of(width, depth)
    # Floor plates seen through the glass; the lobby backdrop sits behind the recessed lobby glass.
    b.box((0, 0, (lobby + H) / 2), (width - 2.0, depth - 2.0, H - lobby - 0.04), s["interior"])
    b.box((0, 0.7, lobby / 2), (width - 2.2, depth - 3.6, lobby - 0.1), s["interior"])
    b.box((0, 0, 0.02), (width - 0.3, depth - 0.3, 0.04), s["floor"])

    pier = 0.9
    for key, wall in W.items():
        L = wall.length
        # Side walls stop a few millimetres lower wherever two walls' pieces overlap at a corner, so no two faces
        # ever share a plane there.
        drop = 0.004 if key in ("left", "right") else 0.0
        # Lobby: glass set back 1.2 m behind stone columns on the front, 0.4 m elsewhere.
        rec = 1.2 if key == "front" else 0.4
        wall.box(b, pier, L - pier, 0.0, lobby, -rec - 0.02, -rec, s["glass"])
        n = max(2, int((L - 2 * pier) / 2.0))
        for i in range(n + 1):
            m = pier + (L - 2 * pier) * i / n
            wall.box(b, m - 0.04, m + 0.04, 0.0, lobby, -rec, -rec + 0.1, s["frame"])
        wall.box(b, pier, L - pier, lobby - 0.35, lobby, -rec, -rec + 0.12, s["frame"])
        wall.box(b, pier, L - pier, lobby - 0.05, lobby + 0.25, -rec, 0.0, s["wall"])     # soffit edge over the recess
        cols = max(2, int(L / 6.5))
        for i in range(cols + 1):
            m = min(max(L * i / cols, pier / 2), L - pier / 2)
            wall.box(b, m - pier / 2, m + pier / 2, 0.0, lobby - drop, -0.6, 0.06, s["stone"])
            wall.box(b, m - pier / 2 - 0.04, m + pier / 2 + 0.04, 0.0, 0.3 - drop, -0.62, 0.1, s["bulkhead"])
        # Curtain wall above the lobby: glass plane, spandrels at each slab, transoms and mullion fins.
        wall.box(b, pier, L - pier, lobby, H, -0.16, -0.14, s["glass"])
        for f in range(1, floors):
            zf = lobby + (f - 1) * fh
            wall.box(b, pier, L - pier, zf, zf + 0.95, -0.12, -0.06, s["spandrel"])
            wall.box(b, pier, L - pier, zf + 0.95, zf + 1.01, -0.14, 0.02, s["frame"])
            wall.box(b, pier, L - pier, zf - 0.03, zf + 0.03, -0.14, 0.03, s["frame"])
        bays = max(3, int((L - 2 * pier) / 1.5))
        for i in range(bays + 1):
            m = pier + (L - 2 * pier) * i / bays
            deep = 0.22 if i % 2 == 0 else 0.1
            wall.box(b, m - 0.035, m + 0.035, lobby, H, -0.14, deep, s["frame"])
        # Corner piers and crown.
        for m in (pier / 2, L - pier / 2):
            wall.box(b, m - pier / 2, m + pier / 2, lobby, H + crown - drop, -0.3, 0.05 if m < L / 2 else 0.049, s["stone"])
        wall.box(b, pier, L - pier, H, H + crown, -0.3, 0.02, s["stone"])
        wall.box(b, 0.0, L, H + crown, H + crown + 0.08 - drop, -0.36, 0.12, s["coping"])  # coping caps piers and crown
    # Entrance canopy with fascia and down lights; revolving-style door block in the recess.
    fr = W["front"]
    c0, c1 = width / 2 - 3.5, width / 2 + 3.5
    fr.box(b, c0, c1, 3.6, 3.9, 0.06, 3.0, s["frame"])
    fr.box(b, c0, c1, 3.9, 4.25, 2.9, 3.0, s["stone"])
    for k in range(4):
        lx = c0 + 1.0 + k * (c1 - c0 - 2.0) / 3
        fr.box(b, lx - 0.15, lx + 0.15, 3.59, 3.6, 1.2, 1.5, s["lamp"])
    for sx in (c0 + 0.1, c1 - 0.1):
        fr.box(b, sx - 0.08, sx + 0.08, 0.0, 3.6, 2.7, 2.86, s["frame"])
    door = Wall(fr.p(width / 2 - 1.6, 0.0, -1.2), fr.a, fr.n, 3.2)
    for k in range(2):
        d0 = 0.1 + k * 1.55
        door.box(b, d0, d0 + 1.45, 0.05, 2.5, 0.0, 0.03, s["glass"])
        frame_rect(b, door, d0 - 0.05, d0 + 1.5, 0.0, 2.55, 0.08, 0.0, 0.08, s["frame"])
    # Roof: membrane, penthouse with louvers, units, antenna, davits.
    b.box((0, 0, H + 0.02), (width - 0.6, depth - 0.6, 0.04), s["roof"])
    pw, pd, ph = width * 0.4, depth * 0.4, 4.0
    pen = walls_of(pw, pd)
    b.box((0, 0, H + ph / 2), (pw - 0.3, pd - 0.3, ph), s["vent"])
    for key, wall in pen.items():
        wall.box(b, 0.0, 0.3, H, H + ph, -0.3, 0.0, s["wall"])
        wall.box(b, wall.length - 0.3, wall.length, H, H + ph, -0.3, -0.001, s["wall"])
        louvers(b, wall, 0.3, wall.length - 0.3, H + 0.3, H + ph - 0.3, s["metal"], pitch=0.14, d_back=-0.12)
        wall.box(b, 0.3, wall.length - 0.3, H, H + 0.3, -0.3, 0.0, s["wall"])
        wall.box(b, 0.3, wall.length - 0.3, H + ph - 0.3, H + ph, -0.3, 0.0, s["wall"])
    b.box((0, 0, H + ph + 0.1), (pw + 0.3, pd + 0.3, 0.2), s["coping"])
    for k in range(2):
        hvac_unit(b, (-1) ** k * width * 0.32, depth * 0.3, H + 0.04, s, w=2.4, d=1.6, h=1.4)
    b.box((pw * 0.3, 0, H + ph + 4.0), (0.12, 0.12, 8.0), s["metal"])
    for k in range(3):
        b.box((pw * 0.3, 0, H + ph + 2.0 + k * 2.2), (0.9, 0.05, 0.05), s["metal"])
    for (dx, dy) in ((-width / 2 + 1.5, -depth / 2 + 1.0), (width / 2 - 1.5, -depth / 2 + 1.0), (-width / 2 + 1.5, depth / 2 - 1.0), (width / 2 - 1.5, depth / 2 - 1.0)):
        b.box((dx, dy, H + 1.0), (0.15, 0.15, 2.0), s["metal"])
        b.box((dx, dy - math.copysign(0.5, dy), H + 2.0), (0.12, 1.1, 0.12), s["metal"])
    return b.finish(collection)


# ---------------------------------------------------------------------------------------------------------------- apartments

def detailed_apartment(name, width=40.0, depth=26.0, floors=4, style="brick_brown", seed=5, walkways=True, collection=None):
    """Garden apartments (``walkways``): open exterior walkways with steel railings and posts, unit doors with
    lights and numbers, open steel stairs with landings at both ends. Mid-rise (no walkways): balconies with
    railings and sliding doors on the front and back, an entrance canopy. Both: punched windows with sills and
    lintels, wall AC sleeves, stone base, cornice and parapet, rooftop units."""
    rng = random.Random(seed)
    b = _Builder(name)
    mat, color, rough = STYLES[style]
    s = slots_common(b, seed, rng)
    s["wall"] = b.slot(mat, color, rough)
    s["rail"] = b.slot("M_Metal_Painted_Dark", (0.12, 0.13, 0.14), 0.45, 0.8)
    s["walk"] = b.slot("M_Concrete_Grey", (0.58, 0.58, 0.56), 0.8)
    s["door"] = b.slot("M_Door_Painted_%02d" % (seed % 100), rng.choice(((0.12, 0.2, 0.32), (0.3, 0.1, 0.08), (0.14, 0.24, 0.14))), 0.45)
    fh = 3.0
    H = floors * fh + 0.4
    par = 0.8
    W = walls_of(width, depth)
    openings = {k: [] for k in W}
    b.box((0, 0, H / 2), (width - 1.4, depth - 1.4, H - 0.02), s["interior"])
    unit = 7.5
    units = max(2, int(width / unit))
    uw = width / units
    for f in range(floors):
        zf = 0.4 + f * fh
        for i in range(units):
            u0 = i * uw
            # Front: a door and a window per unit (garden) or a balcony door and window (mid-rise).
            dc = u0 + uw * 0.3
            if walkways or f == 0:
                w = W["front"]
                o = (dc - 0.5, dc + 0.5, zf, zf + 2.15)
                w.box(b, o[0], o[1], zf, zf + 2.15, -0.12, -0.08, s["door"])
                frame_rect(b, w, o[0] - 0.06, o[1] + 0.06, zf - 0.001, zf + 2.21, 0.06, -0.14, 0.02, s["frame"])
                w.box(b, o[1] - 0.2, o[1] - 0.09, zf + 1.0, zf + 1.06, -0.08, -0.04, s["metal"])
                w.box(b, o[1] + 0.2, o[1] + 0.42, zf + 1.9, zf + 2.08, 0.0, 0.1, s["black"])   # light
                w.box(b, o[1] + 0.22, o[1] + 0.4, zf + 1.89, zf + 1.9, 0.02, 0.08, s["lamp"])
                w.box(b, o[1] + 0.22, o[1] + 0.4, zf + 1.55, zf + 1.7, 0.0, 0.02, s["stone"])   # number plate
                openings["front"].append((o[0] - 0.06, o[1] + 0.06, zf, zf + 2.21))
            else:
                w = W["front"]
                o = (dc - 0.9, dc + 0.9, zf, zf + 2.3)
                w.box(b, o[0], o[1], zf, zf + 2.3, -0.13, -0.115, s["glass"])
                frame_rect(b, w, o[0], o[1], zf, zf + 2.3, 0.06, -0.115, -0.07, s["frame"])
                w.box(b, dc - 0.03, dc + 0.03, zf + 0.06, zf + 2.24, -0.115, -0.07, s["frame"])
                openings["front"].append(o)
            openings["front"].append(punched_window(b, W["front"], u0 + uw * 0.72, zf + 1.5, 1.2, 1.4, s))
            if rng.random() < 0.35:
                W["front"].box(b, u0 + uw * 0.72 - 0.35, u0 + uw * 0.72 + 0.35, zf + 0.25, zf + 0.65, 0.0, 0.35, s["hvac"])  # AC sleeve
            # Back: two windows per unit (mid-rise: a balcony door and a window).
            for k in (0.3, 0.72):
                openings["back"].append(punched_window(b, W["back"], u0 + uw * k, zf + 1.5, 1.2, 1.4, s))
        for key in ("left", "right"):
            n = max(1, int(depth / 5.0))
            for i in range(n):
                openings[key].append(punched_window(b, W[key], depth * (i + 0.5) / n, zf + 1.5, 1.0, 1.4, s))
    for key, wall in W.items():
        masonry(b, wall, 0.0, H + par, openings[key], s["wall"])
        wall.box(b, 0.0, wall.length, 0.0, 0.5, 0.0, 0.06, s["stone"])
        cornice(b, wall, H + 0.05, s, depth=0.7, s0=0.0, s1=wall.length)
    parapet_coping(b, width, depth, H + par, s)
    rooftop(b, width, depth, H, s, rng, units=2 + floors // 4)

    fr = W["front"]
    if walkways:
        for f in range(1, floors):
            z = 0.4 + f * fh
            fr.box(b, 0.0, width, z - 0.2, z, 0.0, 1.8, s["walk"])
            fr.box(b, 0.0, width, z - 0.45, z - 0.2, 1.6, 1.8, s["rail"])         # edge beam
            railing(b, fr, 0.0, width, z, 1.75, s["rail"])
        for i in range(int(width / 6.0) + 1):
            sp = min(max(i * 6.0, 0.1), width - 0.1)
            fr.box(b, sp - 0.08, sp + 0.08, 0.0, 0.4 + (floors - 1) * fh - 0.45, 1.62, 1.78, s["rail"])  # columns
        # Open steel switchback stairs at both ends: two lanes, a flight up each lane per half storey, a mid landing
        # out front and a floor landing joined to the walkway.
        lane = 1.1
        for (sa, sb) in ((-2.3, 0.0), (width, width + 2.3)):
            lanes = ((sa + 0.03, sa + 0.03 + lane), (sb - 0.03 - lane, sb - 0.03))
            for f in range(floors - 1):
                z0 = 0.4 + f * fh
                zm, z1 = z0 + fh / 2, z0 + fh
                for (l0, l1), (za, zb, da, db) in zip(lanes, ((z0, zm, 1.9, 4.3), (zm, z1, 4.3, 1.9))):
                    steps = max(4, int((zb - za) / 0.18))
                    for k in range(steps):
                        t = (k + 1) / steps
                        zz = za + (zb - za) * t
                        dd = da + (db - da) * (k + 0.5) / steps
                        fr.box(b, l0, l1, zz - 0.04, zz, dd - 0.14, dd + 0.14, s["walk"])     # tread
                    for ss in (l0 - 0.03, l1):
                        c = [None] * 8
                        for ai, sq in enumerate((ss, ss + 0.03)):
                            c[ai * 4 + 0] = fr.p(sq, za - 0.25, da)
                            c[ai * 4 + 1] = fr.p(sq, zb - 0.25, db)
                            c[ai * 4 + 2] = fr.p(sq, za + 0.02, da)
                            c[ai * 4 + 3] = fr.p(sq, zb + 0.02, db)
                        hexa(b, c, s["rail"])                                                # stringers
                fr.box(b, sa, sb, zm - 0.12, zm, 4.3, 5.4, s["walk"])                         # mid landing
                fr.box(b, sa, sb, z1 - 0.2, z1, 0.0, 1.9, s["walk"])                          # floor landing
                railing(b, fr, sa, sb, zm, 5.37, s["rail"], post_every=1.2)
            for d_ in (0.05, 5.25):
                for s_ in (sa + 0.02, sb - 0.16):
                    fr.box(b, s_, s_ + 0.14, 0.0, 0.4 + (floors - 1) * fh - 0.2, d_, d_ + 0.14, s["rail"])  # stair posts
    else:
        # Balconies: slab, fascia and a railing at each balcony door, front and back.
        for f in range(1, floors):
            z = 0.4 + f * fh
            for i in range(units):
                for key in ("front", "back"):
                    wall = W[key]
                    bc = i * uw + uw * 0.3
                    wall.box(b, bc - 1.4, bc + 1.4, z - 0.18, z, 0.0, 1.4, s["walk"])
                    railing(b, wall, bc - 1.35, bc + 1.35, z, 1.35, s["rail"], post_every=1.35)
                    for sx_ in (bc - 1.35, bc + 1.35):
                        wall.box(b, sx_ - 0.015, sx_ + 0.015, z + 0.12, z + 1.0, 0.1, 1.35, s["rail"])
        c0, c1 = width / 2 - 2.5, width / 2 + 2.5
        fr.box(b, c0, c1, 3.0, 3.25, 0.0, 2.5, s["frame"])
        for sx_ in (c0 + 0.1, c1 - 0.1):
            fr.box(b, sx_ - 0.07, sx_ + 0.07, 0.0, 3.0, 2.3, 2.44, s["frame"])
    return b.finish(collection)


# ---------------------------------------------------------------------------------------------------------------- industrial

def detailed_warehouse(name, width=60.0, depth=40.0, height=10.0, seed=3, collection=None):
    """Distribution warehouse: tilt-up concrete base, 3D corrugated cladding, corner trims, dock-high loading doors
    with seals, bumpers and a steel canopy, man doors with steps and bollards, standing-seam roof with skylights,
    exhaust fans, gutters and downspouts, a blank sign panel and wall-pack lights."""
    rng = random.Random(seed)
    b = _Builder(name)
    s = slots_common(b, seed, rng)
    s["clad"] = b.slot("M_Metal_Corrugated", (0.62, 0.64, 0.66), 0.45, 0.6)
    s["roofm"] = b.slot("M_Metal_Roof", (0.5, 0.52, 0.5), 0.5, 0.7)
    s["dock"] = b.slot("M_Door_Rollup", (0.35, 0.38, 0.42), 0.5, 0.7)
    s["rubber"] = b.slot("M_Rubber", (0.03, 0.03, 0.03), 0.9)
    s["yellow"] = b.slot("M_Paint_Yellow", (0.85, 0.68, 0.1), 0.55)
    s["skylight"] = b.slot("M_Skylight", (0.8, 0.82, 0.78), 0.2)
    s["sign"] = b.slot("M_Sign_%02d" % (seed % 100), (0.1, 0.22, 0.4), 0.4)
    base = 1.2
    W = walls_of(width, depth)
    b.box((0, 0, height / 2), (width - 0.2, depth - 0.2, height), s["vent"])     # dark interior behind the cladding
    docks = max(2, int(width // 9))
    openings = {k: [] for k in W}
    fr = W["front"]
    for i in range(docks):
        dc = (i + 0.5) * width / docks
        o = (dc - 1.5, dc + 1.5, base, base + 3.0)
        openings["front"].append(o)
        # Sectional door with ribs and a vision strip.
        for k in range(6):
            zb = base + k * 0.5
            fr.box(b, o[0], o[1], zb + 0.01, zb + 0.49, -0.12, -0.08, s["dock"])
            fr.box(b, o[0] + 0.05, o[1] - 0.05, zb + 0.22, zb + 0.28, -0.08, -0.06, s["dock"])
        for k in range(5):
            fr.box(b, o[0] + 0.3 + k * 0.5, o[0] + 0.6 + k * 0.5, base + 1.62, base + 1.8, -0.081, -0.07, s["glass"])
        # Dock seal pads, bumpers, leveler lip, dock light.
        fr.box(b, o[0] - 0.35, o[0], base - 0.2, base + 3.3, 0.0, 0.3, s["rubber"])
        fr.box(b, o[1], o[1] + 0.35, base - 0.2, base + 3.3, 0.0, 0.3, s["rubber"])
        fr.box(b, o[0] - 0.35, o[1] + 0.35, base + 3.0, base + 3.4, 0.0, 0.3, s["rubber"])
        for bx in (o[0] + 0.2, o[1] - 0.45):
            fr.box(b, bx, bx + 0.25, base - 0.45, base - 0.05, 0.0, 0.14, s["rubber"])
        fr.box(b, o[0] + 0.2, o[1] - 0.2, base - 0.05, base, 0.0, 0.25, s["metal"])
        fr.box(b, o[1] + 0.5, o[1] + 0.62, base + 3.2, base + 3.3, 0.0, 0.8, s["black"])
    # Canopy over the docks on brackets with tie rods.
    fr.box(b, 0.5, width - 0.5, base + 4.1, base + 4.3, 0.0, 2.4, s["metal"])
    for i in range(docks + 1):
        bx = 0.5 + (width - 1.0) * i / docks
        c = [None] * 8
        for ai, ss in enumerate((bx - 0.02, bx + 0.02)):
            c[ai * 4 + 0] = fr.p(ss, base + 6.0, 0.0)
            c[ai * 4 + 1] = fr.p(ss, base + 6.05, 0.0)
            c[ai * 4 + 2] = fr.p(ss, base + 4.3, 2.3)
            c[ai * 4 + 3] = fr.p(ss, base + 4.35, 2.3)
        hexa(b, c, s["metal"])
    # Man doors on the sides with steps, a landing, a small canopy and bollards.
    for key in ("left", "right"):
        wall = W[key]
        dc = wall.length * 0.8 if key == "left" else wall.length * 0.2
        openings[key].append(steel_door(b, wall, dc, base, s))
        wall.box(b, dc - 1.0, dc + 1.0, 0.0, base, 0.0, 1.4, s["floor"])
        for k in range(4):
            wall.box(b, dc - 0.6, dc + 0.6, 0.0, base * (3 - k) / 4, 1.4 + k * 0.28, 1.68 + k * 0.28, s["floor"])
        wall.box(b, dc - 0.9, dc + 0.9, base + 2.55, base + 2.65, 0.0, 1.2, s["metal"])
        for bx in (dc - 1.3, dc + 1.3):
            wall.box(b, bx - 0.1, bx + 0.1, 0.0, 1.1, 2.6, 2.8, s["yellow"])
    # Tilt-up concrete base band and the corrugated cladding (ribs every 20 cm, skipped at openings).
    for key, wall in W.items():
        masonry(b, wall, 0.0, base, [o for o in openings[key] if o[2] < base], s["floor"], t=0.2)
        wall.box(b, 0.0, wall.length, base - 0.02, base + 0.06, 0.0, 0.08, s["metal"])   # base flashing
        masonry(b, wall, base, height, openings[key], s["clad"], t=0.12)
        x = 0.1
        while x < wall.length - 0.05:
            blocked = [o for o in openings[key] if o[0] - 0.05 < x < o[1] + 0.05]
            z0 = max([o[3] for o in blocked], default=base + 0.06)
            if key == "front" and 0.5 < x < wall.length - 0.5 and z0 < base + 4.35:
                segments = ((z0, base + 4.1), (base + 4.3, height - 0.2))
            else:
                segments = ((z0, height - 0.2),)
            for (za, zb) in segments:
                if zb - za > 0.1:
                    wall.box(b, x - 0.03, x + 0.03, za, zb, 0.0, 0.035, s["clad"])
            x += 0.2
        wall.box(b, -0.08, 0.0, base, height, -0.12, 0.08, s["metal"])                     # corner trim
    fr.box(b, width * 0.3, width * 0.7, height - 1.6, height - 0.4, 0.04, 0.1, s["sign"])
    frame_rect(b, fr, width * 0.3 - 0.05, width * 0.7 + 0.05, height - 1.65, height - 0.35, 0.05, 0.04, 0.12, s["black"])
    # Low-pitch standing-seam roof (ridge along the depth), ribs, skylights, fans, gutters and downspouts.
    rise = 1.4
    th = 0.12
    for side in (-1, 1):
        ex = side * (width / 2 + 0.3)
        edge_a, edge_b = Vector((ex, -depth / 2 - 0.3, height)), Vector((ex, depth / 2 + 0.3, height))
        ridge_a, ridge_b = Vector((0, -depth / 2 - 0.3, height + rise)), Vector((0, depth / 2 + 0.3, height + rise))
        n = (ridge_a - edge_a).cross(edge_b - edge_a).normalized()
        if n.z < 0:
            n = -n
        c = [None] * 8
        for ai, (e, r) in enumerate(((edge_a, ridge_a), (edge_b, ridge_b))):
            c[ai * 4 + 0], c[ai * 4 + 1] = e - n * th, r - n * th
            c[ai * 4 + 2], c[ai * 4 + 3] = e, r
        hexa(b, c, s["roofm"])
        y = -depth / 2
        while y < depth / 2:
            c = [None] * 8
            for ai, yy in enumerate((y - 0.025, y + 0.025)):
                e, r = Vector((ex, yy, height)), Vector((0.0, yy, height + rise))
                c[ai * 4 + 0], c[ai * 4 + 1] = e, r
                c[ai * 4 + 2], c[ai * 4 + 3] = e + n * 0.05, r + n * 0.05
            hexa(b, c, s["roofm"])
            y += 0.6
        for k in range(3):
            t = 0.35 + 0.1 * (k % 2)
            yy = -depth / 2 + depth * (k + 0.5) / 3
            p = edge_a.lerp(ridge_a, t) + Vector((0, yy + depth / 2 + 0.3, 0)) + n * 0.03
            b.box((p.x, p.y, p.z + 0.12), (1.2, 2.4, 0.2), s["skylight"])
        fx = side * width * 0.2
        b.box((fx, 0.0, height + rise * 0.6 + 0.4), (1.1, 1.1, 0.8), s["metal"])
        b.box((fx, 0.0, height + rise * 0.6 + 0.9), (1.4, 1.4, 0.1), s["metal"])
        b.box((ex + side * 0.12, 0.0, height - 0.1), (0.2, depth + 0.6, 0.2), s["coping"])      # gutter
        for k in range(4):
            yy = -depth / 2 + depth * (k + 0.5) / 4
            b.box((side * (width / 2 + 0.12), yy, (height - 0.2) / 2), (0.12, 0.12, height - 0.2), s["coping"])
    # Gable ends: cladding up into the triangle (the ridge runs along the depth).
    for key in ("front", "back"):
        wall = W[key]
        z = height
        while z < height + rise - 0.05:
            half = (width / 2) * (1 - (z + 0.2 - height) / rise)
            if half > 0.1:
                wall.box(b, width / 2 - half, width / 2 + half, z, z + 0.2, -0.12, 0.0, s["clad"])
            z += 0.2
    return b.finish(collection)


# ---------------------------------------------------------------------------------------------------------------- gas station

def detailed_gas_station(name, width=34.0, depth=26.0, seed=7, collection=None):
    """Forecourt canopy (thick fascia, lit soffit, clad columns) over raised pump islands with dispensers,
    bollards and bins; convenience store with a storefront, sign band, ice chest and air machine; price
    monument by the road."""
    rng = random.Random(seed)
    b = _Builder(name)
    s = slots_common(b, seed, rng)
    s["wall"] = b.slot("M_Stucco_White", (0.86, 0.85, 0.82), 0.9)
    s["band"] = b.slot("M_Canopy_Band_%02d" % (seed % 100), rng.choice(((0.75, 0.12, 0.1), (0.1, 0.35, 0.6), (0.12, 0.45, 0.25))), 0.4)
    s["canopy"] = b.slot("M_Canopy_White", (0.92, 0.92, 0.9), 0.5)
    s["pump"] = b.slot("M_Pump", (0.85, 0.85, 0.85), 0.4)
    s["rubber"] = b.slot("M_Rubber", (0.03, 0.03, 0.03), 0.9)
    s["yellow"] = b.slot("M_Paint_Yellow", (0.85, 0.68, 0.1), 0.55)
    s["pave"] = b.slot("M_Sidewalk", (0.6, 0.59, 0.56), 0.82)
    x0, y0 = -width / 2, -depth / 2
    b.box((0, 0, 0.03), (width, depth, 0.06), s["pave"])
    # Store.
    sw, sd, sh = width * 0.55, depth * 0.34, 4.6
    sy0 = depth / 2 - sd
    SW = walls_of(sw, sd, x0=-sw / 2, y0=sy0)
    b.box((0, sy0 + sd / 2 + 0.4, sh / 2), (sw - 1.6, sd - 2.4, sh - 0.04), s["interior"])
    op = {k: [] for k in SW}
    fr = SW["front"]
    a, c = sw * 0.15, sw * 0.85
    op["front"].append((a, c, 0.0, 3.0))
    dz0, dz1 = sw / 2 - 0.95, sw / 2 + 0.95
    storefront_panel(b, fr, a, dz0, 3.0, s, 0.5, 2.7)
    storefront_panel(b, fr, dz1, c, 3.0, s, 0.5, 2.7)
    # Double glass doors in the gap, a transom above.
    door = Wall(fr.p(dz0, 0.0, -0.1), fr.a, fr.n, dz1 - dz0)
    for k in range(2):
        d0 = k * (dz1 - dz0) / 2
        door.box(b, d0 + 0.08, d0 + (dz1 - dz0) / 2 - 0.08, 0.08, 2.32, -0.02, 0.0, s["glass"])
        frame_rect(b, door, d0, d0 + (dz1 - dz0) / 2, 0.0, 2.4, 0.08, -0.03, 0.02, s["frame"])
        door.box(b, d0 + 0.15, d0 + (dz1 - dz0) / 2 - 0.15, 1.0, 1.08, 0.02, 0.05, s["metal"])  # push bar
    fr.box(b, dz0, dz1, 2.4, 3.0, -0.13, -0.115, s["glass"])
    frame_rect(b, fr, dz0, dz1, 2.4, 3.0, 0.07, -0.115, -0.05, s["frame"])
    op["back"].append(steel_door(b, SW["back"], sw * 0.3, 0.06, s))
    for key, wall in SW.items():
        masonry(b, wall, 0.0, sh + 0.8, op[key], s["wall"])
        wall.box(b, 0.0, wall.length, 0.0, 0.45, 0.0, 0.05, s["bulkhead"])
    fr.box(b, 0.0, sw, 3.3, sh + 0.8, 0.0, 0.08, s["band"])
    frame_rect(b, fr, sw * 0.25, sw * 0.75, 3.55, sh + 0.55, 0.05, 0.08, 0.12, s["canopy"])
    parapet_coping(b, sw, sd, sh + 0.8, s, x0=-sw / 2, y0=sy0)
    rooftop(b, sw, sd, sh, s, rng, units=1, x0=-sw / 2, y0=sy0)
    fr.box(b, c + 0.4, c + 1.9, 0.0, 1.2, 0.0, 0.8, s["canopy"])                           # ice chest
    fr.box(b, c + 0.45, c + 1.85, 1.2, 1.25, 0.0, 0.75, s["band"])
    fr.box(b, a - 1.3, a - 0.6, 0.0, 1.5, 0.0, 0.5, s["band"])                             # air machine
    # Canopy: deck, fascia band all round, lit soffit, clad columns.
    cy, cw, cd, ch = y0 + depth * 0.33, width * 0.72, depth * 0.44, 5.4
    b.box((0, cy, ch + 0.35), (cw, cd, 0.5), s["canopy"])
    for (px, py, sx, sy) in ((0, cy - cd / 2, cw + 0.06, 0.06), (0, cy + cd / 2, cw + 0.06, 0.06), (-cw / 2, cy, 0.06, cd - 0.06), (cw / 2, cy, 0.06, cd - 0.06)):
        b.box((px, py, ch + 0.45), (sx, sy, 0.9), s["band"])
    for i in range(4):
        for j in range(2):
            lx = -cw / 2 + cw * (i + 0.5) / 4
            ly = cy - cd / 4 + j * cd / 2
            b.box((lx, ly, ch + 0.095), (1.1, 0.5, 0.01), s["lamp"])
    islands = (-cw * 0.25, cw * 0.25)
    for ix in islands:
        b.box((ix, cy, 0.15), (1.5, cd * 0.72, 0.18), s["floor"])
        b.box((ix, cy, 0.07), (1.7, cd * 0.72 + 0.2, 0.08), s["yellow"])
        b.box((ix, cy, ch / 2 + 0.2), (0.45, 0.45, ch - 0.1), s["canopy"])                  # clad column
        for py in (cy - cd * 0.22, cy + cd * 0.22):
            b.box((ix, py, 1.1), (0.75, 0.55, 1.7), s["pump"])                             # dispenser body
            for side in (-1, 1):
                b.box((ix + side * 0.38, py, 1.35), (0.02, 0.4, 0.3), s["glass"])          # screen
                b.box((ix + side * 0.38, py, 0.95), (0.03, 0.46, 0.35), s["band"])         # nozzle bay
                b.box((ix + side * 0.43, py - 0.1, 0.7), (0.05, 0.05, 0.9), s["rubber"])   # hose
            b.box((ix, py, 2.05), (0.8, 0.6, 0.25), s["band"])                             # topper
            for by in (py - 0.55, py + 0.55):
                b.box((ix + 0.55, by, 0.55), (0.14, 0.14, 0.8), s["yellow"])               # bollards
        b.box((ix - 0.55, cy, 0.7), (0.4, 0.4, 0.8), s["metal"])                           # bin
    # Price monument by the road.
    mx, my = x0 + 2.2, y0 + 1.4
    b.box((mx, my, 0.4), (2.6, 0.7, 0.8), s["bulkhead"])
    b.box((mx, my, 2.6), (2.2, 0.4, 3.6), s["band"])
    for k in range(3):
        b.box((mx, my - 0.21, 1.4 + k * 0.9), (1.8, 0.02, 0.6), s["black"])
    b.box((mx, my, 4.55), (2.4, 0.5, 0.3), s["canopy"])
    return b.finish(collection)


# ---------------------------------------------------------------------------------------------------------------- church

def _prism(b, pts, d0, d1, wall, slot):
    """A triangular solid from three (s, z) points extruded between wall depths d0 and d1."""
    v = [b.bm.verts.new(tuple(wall.p(ps, pz, d))) for d in (d0, d1) for (ps, pz) in pts]
    for idx in ((0, 1, 2), (5, 4, 3), (0, 3, 4, 1), (1, 4, 5, 2), (2, 5, 3, 0)):
        f = b.bm.faces.new([v[i] for i in idx])
        f.material_index = slot


def pointed_window(b, wall, sc, z0, w, h, s):
    """Lancet window: glass with a pointed head, a central mullion, stone jambs, a pointed stone hood and a sill.
    Returns the rectangular opening to cut; the wedges beside the pointed head are filled here."""
    s0, s1 = sc - w / 2, sc + w / 2
    apex = z0 + h
    z_spring = apex - w * 0.8
    wall.box(b, s0, s1, z0, z_spring, -0.13, -0.115, s["glass"])
    _prism(b, ((s0, z_spring), (s1, z_spring), (sc, apex)), -0.13, -0.115, wall, s["glass"])
    # Wall infill beside the pointed head (the cut opening is rectangular).
    _prism(b, ((s0, z_spring), (sc, apex), (s0, apex)), -0.16, 0.0, wall, s["wall"])
    _prism(b, ((s1, z_spring), (s1, apex), (sc, apex)), -0.16, 0.0, wall, s["wall"])
    wall.box(b, sc - 0.02, sc + 0.02, z0, apex - 0.1, -0.115, -0.095, s["frame"])            # mullion
    wall.box(b, s0 - 0.12, s0, z0, z_spring, 0.0, 0.05, s["stone"])                          # jambs
    wall.box(b, s1, s1 + 0.12, z0, z_spring, 0.0, 0.05, s["stone"])
    for sgn in (-1, 1):
        # Hood: a stone bar from the springing point up to the apex, 14 cm deep, following the pointed head.
        p0 = Vector((sc + sgn * (w / 2 + 0.12), z_spring))
        p1 = Vector((sc, apex + 0.16))
        along = (p1 - p0).normalized()
        down = Vector((along.y, -along.x))
        if down.y > 0:
            down = -down
        front = 0.07 + (0.004 if sgn > 0 else 0.0)  # the two bars overlap at the apex: never coplanar
        c = [None] * 8
        for ai, off in enumerate((Vector((0, 0)), down * 0.14)):
            for ti, q in enumerate((p0, p1)):
                c[ai * 4 + 0 * 2 + ti] = wall.p(q.x + off.x, q.y + off.y, 0.0)
                c[ai * 4 + 1 * 2 + ti] = wall.p(q.x + off.x, q.y + off.y, front)
        hexa(b, c, s["stone"])
    wall.box(b, s0 - 0.15, s1 + 0.15, z0 - 0.12, z0, -0.05, 0.1, s["stone"])                # sill
    return (s0, s1, z0, apex)


def detailed_church(name, width=16.0, depth=28.0, seed=8, collection=None):
    """Brick church: nave with stepped buttresses between lancet windows, stone copings and a shingled roof;
    front tower with a pointed double door, belfry with louvered openings, pinnacles and a spire with a finial."""
    rng = random.Random(seed)
    b = _Builder(name)
    s = slots_common(b, seed, rng)
    s["wall"] = b.slot("M_Brick_Red", (0.46, 0.2, 0.14), 0.85)
    s["glass"] = b.slot("M_Glass_Stained", (0.25, 0.2, 0.35), 0.15, 0.1)
    s["door"] = b.slot("M_Door_Wood", (0.3, 0.19, 0.11), 0.7)
    s["roof"] = b.slot("M_Roof_Shingle", (0.24, 0.22, 0.21), 0.9)
    s["deck"] = b.slot("M_Soffit_White", (0.88, 0.88, 0.86), 0.6)
    s["trim"] = s["stone"]
    wall_h = 8.0
    tower = 5.2
    nave_y0 = -depth / 2 + tower
    nave_d = depth - tower
    NW = walls_of(width, nave_d, y0=nave_y0)
    op = {k: [] for k in NW}
    b.box((0, nave_y0 + nave_d / 2, wall_h / 2), (width - 1.2, nave_d - 1.2, wall_h), s["interior"])
    bays = 5
    for key in ("left", "right"):
        wall = NW[key]
        for i in range(bays):
            sc = nave_d * (i + 0.5) / bays
            op[key].append(pointed_window(b, wall, sc, 1.6, 1.3, 4.6, s))
        for i in range(bays + 1):
            sp = min(max(nave_d * i / bays, 0.35), nave_d - 0.35)
            wall.box(b, sp - 0.35, sp + 0.35, 0.0, 4.0, 0.0, 0.8, s["wall"])          # buttress, lower stage
            wall.box(b, sp - 0.35, sp + 0.35, 4.0, 6.8, 0.0, 0.45, s["wall"])         # upper stage
            wall.box(b, sp - 0.4, sp + 0.4, 3.9, 4.05, 0.0, 0.88, s["stone"])         # weathering
            wall.box(b, sp - 0.4, sp + 0.4, 6.8, 6.95, 0.0, 0.53, s["stone"])
    op["back"].append(pointed_window(b, NW["back"], width / 2, 2.0, 2.2, 5.2, s))
    for key, wall in NW.items():
        if key == "front":
            continue
        masonry(b, wall, 0.0, wall_h, op[key], s["wall"])
        wall.box(b, 0.0, wall.length, 0.0, 0.6, 0.0, 0.08, s["stone"])
        wall.box(b, 0.0, wall.length, wall_h - 0.2, wall_h, 0.0, 0.1, s["stone"])
    masonry(b, NW["front"], 0.0, wall_h, [], s["wall"])  # nave front beside the tower
    # Gable walls (back, and the part of the front beside the tower) up into the roof, brick.
    pitch = width * 0.5
    for key in ("back", "front"):
        wall = NW[key]
        z = wall_h
        while z < wall_h + pitch - 0.3:
            half = (width / 2) * (1 - (z + 0.3 - wall_h) / pitch) - 0.2
            if half > 0.1:
                wall.box(b, width / 2 - half, width / 2 + half, z, z + 0.3, -0.3, 0.0, s["wall"])
            z += 0.3
    for side in (-1, 1):
        ex = side * (width / 2 + 0.35)
        sloped_roof(b, Vector((ex, nave_y0 - 0.2, wall_h - 0.2)), Vector((ex, depth / 2 + 0.4, wall_h - 0.2)),
                    Vector((0, nave_y0 - 0.2, wall_h + pitch)), Vector((0, depth / 2 + 0.4, wall_h + pitch)), 0.2, s, rakes=True,
                    rake_gap=0.006 if side > 0 else 0.0)
    b.box((0, (nave_y0 + depth / 2) / 2 + 0.1, wall_h + pitch + 0.04), (0.3, nave_d + 0.4, 0.08), s["roof"])
    # Tower.
    TW = walls_of(tower, tower, y0=-depth / 2)
    th_ = 17.0
    top = {k: [] for k in TW}
    fr = TW["front"]
    dw, dh = 2.2, 3.6
    door_top = 0.6 + dh - 0.9
    top["front"].append((tower / 2 - dw / 2, tower / 2 + dw / 2, 0.6, door_top))
    fr.box(b, tower / 2 - dw / 2, tower / 2 + dw / 2, 0.6, door_top, -0.2, -0.15, s["door"])
    fr.box(b, tower / 2 - 0.02, tower / 2 + 0.02, 0.6, door_top, -0.15, -0.12, s["black"])
    for k in range(2):
        for (p0, p1) in ((0.2, 1.1), (1.5, 2.4)):
            fr.box(b, tower / 2 - dw / 2 + 0.15 + k * dw / 2, tower / 2 - 0.15 + k * dw / 2, 0.6 + p0, 0.6 + p1, -0.15, -0.13, s["door"])
    # Pointed glazed head over the double door (its own opening, stacked on the door's).
    top["front"].append(pointed_window(b, fr, tower / 2, door_top, dw, 2.2, s))
    # Stone steps up to the door, each block standing on the ground.
    for k in range(3):
        fr.box(b, tower / 2 - 2.0, tower / 2 + 2.0, 0.0, 0.2 * (k + 1), 0.4 + (2 - k) * 0.35, 0.75 + (2 - k) * 0.35, s["stone"])
    fr.box(b, tower / 2 - 2.0, tower / 2 + 2.0, 0.0, 0.6, 0.0, 0.4, s["stone"])
    top["front"].append(pointed_window(b, fr, tower / 2, 7.0, 1.1, 3.0, s))
    for key, wall in TW.items():
        L = wall.length
        # Belfry openings with louvers on all four faces.
        bo = (L / 2 - 0.9, L / 2 + 0.9, 12.0, 15.2)
        top[key].append(bo)
        louvers(b, wall, bo[0], bo[1], bo[2], bo[3] - 0.9, s["door"], pitch=0.18, d_back=-0.2)
        wall.box(b, bo[0], bo[1], bo[2] - 0.15, bo[2], -0.1, 0.12, s["stone"])
        masonry(b, wall, 0.0, th_, [o for o in top[key]] if key == "front" else [bo], s["wall"], t=0.3)
        wall.box(b, 0.0, L, 11.6, 11.85, 0.0, 0.12, s["stone"])
        # Top band: front and back runs wrap the corners, the sides fit between them.
        ext = 0.2 if key in ("front", "back") else 0.0
        wall.box(b, -ext, L + ext, th_ - 0.25, th_, 0.0, 0.2, s["stone"])
        for m in (0.3, L - 0.3):
            wall.box(b, m - 0.3, m + 0.3, 0.0, 11.6, 0.0, 0.25 if m < L / 2 else 0.249, s["wall"])  # corner buttress
    for (px, py) in ((-tower / 2 + 0.3, -depth / 2 + 0.3), (tower / 2 - 0.3, -depth / 2 + 0.3), (-tower / 2 + 0.3, -depth / 2 + tower - 0.3), (tower / 2 - 0.3, -depth / 2 + tower - 0.3)):
        b.box((px, py, th_ + 0.6), (0.45, 0.45, 1.2), s["stone"])
        b.pyramid(px, py, th_ + 1.2, 0.26, 1.3, s["stone"])
    b.box((0, -depth / 2 + tower / 2, th_ + 0.2), (tower - 1.0, tower - 1.0, 0.4), s["stone"])
    b.pyramid(0, -depth / 2 + tower / 2, th_ + 0.4, (tower - 1.2) / 2, 10.0, s["roof"])
    b.box((0, -depth / 2 + tower / 2, th_ + 10.6), (0.12, 0.12, 1.6), s["metal"])
    b.box((0, -depth / 2 + tower / 2, th_ + 11.0), (0.7, 0.12, 0.12), s["metal"])
    b.box((0, -depth / 2 + tower / 2, th_ / 2), (tower - 0.8, tower - 0.8, th_ - 0.1), s["interior"])
    return b.finish(collection)
