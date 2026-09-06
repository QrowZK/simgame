#!/usr/bin/env python3
"""Generates the modular machine kit as glTF, using Blender headless.

    /path/to/blender --background --python tools/generate_models.py

Writes game/models/{hull_0..7,attach_0..9,belt,pipe}.glb plus a shared panel
texture. MeshKit picks these up automatically and falls back to its procedural
placeholders for anything missing, so parts can land one at a time.

Everything is parametric rather than modelled by hand. ADR 0003 chose a kit --
a hull per tier plus a function attachment per category -- over bespoke meshes,
because ~130 machine variants sourced from asset packs cannot stay visually
coherent under a rotatable camera, and because MultiMesh batches per unique
mesh. Generating the kit from parameters keeps it coherent by construction and
lets the whole look be regenerated when the art direction changes.

GRID: one tile is 1.0 unit. Every part is centred on X/Z, sits on Y=0, and stays
inside a 1x1 footprint, so placing at integer tile coordinates lines up exactly.
"""

import math
import os
import sys

import bpy  # type: ignore
import bmesh  # type: ignore

TILE = 1.0
FOOTPRINT = 0.9          # leaves a visible seam between neighbouring machines
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "game", "models")

TIERS = 8
CATEGORIES = 10


# --------------------------------------------------------------------------- #
# scene helpers
# --------------------------------------------------------------------------- #

def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.objects):
        for item in list(block):
            if item.users == 0:
                block.remove(item)


def panel_texture(size=256):
    """A plate-and-rivet pattern, generated rather than sourced.

    Deterministic: same pixels every run, so regenerating the kit produces no
    spurious diff.
    """
    name = "panel"
    if name in bpy.data.images:
        return bpy.data.images[name]

    image = bpy.data.images.new(name, width=size, height=size)
    pixels = [0.0] * (size * size * 4)

    def hash01(x, y, salt=0):
        h = (x * 374761393 + y * 668265263 + salt * 2246822519) & 0xFFFFFFFF
        h = (h ^ (h >> 13)) * 1274126177 & 0xFFFFFFFF
        return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0

    plate = size // 4
    for y in range(size):
        for x in range(size):
            # base metal with fine grain
            v = 0.52 + 0.05 * hash01(x, y)

            # recessed seams between plates
            if x % plate < 2 or y % plate < 2:
                v *= 0.62

            # rivets just inside each plate corner
            cx, cy = x % plate, y % plate
            for rx, ry in ((5, 5), (plate - 6, 5), (5, plate - 6), (plate - 6, plate - 6)):
                if (cx - rx) ** 2 + (cy - ry) ** 2 <= 4:
                    v = min(1.0, v * 1.5)

            # broad streaking so large faces are not flat
            v *= 0.94 + 0.06 * hash01(x // 16, y // 8, 7)

            i = (y * size + x) * 4
            pixels[i:i + 4] = [v, v, v, 1.0]

    image.pixels = pixels
    image.pack()
    return image


def industrial_material():
    """One shared material. Base colour stays near-white so Godot's per-instance
    tier and status colours multiply through it rather than fighting it."""
    name = "industrial"
    if name in bpy.data.materials:
        return bpy.data.materials[name]

    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = nodes["Principled BSDF"]

    tex = nodes.new("ShaderNodeTexImage")
    tex.image = panel_texture()
    tex.location = (-400, 0)
    links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])

    bsdf.inputs["Metallic"].default_value = 0.35
    bsdf.inputs["Roughness"].default_value = 0.62
    return mat


def finish(obj):
    """UV unwrap, apply the shared material, shade flat, and drop the origin to
    the tile centre at floor level."""
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)

    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.02)
    bpy.ops.object.mode_set(mode="OBJECT")

    obj.data.materials.clear()
    obj.data.materials.append(industrial_material())
    bpy.ops.object.shade_flat()
    obj.select_set(False)


def box(name, sx, sy, sz, z=0.0, bevel=0.012):
    mesh = bpy.data.meshes.new(name)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)

    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=(sx, sz, sy), verts=bm.verts)          # Y up in Godot
    bmesh.ops.translate(bm, vec=(0, z + sy / 2, 0), verts=bm.verts)
    if bevel > 0:
        bmesh.ops.bevel(bm, geom=bm.verts[:] + bm.edges[:], offset=bevel,
                        segments=2, affect="EDGES", profile=0.5)
    bm.to_mesh(mesh)
    bm.free()
    return obj


def cylinder(name, radius, height, z=0.0, segments=16, cone=1.0):
    mesh = bpy.data.meshes.new(name)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)

    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segments,
                          radius1=radius, radius2=radius * cone, depth=height)
    bmesh.ops.rotate(bm, verts=bm.verts,
                     matrix=__import__("mathutils").Matrix.Rotation(math.radians(90), 3, "X"))
    bmesh.ops.translate(bm, vec=(0, z + height / 2, 0), verts=bm.verts)
    bm.to_mesh(mesh)
    bm.free()
    return obj


def join(name, objects):
    for o in objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    merged = bpy.context.view_layer.objects.active
    merged.name = name
    merged.data.name = name
    bpy.ops.object.select_all(action="DESELECT")
    return merged


def export(obj, filename):
    os.makedirs(OUT, exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.gltf(
        filepath=os.path.join(OUT, filename),
        export_format="GLB",
        use_selection=True,
        export_apply=True,
        export_yup=True,
    )
    obj.select_set(False)


# --------------------------------------------------------------------------- #
# the kit
# --------------------------------------------------------------------------- #

def hull_height(tier):
    """Body height for a tier. The renderer needs the matching deck height to
    seat attachments, so the formula lives in one place and is printed on
    generation for MeshKit to mirror."""
    return 0.42 + tier * 0.06


def build_hull(tier):
    """Hulls grow squatter and better-clad up the ladder, so tier reads at a
    glance from any camera angle."""
    height = hull_height(tier)
    width = FOOTPRINT

    parts = [box(f"body{tier}", width, height, width, bevel=0.015 + tier * 0.004)]

    # corner posts, chunky and exposed low down, slimmer and inset high up
    post = 0.085 - tier * 0.006
    inset = 0.5 * width - post * 0.5 - tier * 0.004
    for sx in (-1, 1):
        for sz in (-1, 1):
            # Flush with the body: standing proud read as spikes, not structure.
            p = box(f"post{tier}", post, height * (0.96 - tier * 0.02), post, bevel=0.004)
            p.location.x = sx * inset
            p.location.z = sz * inset
            parts.append(p)

    # a recessed top deck the attachment sits on
    parts.append(box(f"deck{tier}", width * 0.74, 0.05, width * 0.74,
                     z=height, bevel=0.01))

    # higher tiers gain a cooling fin bank down one side
    if tier >= 3:
        for i in range(3):
            fin = box(f"fin{tier}_{i}", 0.02, height * 0.6, width * 0.66,
                      z=height * 0.2, bevel=0.002)
            fin.location.x = -width * 0.5 - 0.012
            fin.location.z = (i - 1) * 0.2
            parts.append(fin)

    return join(f"hull_{tier}", parts)


def build_attachment(category):
    """One silhouette per machine function. Distinct in outline, because at
    playing zoom the shape is all you get."""
    if category == 0:      # smelting -- squat furnace with a stack
        parts = [box("f", 0.46, 0.3, 0.46), cylinder("s", 0.09, 0.34, z=0.3)]
    elif category == 1:    # crushing -- heavy drum
        parts = [cylinder("d", 0.26, 0.34, segments=20)]
    elif category == 2:    # separating -- inverted cone
        parts = [cylinder("c", 0.28, 0.4, segments=20, cone=0.34)]
    elif category == 3:    # chemistry -- reaction vessel
        parts = [cylinder("v", 0.2, 0.42, segments=20),
                 cylinder("cap", 0.23, 0.06, z=0.42, segments=20)]
    elif category == 4:    # fluid handling -- manifold with two risers
        parts = [box("m", 0.5, 0.14, 0.24),
                 cylinder("r1", 0.06, 0.34, z=0.14), cylinder("r2", 0.06, 0.34, z=0.14)]
        parts[1].location.x = -0.16
        parts[2].location.x = 0.16
    elif category == 5:    # shaping -- angled press
        parts = [box("bed", 0.5, 0.1, 0.4), box("ram", 0.22, 0.3, 0.22, z=0.1)]
    elif category == 6:    # assembly -- gantry arm
        parts = [box("rail", 0.62, 0.07, 0.14), box("head", 0.14, 0.2, 0.18, z=0.07)]
        parts[1].location.x = 0.14
    elif category == 7:    # power -- turbine cylinder lying down
        parts = [cylinder("t", 0.18, 0.46, segments=20)]
        parts[0].rotation_euler = (0, 0, math.radians(90))
    elif category == 8:    # extraction -- drill mast
        parts = [box("base", 0.34, 0.12, 0.34), cylinder("mast", 0.05, 0.44, z=0.12),
                 cylinder("bit", 0.11, 0.12, z=0.0, cone=0.15)]
    else:                  # exotic -- floating core in a cradle
        parts = [cylinder("cradle", 0.24, 0.1, segments=20),
                 cylinder("core", 0.15, 0.3, z=0.14, segments=20, cone=0.45)]

    return join(f"attach_{category}", parts)


def build_belt():
    parts = [box("bed", TILE, 0.06, 0.72, bevel=0.006)]
    for side in (-1, 1):
        rail = box("rail", TILE, 0.05, 0.07, z=0.06, bevel=0.004)
        rail.location.z = side * 0.325
        parts.append(rail)
    for i in range(4):     # rollers, so direction reads when the camera turns
        roller = cylinder("roller", 0.028, 0.66, z=0.062, segments=8)
        roller.rotation_euler = (0, 0, math.radians(90))
        roller.location.x = -0.375 + i * 0.25
        parts.append(roller)
    return join("belt", parts)


def build_pipe():
    parts = [cylinder("tube", 0.13, TILE, z=0.13, segments=16)]
    parts[0].rotation_euler = (0, 0, math.radians(90))
    for side in (-1, 1):
        flange = cylinder("flange", 0.16, 0.05, z=0.13, segments=16)
        flange.rotation_euler = (0, 0, math.radians(90))
        flange.location.x = side * 0.475
        parts.append(flange)
    return join("pipe", parts)


def main():
    clear_scene()
    industrial_material()

    made = []
    for tier in range(TIERS):
        obj = build_hull(tier)
        finish(obj)
        export(obj, f"hull_{tier}.glb")
        made.append(f"hull_{tier}")

    for category in range(CATEGORIES):
        obj = build_attachment(category)
        finish(obj)
        export(obj, f"attach_{category}.glb")
        made.append(f"attach_{category}")

    for builder, name in ((build_belt, "belt"), (build_pipe, "pipe")):
        obj = builder()
        finish(obj)
        export(obj, f"{name}.glb")
        made.append(name)

    print("deck heights (MeshKit.DeckHeight must match):")
    print("  " + ", ".join("%.2f" % (hull_height(t) + 0.05) for t in range(TIERS)))
    print("generated %d parts into %s" % (len(made), OUT))
    for name in made:
        print("  " + name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
