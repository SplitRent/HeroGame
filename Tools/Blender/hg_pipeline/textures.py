"""Procedural tileable PBR textures for the environment kit (base colour + normal map).

Pure numpy (no bpy): every texture tiles seamlessly and covers ``TILE_METRES`` of surface, matching the kit's
world-space box-projected UVs (``geometry.box_uv_project(scale=2.0)``: 1 UV unit = 2 m). Each surface is built from a
height field (mortar joints, board laps, ribs, shingle rows) so the normal map and the colour agree. Placeholder art
(docs/ASSET_TRACKER.md): believable at street level, not hero quality.

    python -c "from hg_pipeline import textures; textures.write_all('out')"
"""
import os
import struct
import zlib

import numpy as np

SIZE = 512
TILE_METRES = 2.0
PX_PER_M = SIZE / TILE_METRES


# ---------------------------------------------------------------------- noise (seamless by construction)

def _noise(seed, scale_px, size=SIZE):
    """Smooth tileable noise in [0, 1]: white noise low-passed in the frequency domain (periodic by definition)."""
    rng = np.random.default_rng(seed)
    white = rng.standard_normal((size, size))
    f = np.fft.fftfreq(size)
    fx, fy = np.meshgrid(f, f)
    sigma = 1.0 / max(scale_px, 1e-3)
    spectrum = np.fft.fft2(white) * np.exp(-(fx ** 2 + fy ** 2) / (2 * sigma ** 2))
    out = np.real(np.fft.ifft2(spectrum))
    out -= out.min()
    return out / max(out.max(), 1e-9)


def _fbm(seed, base_px, octaves=4):
    total = np.zeros((SIZE, SIZE))
    amp, norm = 1.0, 0.0
    for o in range(octaves):
        total += amp * _noise(seed + o * 101, base_px / (2 ** o))
        norm += amp
        amp *= 0.5
    return total / norm


def _grid():
    y, x = np.mgrid[0:SIZE, 0:SIZE].astype(np.float64)
    return x / PX_PER_M, y / PX_PER_M  # metres; y grows downwards in image space


def _fit(size_m):
    """The nearest size that repeats a whole number of times per tile, so patterns meet themselves at the edges."""
    return TILE_METRES / max(1, round(TILE_METRES / size_m))


def _tint(base, variation, amount):
    """base (3,) colour modulated per pixel by ``variation`` in [0, 1] (±amount)."""
    v = (variation - 0.5) * 2 * amount
    return np.clip(np.asarray(base)[None, None, :] * (1 + v[..., None]), 0, 1)


# ---------------------------------------------------------------------- surfaces: (colour HxWx3 in 0..1, height HxW)

def brick(seed, color, mortar=(0.62, 0.6, 0.56)):
    """Running bond (about 215 x 65 mm bricks, 10 mm joints; sized to repeat exactly per tile)."""
    x, y = _grid()
    course, length, joint = _fit(0.075), _fit(0.225), 0.01
    rows, cols = round(TILE_METRES / course), round(TILE_METRES / length)
    row = np.floor(y / course).astype(int)
    xs = x + (row % 2) * length / 2
    col = np.floor(xs / length).astype(int)
    fy = (y % course) / course
    fx = (xs % length) / length
    in_joint = (fy < joint / course) | (fx < joint / length)
    rng = np.random.default_rng(seed)
    per_brick = rng.random((rows, cols))
    brick_var = per_brick[row % rows, col % cols]
    surface = _fbm(seed, 6)
    colour = _tint(color, 0.35 * brick_var + 0.65 * surface, 0.22)
    colour = np.where(in_joint[..., None], _tint(mortar, _fbm(seed + 7, 3), 0.1), colour)
    edge = np.minimum(np.minimum(fy, 1 - fy) * course, np.minimum(fx, 1 - fx) * length)
    height = np.clip(edge / 0.012, 0, 1) * 0.8 + 0.2 * surface
    height = np.where(in_joint, 0.0, height)
    return colour, height


def stucco(seed, color):
    fine = _fbm(seed, 3)
    blotch = _fbm(seed + 3, 60, 3)
    colour = _tint(color, 0.7 * blotch + 0.3 * fine, 0.09)
    return colour, 0.7 * fine + 0.3 * blotch


def concrete(seed, color):
    """Poured concrete with a formwork joint every metre and faint tie holes."""
    x, y = _grid()
    fine = _fbm(seed, 2.5)
    stain = _fbm(seed + 5, 90, 3)
    colour = _tint(color, 0.55 * stain + 0.45 * fine, 0.12)
    joint = (np.abs((y % 1.0) - 0.5) > 0.495)
    colour = np.where(joint[..., None], colour * 0.8, colour)
    ties = ((np.abs((x % 0.5) - 0.25) < 0.008) & (np.abs((y % 0.5) - 0.25) < 0.008))
    colour = np.where(ties[..., None], colour * 0.6, colour)
    height = 0.6 * fine + 0.4 * stain
    height = np.where(joint | ties, 0.0, height)
    return colour, height


def siding(seed, color):
    """Horizontal lap siding, 150 mm exposure: each board shades from its bottom lip up."""
    x, y = _grid()
    exposure = _fit(0.15)
    t = (y % exposure) / exposure  # 0 at top of board (image y down), 1 at its lip
    grain = _noise(seed, 1.5) * 0.4 + _fbm(seed + 1, 40, 2) * 0.6
    colour = _tint(color, 0.5 + 0.35 * (t - 0.5) + 0.3 * (grain - 0.5), 0.12)
    colour = np.where((t > 0.965)[..., None], colour * 0.55, colour)  # shadow line under each lap
    height = t * 0.9 + 0.1 * grain
    return colour, height


def corrugated(seed, color, period=0.076):
    x, y = _grid()
    period = _fit(period)
    wave = 0.5 + 0.5 * np.sin(2 * np.pi * x / period)
    streaks = _noise(seed, 30)[:, :1] * np.ones((1, SIZE))  # vertical rain streaks
    rust = _fbm(seed + 9, 25, 3)
    colour = _tint(color, 0.45 * wave + 0.35 * streaks + 0.2 * rust, 0.14)
    return colour, wave


def standing_seam(seed, color, pitch=0.5):
    x, y = _grid()
    pitch = _fit(pitch)
    d = np.abs((x % pitch) - pitch / 2)
    seam = np.clip(1 - (pitch / 2 - d) / 0.02, 0, 1)
    colour = _tint(color, 0.6 * _fbm(seed, 40, 3) + 0.4 * seam, 0.1)
    return colour, 0.2 + 0.8 * seam


def shingles(seed, color):
    """Three-tab asphalt shingles: 143 mm courses, 333 mm tabs, staggered."""
    x, y = _grid()
    course, tab = _fit(0.143), _fit(1.0 / 3)
    rows, cols = round(TILE_METRES / course), round(TILE_METRES / tab)
    row = np.floor(y / course).astype(int)
    xs = x + (row % 2) * tab / 2
    col = np.floor(xs / tab).astype(int)
    rng = np.random.default_rng(seed)
    per_tab = rng.random((rows, cols))
    granules = _noise(seed + 2, 0.8)
    colour = _tint(color, 0.45 * per_tab[row % rows, col % cols] + 0.55 * granules, 0.25)
    t = (y % course) / course
    slot = np.abs((xs % tab) - tab / 2) > tab / 2 - 0.006
    shadow = (t > 0.93) | slot
    colour = np.where(shadow[..., None], colour * 0.45, colour)
    height = np.where(shadow, 0.0, 0.3 + 0.6 * t + 0.1 * granules)
    return colour, height


def gravel_roof(seed, color):
    granules = _noise(seed, 0.9)
    patches = _fbm(seed + 4, 70, 3)
    colour = _tint(color, 0.6 * granules + 0.4 * patches, 0.35)
    return colour, granules


def wood(seed, color):
    """Painted/stained vertical-grain wood."""
    x, y = _grid()
    warp = _fbm(seed, 50, 3) * 0.08
    grain = 0.5 + 0.5 * np.sin(2 * np.pi * (x + warp) / _fit(0.012)) * _noise(seed + 1, 4)
    colour = _tint(color, 0.7 * grain + 0.3 * _fbm(seed + 2, 20, 2), 0.18)
    return colour, grain * 0.5


def canvas_stripes(seed, color, stripe=0.25):
    """Awning canvas: alternating colour/white stripes with weave."""
    x, y = _grid()
    weave = _noise(seed, 0.6)
    band = (np.floor(x / stripe) % 2 == 0)
    base = np.where(band[..., None], np.asarray(color)[None, None, :], np.asarray((0.9, 0.88, 0.82))[None, None, :])
    colour = np.clip(base * (0.92 + 0.16 * weave[..., None]), 0, 1)
    return colour, weave * 0.3


def asphalt(seed, color):
    """Worn asphalt: aggregate speckle, tyre-polished lanes, patch repairs, hairline cracks and oil stains."""
    x, y = _grid()
    aggregate = _noise(seed, 0.7)
    speck = (aggregate > 0.72).astype(float) * 0.5 + (aggregate < 0.22).astype(float) * -0.4
    wear = _fbm(seed + 1, 80, 3)
    patch = _fbm(seed + 2, 120, 2) > 0.66
    colour = _tint(color, 0.5 + 0.18 * speck + 0.3 * (wear - 0.5), 0.35)
    colour = np.where(patch[..., None], colour * 0.88, colour)  # darker resurfaced patches
    crack_field = np.abs(_fbm(seed + 3, 50, 3) - 0.5)
    crack = crack_field < 0.005
    colour = np.where(crack[..., None], colour * 0.45, colour)
    stain = _fbm(seed + 4, 35, 2)
    colour = colour * (1 - 0.12 * np.clip((stain - 0.75) / 0.1, 0, 1))[..., None]
    height = 0.6 * aggregate + 0.2 * wear
    height = np.where(crack, 0.0, height)
    return colour, height


def sidewalk(seed, color):
    """Broom-finished concrete slabs (1 m squares, tooled joints) with the odd stain and hairline crack."""
    x, y = _grid()
    slab = _fit(1.0)
    jx, jy = x % slab, y % slab
    joint = (np.minimum(jx, slab - jx) < 0.008) | (np.minimum(jy, slab - jy) < 0.008)
    edge = np.minimum(np.minimum(jx, slab - jx), np.minimum(jy, slab - jy))
    broom = _noise(seed, 0.6)[:, :1] * 0.5 + _noise(seed + 1, 0.9) * 0.5  # fine lines across the slab
    rng = np.random.default_rng(seed)
    per_slab = rng.random((round(TILE_METRES / slab), round(TILE_METRES / slab)))
    tone = per_slab[(y // slab).astype(int) % per_slab.shape[0], (x // slab).astype(int) % per_slab.shape[1]]
    stain = _fbm(seed + 2, 60, 3)
    colour = _tint(color, 0.35 * tone + 0.35 * stain + 0.3 * broom, 0.12)
    colour = np.where(joint[..., None], colour * 0.55, colour)
    height = np.clip(edge / 0.02, 0, 1) * 0.7 + 0.3 * broom
    height = np.where(joint, 0.0, height)
    return colour, height


def lawn(seed, color):
    """Mown lawn seen from above: dense blade strokes in several greens, thinner patches with soil showing."""
    x, y = _grid()
    blades = _noise(seed, 0.5)
    strokes = _noise(seed + 1, 1.2)
    clumps = _fbm(seed + 2, 40, 3)
    soil = _fbm(seed + 3, 90, 2)
    dry = _fbm(seed + 4, 150, 2)
    green = np.asarray(color)[None, None, :] * (0.7 + 0.6 * (0.6 * blades + 0.4 * strokes))[..., None]
    straw = np.asarray((0.42, 0.4, 0.22))[None, None, :] * (0.8 + 0.4 * blades)[..., None]
    colour = green * (1 - 0.35 * dry[..., None]) + straw * 0.35 * dry[..., None]
    colour = colour * (0.85 + 0.3 * clumps)[..., None]
    bare = np.clip((soil - 0.8) / 0.08, 0, 1) * (blades < 0.45) * 0.6
    colour = colour * (1 - bare[..., None]) + np.asarray((0.28, 0.22, 0.15))[None, None, :] * bare[..., None]
    return np.clip(colour, 0, 1), 0.6 * blades + 0.4 * clumps


def dirt(seed, color):
    """Packed dirt/gravel lot: pebbles, tyre ruts, a few weeds."""
    x, y = _grid()
    pebbles = _noise(seed, 0.9)
    lumps = _fbm(seed + 1, 30, 3)
    weeds = (_fbm(seed + 2, 50, 3) > 0.7) & (_noise(seed + 3, 0.6) > 0.55)
    colour = _tint(color, 0.5 * pebbles + 0.5 * lumps, 0.3)
    colour = np.where((pebbles > 0.78)[..., None], colour * 1.25, colour)
    colour = np.where(weeds[..., None], np.asarray((0.2, 0.3, 0.12))[None, None, :], colour)
    return np.clip(colour, 0, 1), 0.7 * pebbles + 0.3 * lumps


def water(seed, color):
    """Harbour water: colour depth variation; the normal map carries small wind ripples."""
    x, y = _grid()
    ripples = 0.0
    for k, (fx, fy) in enumerate(((3, 1), (2, 5), (7, 2), (5, 9), (11, 4))):
        phase = np.random.default_rng(seed + k).random() * 2 * np.pi
        ripples = ripples + np.sin(2 * np.pi * (fx * x + fy * y) / TILE_METRES + phase) / (1 + k * 0.6)
    ripples = ripples + 0.8 * (_fbm(seed, 12, 3) - 0.5)
    colour = _tint(color, _fbm(seed + 5, 120, 2), 0.15)
    return colour, (ripples - ripples.min()) / (ripples.max() - ripples.min())


# ---------------------------------------------------------------------- catalogue

# material name -> (generator, args, normal strength). Colours are linear-ish sRGB base colours.
SURFACES = {
    "M_Brick_Red": (brick, (0.52, 0.24, 0.17), 3.0),
    "M_Brick_Brown": (brick, (0.4, 0.27, 0.19), 3.0),
    "M_Stucco_Cream": (stucco, (0.84, 0.78, 0.64), 1.2),
    "M_Stucco_Teal": (stucco, (0.38, 0.62, 0.6), 1.2),
    "M_Stucco_White": (stucco, (0.86, 0.85, 0.82), 1.2),
    "M_Concrete_Grey": (concrete, (0.58, 0.58, 0.56), 1.5),
    "M_Metal_Corrugated": (corrugated, (0.62, 0.64, 0.66), 2.5),
    "M_Metal_Roof": (standing_seam, (0.5, 0.53, 0.52), 2.0),
    "M_Siding_White": (siding, (0.88, 0.88, 0.85), 2.5),
    "M_Siding_Yellow": (siding, (0.86, 0.76, 0.44), 2.5),
    "M_Siding_Blue": (siding, (0.46, 0.6, 0.72), 2.5),
    "M_Siding_Green": (siding, (0.5, 0.62, 0.5), 2.5),
    "M_Roof_Shingle": (shingles, (0.24, 0.22, 0.21), 2.5),
    "M_Roof_Shingle_Red": (shingles, (0.38, 0.18, 0.14), 2.5),
    "M_Roof_Tar": (gravel_roof, (0.2, 0.2, 0.19), 1.5),
    "M_Door_Wood": (wood, (0.3, 0.19, 0.11), 1.0),
    # Ground surfaces (roads, sidewalks, lawns, lots, water) share the same 2 m tiling so everything lines up.
    "M_Asphalt": (asphalt, (0.2, 0.2, 0.21), 1.5),
    "M_Sidewalk": (sidewalk, (0.6, 0.59, 0.56), 1.8),
    "M_Grass_Lawn": (lawn, (0.2, 0.34, 0.12), 1.2),
    "M_Dirt_Lot": (dirt, (0.4, 0.34, 0.26), 2.0),
    "M_Water_Harbour": (water, (0.07, 0.13, 0.15), 0.6),
}

# Ground materials are not used by any kit mesh but go into the Unity manifest all the same:
# name -> (colour, roughness, metallic). Colour is white where a texture supplies it.
GROUND_MATERIALS = {
    "M_Asphalt": ((1, 1, 1), 0.88, 0.0),
    "M_Sidewalk": ((1, 1, 1), 0.82, 0.0),
    "M_Grass_Lawn": ((1, 1, 1), 0.95, 0.0),
    "M_Dirt_Lot": ((1, 1, 1), 0.93, 0.0),
    "M_Water_Harbour": ((1, 1, 1), 0.04, 0.0),
    "M_Paint_White": ((0.86, 0.86, 0.82), 0.6, 0.0),
    "M_Paint_Yellow": ((0.86, 0.66, 0.12), 0.6, 0.0),
    "M_Curb": ((0.64, 0.63, 0.6), 0.8, 0.0),
}


def texture_names(material):
    """M_Brick_Red -> (T_Brick_Red_BC, T_Brick_Red_N)."""
    stem = "T_" + material[2:]
    return stem + "_BC", stem + "_N"


def normal_map(height, strength):
    """Tangent-space normal map (OpenGL / Unity convention: green = +V, which is up in the texture)."""
    gx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5
    gy_down = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * 0.5
    n = np.dstack((-gx * strength, gy_down * strength, np.ones_like(height)))
    n /= np.linalg.norm(n, axis=2, keepdims=True)
    return n * 0.5 + 0.5


def build(material, seed=None):
    fn, color, strength = SURFACES[material]
    seed = seed if seed is not None else sum(map(ord, material))
    colour, height = fn(seed, color)
    return np.clip(colour, 0, 1), normal_map(height, strength)


def write_png(path, rgb):
    """8-bit RGB PNG (no Pillow needed). ``rgb`` is HxWx3 in 0..1."""
    data = (np.clip(rgb, 0, 1) * 255 + 0.5).astype(np.uint8)
    h, w, _ = data.shape
    raw = b"".join(b"\x00" + data[r].tobytes() for r in range(h))

    def chunk(tag, payload):
        return struct.pack(">I", len(payload)) + tag + payload + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)

    with open(path, "wb") as fh:
        fh.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) +
                 chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def write_all(folder):
    """Writes every surface as T_<Name>_BC.png and T_<Name>_N.png; returns {material: (bc_path, n_path)}."""
    os.makedirs(folder, exist_ok=True)
    out = {}
    for material in SURFACES:
        colour, normal = build(material)
        bc_name, n_name = texture_names(material)
        bc, n = os.path.join(folder, bc_name + ".png"), os.path.join(folder, n_name + ".png")
        write_png(bc, colour)
        write_png(n, normal)
        out[material] = (bc, n)
    return out
