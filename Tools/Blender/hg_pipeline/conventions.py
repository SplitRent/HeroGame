"""Asset naming and budget conventions shared by Blender tooling, CI and the Unity importer.

Mirrors docs/ASSET_PIPELINE.md and Game/Assets/_Project/Editor/ModelImportRules.cs.
Pure Python: no bpy import.
"""
import re
from dataclasses import dataclass

# Prefix -> meaning. Asset (file/object) names are PREFIX_PascalName[_Variant][_LODn].
PREFIXES = {
    "SM": "static mesh",
    "SK": "skeletal mesh",
    "M": "material",
    "T": "texture",
    "A": "animation",
    "UCX": "convex collision hull (Unreal/Unity convention)",
}

ASSET_NAME = re.compile(r"^(SM|SK)_[A-Z][A-Za-z0-9]*(_[A-Za-z0-9]+)*$")
MATERIAL_NAME = re.compile(r"^M_[A-Z][A-Za-z0-9]*(_[A-Za-z0-9]+)*$")
TEXTURE_NAME = re.compile(r"^T_[A-Z][A-Za-z0-9]*(_[A-Za-z0-9]+)*_(BC|N|M|ORM|E|H)$")
LOD_SUFFIX = re.compile(r"_LOD(\d+)$")
COLLISION_NAME = re.compile(r"^UCX_(.+?)(_\d+)?$")


@dataclass(frozen=True)
class Budget:
    """Triangle budget for LOD0 and minimum number of LOD levels (including LOD0)."""
    max_tris_lod0: int
    min_lods: int
    needs_collision: bool


# Budgets per category (derived from the asset's Unity folder). Tuned for PC/console open world.
BUDGETS = {
    "Buildings": Budget(60000, 4, True),
    "Props": Budget(6000, 3, True),
    "StreetFurniture": Budget(3000, 3, True),
    "Vehicles": Budget(90000, 4, True),
    "Characters": Budget(70000, 3, False),
    "Vegetation": Budget(15000, 4, False),
    "Interiors": Budget(20000, 2, True),
}

# Category -> Unity destination (under Game/Assets/_Project/Art).
UNITY_FOLDERS = {
    "Buildings": "Environment/Buildings",
    "Props": "Environment/Props",
    "StreetFurniture": "Environment/StreetFurniture",
    "Vehicles": "Vehicles",
    "Characters": "Characters",
    "Vegetation": "Environment/Vegetation",
    "Interiors": "Environment/Interiors",
}


def base_name(name: str) -> str:
    """Strips LOD suffix: SM_Shop_A_LOD2 -> SM_Shop_A."""
    return LOD_SUFFIX.sub("", name)


def lod_level(name: str):
    m = LOD_SUFFIX.search(name)
    return int(m.group(1)) if m else None


def collision_target(name: str):
    """UCX_SM_Shop_A_01 -> SM_Shop_A (the render mesh it belongs to), else None."""
    m = COLLISION_NAME.match(name)
    return m.group(1) if m else None


def validate_asset_name(name: str):
    """Returns a list of problems (empty when the name is valid)."""
    problems = []
    if collision_target(name) is not None:
        if not ASSET_NAME.match(collision_target(name)):
            problems.append("collision %s does not reference a valid mesh name" % name)
        return problems
    if not ASSET_NAME.match(base_name(name)):
        problems.append("%s: expected SM_/SK_ + PascalCase (e.g. SM_Shopfront_A)" % name)
    level = lod_level(name)
    if level is not None and level > 7:
        problems.append("%s: LOD index above 7" % name)
    return problems


def validate_material_name(name: str):
    return [] if MATERIAL_NAME.match(name) else ["material %s: expected M_PascalCase" % name]


def validate_texture_name(name: str):
    return [] if TEXTURE_NAME.match(name) else ["texture %s: expected T_Name_(BC|N|M|ORM|E|H)" % name]
