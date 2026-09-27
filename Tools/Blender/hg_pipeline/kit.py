"""Vertical-slice environment kit: generate → LOD → collision → validate → export, one asset per scene."""
import bpy

from . import export, lod_collision
from .generators import buildings, props

# name, category, factory. Names follow conventions (SM_ + PascalCase + variant).
CATALOG = [
    ("SM_Storefront_2F_A", "Buildings", lambda n: buildings.storefront_block(n, 16, 14, 2, "brick", (0.12, 0.35, 0.25), seed=11)),
    ("SM_Storefront_2F_B", "Buildings", lambda n: buildings.storefront_block(n, 12, 14, 2, "stucco_teal", (0.6, 0.18, 0.12), seed=12)),
    ("SM_Storefront_3F_C", "Buildings", lambda n: buildings.storefront_block(n, 20, 16, 3, "stucco", (0.1, 0.2, 0.45), seed=13)),
    ("SM_Office_8F_A", "Buildings", lambda n: buildings.office_tower(n, 26, 22, 8, seed=21)),
    ("SM_Warehouse_A", "Buildings", lambda n: buildings.warehouse(n, 60, 40, 10, seed=31)),
    ("SM_House_Shotgun_A", "Buildings", lambda n: buildings.shotgun_house(n, 5.2, 14, "siding_white", seed=41)),
    ("SM_House_Shotgun_B", "Buildings", lambda n: buildings.shotgun_house(n, 5.6, 15, "siding_yellow", seed=42)),
    ("SM_House_Shotgun_C", "Buildings", lambda n: buildings.shotgun_house(n, 5.0, 13, "siding_blue", seed=43)),
    ("SM_Apartment_4F_A", "Buildings", lambda n: buildings.apartment_block(n, 40, 26, 4, "brick_brown", seed=51)),
    ("SM_StreetLight_A", "StreetFurniture", lambda n: props.street_light(n)),
    ("SM_Bench_A", "StreetFurniture", lambda n: props.bench(n)),
    ("SM_FireHydrant_A", "StreetFurniture", lambda n: props.fire_hydrant(n)),
    ("SM_Bollard_A", "StreetFurniture", lambda n: props.bollard(n)),
    ("SM_BusShelter_A", "StreetFurniture", lambda n: props.bus_shelter(n)),
    ("SM_Dumpster_A", "Props", lambda n: props.dumpster(n)),
]

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


def build_kit(art_root, only=None, strict=True):
    """Exports the catalog into the Unity Art folder. Returns a list of per-asset reports."""
    reports = []
    for name, category, factory in CATALOG:
        if only and name not in only:
            continue
        objects = build_asset(name, category, factory)
        _, report = export.export_asset(objects, category, art_root, name=name, strict=strict)
        reports.append(report)
    return reports
