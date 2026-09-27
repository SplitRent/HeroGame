"""Pre-export validation: every production asset must pass before it reaches Unity (GDD §156)."""
import math

import bpy

from . import conventions
from .geometry import triangle_count, world_bounds


def validate_asset(root_objects, category: str):
    """Validates one asset (its LOD meshes and collision hulls).

    Returns a list of dicts: {"severity": "error"|"warning", "object": str, "message": str}.
    """
    issues = []

    def add(sev, obj, msg):
        issues.append({"severity": sev, "object": obj, "message": msg})

    budget = conventions.BUDGETS.get(category)
    if budget is None:
        add("error", "-", "unknown category %s (expected one of %s)" % (category, ", ".join(conventions.BUDGETS)))
        return issues

    meshes = [o for o in root_objects if o.type == "MESH"]
    render = [o for o in meshes if conventions.collision_target(o.name) is None]
    hulls = [o for o in meshes if conventions.collision_target(o.name) is not None]
    if not render:
        add("error", "-", "no render meshes")
        return issues

    lod_levels = {}
    for o in render:
        for p in conventions.validate_asset_name(o.name):
            add("error", o.name, p)
        level = conventions.lod_level(o.name)
        lod_levels[level if level is not None else 0] = o

        # Transforms must be applied: Unity receives metre-scale, unrotated meshes.
        if any(abs(s - 1.0) > 1e-4 for s in o.scale):
            add("error", o.name, "unapplied scale %s" % (tuple(round(s, 4) for s in o.scale),))
        if any(abs(r) > 1e-4 for r in o.rotation_euler):
            add("error", o.name, "unapplied rotation")
        if not o.data.uv_layers:
            add("error", o.name, "no UV map")
        if not o.data.materials or any(m is None for m in o.data.materials):
            add("error", o.name, "missing material slot assignment")
        for m in o.data.materials:
            if m is not None:
                for p in conventions.validate_material_name(m.name):
                    add("error", o.name, p)

        lo, hi = world_bounds(o)
        size = hi - lo
        if max(size) > 500 or max(size) < 0.01:
            add("error", o.name, "implausible size %.3f m — check units (1 BU = 1 m)" % max(size))
        if abs(lo.z) > 0.05 and category in ("Buildings", "Props", "StreetFurniture", "Vehicles", "Interiors"):
            add("warning", o.name, "origin not at ground level (min z = %.3f)" % lo.z)
        ngons = sum(1 for p in o.data.polygons if len(p.vertices) > 4)
        if ngons:
            add("warning", o.name, "%d n-gons (triangulated on export)" % ngons)

    lod0 = lod_levels.get(0)
    if lod0 is not None and triangle_count(lod0) > budget.max_tris_lod0:
        add("error", lod0.name, "LOD0 has %d tris, budget %d" % (triangle_count(lod0), budget.max_tris_lod0))
    if len(lod_levels) < budget.min_lods:
        add("error", "-", "has %d LOD level(s), category %s requires %d" % (len(lod_levels), category, budget.min_lods))
    levels = sorted(lod_levels)
    if levels != list(range(len(levels))):
        add("error", "-", "LOD levels are not contiguous: %s" % levels)
    for a, b in zip(levels, levels[1:]):
        ta, tb = triangle_count(lod_levels[a]), triangle_count(lod_levels[b])
        if tb > ta * 0.9 and ta > 64:
            add("warning", lod_levels[b].name, "LOD%d barely reduces LOD%d (%d → %d tris)" % (b, a, ta, tb))

    if budget.needs_collision and not hulls:
        add("error", "-", "category %s requires UCX_ collision hulls" % category)
    for h in hulls:
        if len(h.data.vertices) > 255:
            add("error", h.name, "convex hull exceeds 255 vertices (Unity/PhysX limit)")
    return issues


def has_errors(issues) -> bool:
    return any(i["severity"] == "error" for i in issues)


def scene_units_ok() -> bool:
    us = bpy.context.scene.unit_settings
    return us.system == "METRIC" and math.isclose(us.scale_length, 1.0)
