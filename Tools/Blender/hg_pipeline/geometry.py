"""Mesh construction helpers built on bmesh (Blender's mesh editing API)."""
import bmesh
import bpy
from mathutils import Matrix, Vector


def new_object(name: str, bm: "bmesh.types.BMesh", collection=None):
    # N-gons (cylinder caps) cannot carry tangents in FBX; triangulate them, keep quads.
    ngons = [f for f in bm.faces if len(f.verts) > 4]
    if ngons:
        bmesh.ops.triangulate(bm, faces=ngons, quad_method="BEAUTY", ngon_method="BEAUTY")
    mesh = bpy.data.meshes.new(name)
    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(name, mesh)
    (collection or bpy.context.scene.collection).objects.link(obj)
    return obj


def material(name: str, color, roughness=0.6, metallic=0.0):
    """Creates or reuses a Principled BSDF material (maps to HDRP/Lit on import)."""
    mat = bpy.data.materials.get(name)
    if mat is None:
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True
        bsdf = mat.node_tree.nodes.get("Principled BSDF")
        bsdf.inputs["Base Color"].default_value = (*color, 1.0)
        bsdf.inputs["Roughness"].default_value = roughness
        bsdf.inputs["Metallic"].default_value = metallic
        mat.diffuse_color = (*color, 1.0)
    return mat


def material_index(obj, mat) -> int:
    for i, slot in enumerate(obj.data.materials):
        if slot == mat:
            return i
    obj.data.materials.append(mat)
    return len(obj.data.materials) - 1


def add_box(bm, center, size, material_slot=0):
    """Adds an axis-aligned box to a bmesh; returns its faces."""
    geom = bmesh.ops.create_cube(bm, size=1.0)
    verts = geom["verts"]
    bmesh.ops.scale(bm, vec=Vector(size), verts=verts)
    bmesh.ops.translate(bm, vec=Vector(center), verts=verts)
    faces = list({f for v in verts for f in v.link_faces})
    for f in faces:
        f.material_index = material_slot
    return faces


def box_uv_project(obj, scale=1.0):
    """World-space box projection UVs (1 UV unit = 1 m / scale): tileable materials line up across modules."""
    mesh = obj.data
    if not mesh.uv_layers:
        mesh.uv_layers.new(name="UVMap")
    uv = mesh.uv_layers.active.data
    for poly in mesh.polygons:
        n = poly.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        for li in poly.loop_indices:
            co = mesh.vertices[mesh.loops[li].vertex_index].co
            if ax == 0:
                u, v = co.y, co.z
            elif ax == 1:
                u, v = co.x, co.z
            else:
                u, v = co.x, co.y
            uv[li].uv = (u / scale, v / scale)
    # Lightmap UVs for baked/APV lighting.
    if len(mesh.uv_layers) < 2:
        mesh.uv_layers.new(name="Lightmap")


def triangle_count(obj) -> int:
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def world_bounds(obj):
    pts = [obj.matrix_world @ Vector(c) for c in obj.bound_box]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return lo, hi


def apply_transforms(obj):
    """Bakes location-independent transforms (rotation/scale) into the mesh."""
    mat = Matrix.LocRotScale(None, obj.rotation_euler.to_quaternion(), obj.scale)
    obj.data.transform(mat)
    obj.rotation_euler = (0.0, 0.0, 0.0)
    obj.scale = (1.0, 1.0, 1.0)
