"""Automatic LOD chains and convex collision hulls."""
import bmesh
import bpy

from . import conventions

DEFAULT_LOD_RATIOS = (1.0, 0.5, 0.22, 0.08)


def generate_lods(obj, ratios=DEFAULT_LOD_RATIOS):
    """Renames obj to <base>_LOD0 and creates decimated copies <base>_LOD1..n parented to the same parent.

    Returns the list of LOD objects (LOD0 first). Planar-heavy architecture decimates well with the
    'DISSOLVE' (planar) mode first, falling back to 'COLLAPSE' to hit the ratio.
    """
    base = conventions.base_name(obj.name)
    obj.name = base + "_LOD0"
    obj.data.name = obj.name
    lods = [obj]
    for level, ratio in enumerate(ratios[1:], start=1):
        copy = obj.copy()
        copy.data = obj.data.copy()
        copy.name = "%s_LOD%d" % (base, level)
        copy.data.name = copy.name
        for coll in obj.users_collection:
            coll.objects.link(copy)
        planar = copy.modifiers.new("Planar", "DECIMATE")
        planar.decimate_type = "DISSOLVE"
        planar.angle_limit = 0.035 * level
        collapse = copy.modifiers.new("Collapse", "DECIMATE")
        collapse.decimate_type = "COLLAPSE"
        collapse.ratio = ratio
        _apply_modifiers(copy)
        lods.append(copy)
    return lods


def generate_convex_collision(obj, pieces=None, collection=None):
    """Creates UCX_<base>_NN convex hulls. ``pieces`` is an optional list of vertex-index groups;
    by default one hull wraps the whole LOD0 mesh (good for boxy buildings and props)."""
    base = conventions.base_name(obj.name)
    groups = pieces or [None]
    hulls = []
    for i, group in enumerate(groups, start=1):
        bm = bmesh.new()
        src = obj.data
        coords = [src.vertices[v].co.copy() for v in (group if group else range(len(src.vertices)))]
        verts = [bm.verts.new(c) for c in coords]
        result = bmesh.ops.convex_hull(bm, input=verts)
        # Remove interior points that the hull did not use.
        unused = [v for v in result.get("geom_interior", []) if isinstance(v, bmesh.types.BMVert)]
        bmesh.ops.delete(bm, geom=unused, context="VERTS")
        mesh = bpy.data.meshes.new("UCX_%s_%02d" % (base, i))
        bm.to_mesh(mesh)
        bm.free()
        hull = bpy.data.objects.new(mesh.name, mesh)
        hull.matrix_world = obj.matrix_world.copy()
        (collection or obj.users_collection[0]).objects.link(hull)
        hull.display_type = "WIRE"
        hull.hide_render = True
        hulls.append(hull)
    return hulls


def _apply_modifiers(obj):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = bpy.data.meshes.new_from_object(evaluated)
    old = obj.data
    obj.modifiers.clear()
    obj.data = mesh
    if old.users == 0:
        bpy.data.meshes.remove(old)
    mesh.name = obj.name
