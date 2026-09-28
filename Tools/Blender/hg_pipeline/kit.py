"""Vertical-slice environment kit: generate → LOD → collision → validate → export, one asset per scene.

Besides the FBX files, ``build_kit`` writes what Unity needs to use them without hand work:
  * ``Environment/Textures/T_*_BC.png`` / ``T_*_N.png`` — tileable PBR textures (``textures.py``);
  * ``Environment/Materials/KitMaterials.json`` — every kit material's colour, roughness, metallic and texture
    names (``KitMaterials.cs`` turns these into Unity materials, ``ModelImportRules`` assigns them by name);
  * ``Environment/Buildings/KitBuildings.json`` — each building's footprint, height and which place kinds it can
    stand in for (``GreyboxWorldBuilder`` fits one to every lot).
"""
import json
import os

import bpy

from . import export, lod_collision, textures
from .generators import buildings, house_detail, props

# name, category, factory. Names follow conventions (SM_ + PascalCase + variant).
CATALOG = [
    ("SM_Storefront_1F_D", "Buildings", lambda n: buildings.storefront_block(n, 20, 18, 1, "stucco_white", (0.55, 0.12, 0.1), seed=14)),
    ("SM_Storefront_2F_A", "Buildings", lambda n: buildings.storefront_block(n, 16, 14, 2, "brick", (0.12, 0.35, 0.25), seed=11)),
    ("SM_Storefront_2F_B", "Buildings", lambda n: buildings.storefront_block(n, 12, 14, 2, "stucco_teal", (0.6, 0.18, 0.12), seed=12)),
    ("SM_Storefront_3F_C", "Buildings", lambda n: buildings.storefront_block(n, 20, 16, 3, "stucco", (0.1, 0.2, 0.45), seed=13)),
    ("SM_Office_4F_B", "Buildings", lambda n: buildings.office_tower(n, 26, 20, 4, seed=22)),
    ("SM_Office_8F_A", "Buildings", lambda n: buildings.office_tower(n, 26, 22, 8, seed=21)),
    ("SM_Office_16F_C", "Buildings", lambda n: buildings.office_tower(n, 30, 30, 16, seed=23)),
    ("SM_Warehouse_A", "Buildings", lambda n: buildings.warehouse(n, 60, 40, 10, seed=31)),
    ("SM_House_Shotgun_A", "Buildings", lambda n: house_detail.detailed_house(n, 14, 5.4, 1, "M_Board_White", (0.88, 0.88, 0.85), "M_Roof_Shingle", seed=41, entry="left", raised=True, chimney=False)),
    ("SM_House_Shotgun_B", "Buildings", lambda n: house_detail.detailed_house(n, 15, 5.8, 1, "M_Board_Yellow", (0.87, 0.76, 0.46), "M_Roof_Shingle_Red", seed=42, entry="left", raised=True)),
    ("SM_House_Shotgun_C", "Buildings", lambda n: house_detail.detailed_house(n, 13, 5.2, 1, "M_Board_Blue", (0.5, 0.63, 0.74), "M_Roof_Shingle", seed=43, entry="left", raised=True, chimney=False)),
    ("SM_House_1F_A", "Buildings", lambda n: house_detail.detailed_house(n, 12, 14, 1, "M_Board_White", (0.88, 0.88, 0.85), "M_Roof_Shingle", garage=True, seed=61)),
    ("SM_House_1F_B", "Buildings", lambda n: house_detail.detailed_house(n, 11, 13, 1, "M_Board_Grey", (0.6, 0.62, 0.62), "M_Roof_Shingle", seed=62)),
    ("SM_House_1F_C", "Buildings", lambda n: house_detail.detailed_house(n, 10, 13, 1, "M_Board_Green", (0.52, 0.63, 0.5), "M_Roof_Shingle_Red", seed=63)),
    ("SM_House_2F_A", "Buildings", lambda n: house_detail.detailed_house(n, 10, 12, 2, "M_Board_Yellow", (0.87, 0.76, 0.46), "M_Roof_Shingle", seed=64)),
    ("SM_House_2F_B", "Buildings", lambda n: house_detail.detailed_house(n, 12, 13, 2, "M_Board_Blue", (0.5, 0.63, 0.74), "M_Roof_Shingle", garage=True, seed=65)),
    ("SM_Apartment_4F_A", "Buildings", lambda n: buildings.apartment_block(n, 40, 26, 4, "brick_brown", seed=51)),
    ("SM_Apartment_8F_B", "Buildings", lambda n: buildings.apartment_block(n, 30, 30, 8, "brick", seed=52)),
    ("SM_GasStation_A", "Buildings", lambda n: buildings.gas_station(n, 34, 26, seed=71)),
    ("SM_Church_A", "Buildings", lambda n: buildings.church(n, 16, 28, seed=81)),
    ("SM_StreetLight_A", "StreetFurniture", lambda n: props.street_light(n)),
    ("SM_Bench_A", "StreetFurniture", lambda n: props.bench(n)),
    ("SM_FireHydrant_A", "StreetFurniture", lambda n: props.fire_hydrant(n)),
    ("SM_Bollard_A", "StreetFurniture", lambda n: props.bollard(n)),
    ("SM_BusShelter_A", "StreetFurniture", lambda n: props.bus_shelter(n)),
    ("SM_Dumpster_A", "Props", lambda n: props.dumpster(n)),
]

COMMERCIAL = ["Shop", "Restaurant", "Nightlife", "Gym", "TransitStop"]
CIVIC = ["Office", "Hospital", "PoliceStation", "FireStation", "Government", "School", "University"]
INDUSTRIAL = ["Warehouse", "Factory", "Garage", "Dock", "Stadium"]

# Where each building may stand: its main footprint (width along the street, depth; metres) and the PlaceKind names
# (Core/World/Geography.cs) it can represent. The Unity builder picks the closest fit per lot and scales to it.
PLACEMENT = {
    "SM_Storefront_1F_D": ((20, 18), COMMERCIAL + ["GasStation"]),
    "SM_Storefront_2F_A": ((16, 14), COMMERCIAL),
    "SM_Storefront_2F_B": ((12, 14), COMMERCIAL),
    "SM_Storefront_3F_C": ((20, 16), COMMERCIAL + ["Office"]),
    "SM_Office_4F_B": ((26, 20), CIVIC),
    "SM_Office_8F_A": ((26, 22), CIVIC),
    "SM_Office_16F_C": ((30, 30), CIVIC + ["ApartmentBuilding"]),
    "SM_Warehouse_A": ((60, 40), INDUSTRIAL),
    "SM_House_Shotgun_A": ((5.4, 14), ["Residence"]),
    "SM_House_Shotgun_B": ((5.8, 15), ["Residence"]),
    "SM_House_Shotgun_C": ((5.0, 13), ["Residence"]),
    "SM_House_1F_A": ((12, 14), ["Residence"]),
    "SM_House_1F_B": ((11, 13), ["Residence"]),
    "SM_House_1F_C": ((10, 13), ["Residence"]),
    "SM_House_2F_A": ((10, 12), ["Residence"]),
    "SM_House_2F_B": ((12, 13), ["Residence"]),
    "SM_Apartment_4F_A": ((40, 26), ["ApartmentBuilding"]),
    "SM_Apartment_8F_B": ((30, 30), ["ApartmentBuilding"]),
    "SM_GasStation_A": ((34, 26), ["GasStation"]),
    "SM_Church_A": ((16, 28), ["Church"]),
}

LOD_RATIOS = {
    "Buildings": (1.0, 0.45, 0.2, 0.08),
    "StreetFurniture": (1.0, 0.5, 0.2),
    "Props": (1.0, 0.5, 0.2),
}


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    units = bpy.context.scene.unit_settings
    units.system = "METRIC"
    units.scale_length = 1.0


def build_asset(name, category, factory):
    """Builds one asset in a clean scene and returns its objects (LODs + collision)."""
    reset_scene()
    obj = factory(name)
    lods = lod_collision.generate_lods(obj, LOD_RATIOS.get(category, (1.0, 0.5, 0.2)))
    hulls = lod_collision.generate_convex_collision(lods[0])
    return lods + hulls


def material_record(mat):
    """Colour/roughness/metallic of a generated Principled material, plus its kit textures if it has any."""
    bsdf = mat.node_tree.nodes.get("Principled BSDF") if mat.use_nodes else None
    colour = list(bsdf.inputs["Base Color"].default_value)[:3] if bsdf else list(mat.diffuse_color)[:3]
    record = {
        "Name": mat.name,
        "Color": [round(c, 4) for c in colour],
        "Roughness": round(bsdf.inputs["Roughness"].default_value if bsdf else 0.6, 3),
        "Metallic": round(bsdf.inputs["Metallic"].default_value if bsdf else 0.0, 3),
        "BaseMap": "",
        "NormalMap": "",
    }
    if mat.name in textures.SURFACES:
        record["BaseMap"], record["NormalMap"] = textures.texture_names(mat.name)
    return record


def build_kit(art_root, only=None, strict=True):
    """Exports the catalog into the Unity Art folder. Returns a list of per-asset reports."""
    reports = []
    materials = {}
    placements = []
    for name, category, factory in CATALOG:
        if only and name not in only:
            continue
        objects = build_asset(name, category, factory)
        for o in objects:
            if o.type == "MESH" and not o.name.startswith("UCX_"):
                for m in o.data.materials:
                    if m is not None:
                        materials[m.name] = material_record(m)
        if name in PLACEMENT:
            lod0 = next(o for o in objects if o.name.endswith("_LOD0"))
            height = max((lod0.matrix_world @ v.co).z for v in lod0.data.vertices)
            (width, depth), uses = PLACEMENT[name]
            placements.append({"Name": name, "Width": width, "Depth": depth, "Height": round(height, 2), "Uses": uses})
        _, report = export.export_asset(objects, category, art_root, name=name, strict=strict)
        reports.append(report)
    if not only:
        env = os.path.join(art_root, "Environment")
        textures.write_all(os.path.join(env, "Textures"))
        os.makedirs(os.path.join(env, "Materials"), exist_ok=True)
        for name, (colour, roughness, metallic) in textures.GROUND_MATERIALS.items():
            bc, n = textures.texture_names(name) if name in textures.SURFACES else ("", "")
            materials[name] = {"Name": name, "Color": list(colour), "Roughness": roughness, "Metallic": metallic, "BaseMap": bc, "NormalMap": n}
        with open(os.path.join(env, "Materials", "KitMaterials.json"), "w") as fh:
            json.dump({"Materials": [materials[k] for k in sorted(materials)]}, fh, indent=1)
        with open(os.path.join(env, "Buildings", "KitBuildings.json"), "w") as fh:
            json.dump({"Buildings": placements}, fh, indent=1)
    return reports
