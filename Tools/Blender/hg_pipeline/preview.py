"""Renders the kit as a street scene (Cycles, CPU) with the generated textures applied, for docs and review.

    python Tools/Blender/cli.py preview --out docs/images/blender_kit_preview.png --textures <art>/Environment/Textures
"""
import math
import os

import bpy
from mathutils import Vector

from . import kit, textures

# (asset, x, y, rotation about Z in degrees). Streets run along X at y = 0; fronts face -Y (towards the camera).
LAYOUT = [
    ("SM_House_1F_A", -62, 12, 0), ("SM_House_2F_A", -49, 11, 0), ("SM_House_1F_C", -37, 11.5, 0), ("SM_House_2F_B", -24, 11.5, 0),
    ("SM_Storefront_2F_B", -9, 12, 0), ("SM_Storefront_2F_A", 5.5, 12, 0), ("SM_Storefront_3F_C", 24, 13, 0),
    ("SM_GasStation_A", 52, 18, 0), ("SM_Church_A", -80, 19, 0), ("SM_Office_8F_A", 88, 17, 0),
    ("SM_Apartment_4F_A", -20, 45, 0), ("SM_Office_16F_C", 32, 50, 0),
    ("SM_StreetLight_A", -30, 3.2, 0), ("SM_StreetLight_A", 0, 3.2, 0), ("SM_StreetLight_A", 30, 3.2, 0),
    ("SM_Bench_A", 12, 4.2, 0), ("SM_FireHydrant_A", -16, 3.8, 0), ("SM_BusShelter_A", 40, 4.5, 0),
]


def _textured(mat, folder):
    """Adds the kit's base colour and normal map to a generated material (preview only; Unity builds its own)."""
    if mat.name not in textures.SURFACES or mat.get("hg_textured"):
        return
    bc_name, n_name = textures.texture_names(mat.name)
    bc_path, n_path = os.path.join(folder, bc_name + ".png"), os.path.join(folder, n_name + ".png")
    if not (os.path.exists(bc_path) and os.path.exists(n_path)):
        return
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = nodes.get("Principled BSDF")
    bc = nodes.new("ShaderNodeTexImage")
    bc.image = bpy.data.images.load(bc_path, check_existing=True)
    links.new(bc.outputs["Color"], bsdf.inputs["Base Color"])
    nm = nodes.new("ShaderNodeTexImage")
    nm.image = bpy.data.images.load(n_path, check_existing=True)
    nm.image.colorspace_settings.name = "Non-Color"
    normal = nodes.new("ShaderNodeNormalMap")
    links.new(nm.outputs["Color"], normal.inputs["Color"])
    links.new(normal.outputs["Normal"], bsdf.inputs["Normal"])
    mat["hg_textured"] = True


def render(out_path, texture_folder, samples=48, resolution=(1600, 900), layout=None, camera=None):
    """Street scene (default) or any ``layout`` [(asset, x, y, rot)] seen from ``camera`` ((location), (target))."""
    kit.reset_scene()
    scene = bpy.context.scene
    factories = {name: factory for name, _, factory in kit.CATALOG}
    counts = {}
    for name, x, y, rot in (layout or LAYOUT):
        counts[name] = counts.get(name, 0) + 1
        obj = factories[name](name if counts[name] == 1 else "%s_%d" % (name, counts[name]))
        obj.location = (x, y, 0)
        obj.rotation_euler = (0, 0, math.radians(rot))
        for m in obj.data.materials:
            if m is not None:
                _textured(m, texture_folder)
    # Ground: road, sidewalks, lawn.
    def slab(name, size, loc, color, rough):
        bpy.ops.mesh.primitive_plane_add(size=1, location=loc)
        o = bpy.context.active_object
        o.name = name
        o.scale = (size[0], size[1], 1)
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True
        mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (*color, 1)
        mat.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = rough
        o.data.materials.append(mat)
    slab("Road", (400, 9), (0, -2.5, 0.0), (0.05, 0.05, 0.055), 0.9)
    slab("Sidewalk", (400, 4), (0, 4.0, 0.02), (0.35, 0.35, 0.33), 0.85)
    slab("Lawn", (400, 200), (0, 106, -0.01), (0.09, 0.16, 0.05), 0.95)
    slab("FarSide", (400, 60), (0, -37, -0.01), (0.25, 0.25, 0.24), 0.9)

    world = bpy.data.worlds.new("Sky")
    scene.world = world
    world.use_nodes = True
    sky = world.node_tree.nodes.new("ShaderNodeTexSky")
    sky.sky_type = "NISHITA"
    sky.sun_elevation = math.radians(38)
    sky.sun_rotation = math.radians(200)
    world.node_tree.links.new(sky.outputs["Color"], world.node_tree.nodes["Background"].inputs["Color"])
    sky.sun_disc = False  # the sun lamp below provides the direct light
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.12

    sun_data = bpy.data.lights.new("Sun", "SUN")
    sun_data.energy = 4.0
    sun_data.angle = math.radians(1.5)
    sun = bpy.data.objects.new("Sun", sun_data)
    sun.rotation_euler = (math.radians(52), 0, math.radians(200))
    scene.collection.objects.link(sun)

    cam_data = bpy.data.cameras.new("Camera")
    cam_data.lens = 30
    cam = bpy.data.objects.new("Camera", cam_data)
    location, target = camera or ((-8, -58, 22), (2, 20, 5))
    cam.location = location
    direction = Vector(target) - cam.location
    cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    scene.collection.objects.link(cam)
    scene.camera = cam

    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    scene.render.resolution_x, scene.render.resolution_y = resolution
    scene.view_settings.view_transform = "AgX" if "AgX" in [t.identifier for t in type(scene.view_settings).bl_rna.properties["view_transform"].enum_items] else "Filmic"
    scene.view_settings.exposure = -0.6
    scene.view_settings.look = "None"
    scene.render.filepath = os.path.abspath(out_path)
    os.makedirs(os.path.dirname(scene.render.filepath) or ".", exist_ok=True)
    bpy.ops.render.render(write_still=True)
    return scene.render.filepath
