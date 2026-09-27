"""FBX export with the Blender → Unity axis/scale contract."""
import json
import os

import bpy

from . import conventions, validate


def export_fbx(objects, filepath: str):
    """Exports exactly ``objects`` to ``filepath`` for Unity.

    Contract (matches ModelImportRules.cs): metres, Y-up, -Z forward, axis conversion baked into the
    mesh (so Unity objects have identity rotation), no leaf bones, modifiers applied, no embedded media.
    """
    os.makedirs(os.path.dirname(filepath) or ".", exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    for o in objects:
        o.hide_set(False)
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(
        filepath=filepath,
        use_selection=True,
        object_types={"MESH", "ARMATURE", "EMPTY"},
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z",
        axis_up="Y",
        bake_space_transform=True,
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        use_tspace=True,
        add_leaf_bones=False,
        path_mode="STRIP",
        embed_textures=False,
        use_custom_props=True,
    )
    return filepath


def export_asset(objects, category: str, art_root: str, name: str = None, strict: bool = True):
    """Validates then exports an asset into <art_root>/<UnityFolder>/<name>.fbx with a JSON sidecar
    report (validation issues, triangle counts). Raises if validation fails and strict is set."""
    issues = validate.validate_asset(objects, category)
    if strict and validate.has_errors(issues):
        raise ValueError("Validation failed:\n" + "\n".join("%(severity)s %(object)s: %(message)s" % i for i in issues))
    render = [o for o in objects if conventions.collision_target(o.name) is None]
    asset = name or conventions.base_name(render[0].name)
    folder = os.path.join(art_root, conventions.UNITY_FOLDERS[category])
    path = export_fbx(objects, os.path.join(folder, asset + ".fbx"))
    from .geometry import triangle_count
    report = {
        "asset": asset,
        "category": category,
        "fbx": os.path.basename(path),
        "lods": {o.name: triangle_count(o) for o in render},
        "collision_hulls": [o.name for o in objects if conventions.collision_target(o.name) is not None],
        "materials": sorted({m.name for o in render for m in o.data.materials if m}),
        "issues": issues,
        "tool": "hg_pipeline",
    }
    with open(os.path.join(folder, asset + ".pipeline.json"), "w") as fh:
        json.dump(report, fh, indent=2)
    return path, report
