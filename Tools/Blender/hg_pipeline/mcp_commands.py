"""Command surface for Blender MCP integration (TDD §13.3).

An MCP server running inside Blender (e.g. a Blender MCP add-on exposing an "execute code" tool, or
our own thin add-on) calls ``run(command, **kwargs)``. Every command takes and returns JSON-safe
values, never raw bpy objects, so an AI agent can drive the pipeline deterministically:

    run("list_catalog")
    run("build_asset", name="SM_Storefront_2F_A", art_root="/path/Game/Assets/_Project/Art")
    run("validate_selection", category="Props")
    run("generate_lods", object="SM_Crate_A")
    run("generate_collision", object="SM_Crate_A_LOD0")
    run("export_selection", category="Props", art_root="...")

Commands never delete user data outside the objects they create, and export refuses invalid assets.
"""
import json

import bpy

from . import conventions, export, kit, lod_collision, validate

_COMMANDS = {}


def command(fn):
    _COMMANDS[fn.__name__] = fn
    return fn


def run(name, **kwargs):
    if name not in _COMMANDS:
        return {"ok": False, "error": "unknown command %s" % name, "commands": sorted(_COMMANDS)}
    try:
        return {"ok": True, "result": _COMMANDS[name](**kwargs)}
    except Exception as exc:  # report, never crash the host Blender session
        return {"ok": False, "error": "%s: %s" % (type(exc).__name__, exc)}


def run_json(payload: str) -> str:
    request = json.loads(payload)
    return json.dumps(run(request.pop("command"), **request))


@command
def list_commands():
    return sorted(_COMMANDS)


@command
def list_catalog():
    return [{"name": n, "category": c} for n, c, _ in kit.CATALOG]


@command
def conventions_info():
    return {"prefixes": conventions.PREFIXES, "folders": conventions.UNITY_FOLDERS,
            "budgets": {k: vars(v) for k, v in conventions.BUDGETS.items()}}


@command
def build_asset(name, art_root, strict=True):
    for n, category, factory in kit.CATALOG:
        if n == name:
            objects = kit.build_asset(n, category, factory)
            path, report = export.export_asset(objects, category, art_root, name=n, strict=strict)
            return {"path": path, "report": report}
    raise KeyError("not in catalog: " + name)


@command
def validate_selection(category):
    return validate.validate_asset(list(bpy.context.selected_objects), category)


@command
def generate_lods(object, ratios=None):
    obj = bpy.data.objects[object]
    lods = lod_collision.generate_lods(obj, tuple(ratios) if ratios else lod_collision.DEFAULT_LOD_RATIOS)
    return [o.name for o in lods]


@command
def generate_collision(object):
    return [o.name for o in lod_collision.generate_convex_collision(bpy.data.objects[object])]


@command
def export_selection(category, art_root, name=None, strict=True):
    path, report = export.export_asset(list(bpy.context.selected_objects), category, art_root, name=name, strict=strict)
    return {"path": path, "report": report}
