"""Convert a Garry's Mod addon (.gma) into Blender files ready for a VRChat avatar.

Usage:
    blender -b --factory-startup -P gma2blend.py -- ADDON OUT_DIR [options]

ADDON is a .gma file or an already extracted addon folder.
Requires the SourceIO add-on (https://github.com/REDxEYE/SourceIO).
"""
import argparse
import json
import math
import os
import re
import struct
import sys

HUMANOID_NAMES = {
    "Pelvis": "Hips", "Spine": "Spine", "Spine1": "Spine1", "Spine2": "Chest",
    "Spine4": "UpperChest", "Neck1": "Neck", "Head1": "Head",
}
for _s, _side in (("L", "Left"), ("R", "Right")):
    HUMANOID_NAMES.update({
        f"{_s}_Thigh": f"{_side}UpperLeg", f"{_s}_Calf": f"{_side}LowerLeg",
        f"{_s}_Foot": f"{_side}Foot", f"{_s}_Toe0": f"{_side}Toes",
        f"{_s}_Clavicle": f"{_side}Shoulder", f"{_s}_UpperArm": f"{_side}UpperArm",
        f"{_s}_Forearm": f"{_side}LowerArm", f"{_s}_Hand": f"{_side}Hand",
    })
    for _f, _finger in enumerate(("Thumb", "Index", "Middle", "Ring", "Little")):
        for _j, _joint in enumerate(("Proximal", "Intermediate", "Distal")):
            HUMANOID_NAMES[f"{_s}_Finger{_f}{_j or ''}"] = f"{_side}{_finger}{_joint}"

# SFM / TF2-style rigs name the same bones bip_*.
SFM_NAMES = {"pelvis": "Hips", "spine_0": "Spine", "spine_1": "Chest", "spine_2": "Chest",
             "spine_3": "UpperChest", "neck": "Neck", "neck_0": "Neck", "head": "Head"}
for _s, _side in (("L", "Left"), ("R", "Right")):
    SFM_NAMES.update({f"hip_{_s}": f"{_side}UpperLeg", f"knee_{_s}": f"{_side}LowerLeg",
                      f"foot_{_s}": f"{_side}Foot", f"toe_{_s}": f"{_side}Toes",
                      f"collar_{_s}": f"{_side}Shoulder", f"upperArm_{_s}": f"{_side}UpperArm",
                      f"lowerArm_{_s}": f"{_side}LowerArm", f"hand_{_s}": f"{_side}Hand"})
    for _finger, _human in (("thumb", "Thumb"), ("index", "Index"), ("middle", "Middle"),
                            ("ring", "Ring"), ("pinky", "Little")):
        for _j, _joint in enumerate(("Proximal", "Intermediate", "Distal")):
            SFM_NAMES[f"{_finger}_{_j}_{_s}"] = f"{_side}{_human}{_joint}"

VIEWMODEL = re.compile(r"(^|[_/\\])(c_arms|v_|arms|hands)([_.]|$)", re.I)
HELPER_BONE = re.compile(r"twist|_root|elbow|knee|wrist|ulna|bicep", re.I)
ENGINE_TEXTURES = {"normal", "phong_exp", "lightwarptexture"}



def _read_cstring(f):
    out = bytearray()
    while (c := f.read(1)) not in (b"", b"\0"):
        out += c
    return out.decode("utf-8", "replace")


def extract_gma(path, out_dir):
    """Extract a GMAD archive. Returns the addon name, author and file list."""
    with open(path, "rb") as f:
        if f.read(4) != b"GMAD":
            raise ValueError(f"{path} is not a .gma archive")
        version = f.read(1)[0]
        f.seek(16, os.SEEK_CUR)  # steam id + timestamp
        if version > 1:
            while _read_cstring(f):  # required content, terminated by ""
                pass
        info = {"name": _read_cstring(f), "description": _read_cstring(f), "author": _read_cstring(f)}
        f.seek(4, os.SEEK_CUR)  # addon version

        entries = []
        while struct.unpack("<I", f.read(4))[0]:
            name = _read_cstring(f)
            size, _crc = struct.unpack("<qI", f.read(12))
            entries.append((name, size))

        root = os.path.realpath(out_dir)
        for name, size in entries:
            dest = os.path.realpath(os.path.join(root, name))
            if os.path.commonpath([root, dest]) != root:
                raise ValueError(f"archive entry escapes the output folder: {name}")
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with open(dest, "wb") as out:
                out.write(f.read(size))

    info["files"] = [name for name, _ in entries]
    return info


def read_vmt_materials(addon_dir):
    """Base texture and render flags of every .vmt in the addon."""
    materials = []
    key = re.compile(r'"?\$(\w+)"?\s+"?([^"\r\n]+)"?')
    for folder, _, files in os.walk(os.path.join(addon_dir, "materials")):
        for name in files:
            if not name.lower().endswith(".vmt"):
                continue
            with open(os.path.join(folder, name), encoding="utf-8", errors="replace") as f:
                text = re.sub(r"//.*", "", f.read())
            values = {k.lower(): v.strip() for k, v in key.findall(text)}
            base = values.get("basetexture", "")
            materials.append({
                "name": name[:-4].lower(),
                "texture": os.path.basename(base.replace("\\", "/")),
                "translucent": values.get("translucent") == "1",
                "alphatest": values.get("alphatest") == "1",
                "double_sided": values.get("nocull") == "1",
            })
    return materials



def enable_sourceio():
    import addon_utils
    import bpy

    if "mdl" in dir(getattr(bpy.ops, "sourceio", None)):
        return
    for module in addon_utils.modules():
        if "sourceio" in module.__name__.lower():
            addon_utils.enable(module.__name__, default_set=True)
            return
    raise RuntimeError("SourceIO is not installed: https://github.com/REDxEYE/SourceIO")


def call_operator(op, **kwargs):
    """SourceIO operators need a 3D view in the context when Blender has a UI."""
    import bpy

    windows = bpy.context.window_manager.windows
    if windows:
        area = next((a for a in windows[0].screen.areas if a.type == "VIEW_3D"), None)
        if area:
            with bpy.context.temp_override(window=windows[0], area=area):
                return op(**kwargs)
    return op(**kwargs)


def import_mdl(path):
    import bpy

    folder = os.path.dirname(path) + os.sep
    call_operator(bpy.ops.sourceio.mdl, filepath=path, files=[{"name": os.path.basename(path)}], directory=folder)


def repair_shape_key_names():
    import bpy

    for mesh in bpy.data.meshes:
        for key in mesh.shape_keys.key_blocks if mesh.shape_keys else ():
            try:
                fixed = key.name.encode("cp1252").decode("utf-8")
            except UnicodeError:
                continue
            key.name = fixed


def remove_outline_hulls():
    """Delete inverted-hull outline faces; a toon shader draws outlines instead."""
    import bpy

    removed = 0
    for obj in [o for o in bpy.data.objects if o.type == "MESH"]:
        slots = [i for i, m in enumerate(obj.data.materials) if m and "outline" in m.name.lower()]
        if not slots:
            continue
        for poly in obj.data.polygons:
            poly.select = poly.material_index in slots
        removed += sum(p.select for p in obj.data.polygons)
        bpy.ops.object.select_all(action="DESELECT")
        bpy.context.view_layer.objects.active = obj
        obj.select_set(True)
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_mode(type="FACE")
        bpy.ops.mesh.delete(type="FACE")
        bpy.ops.object.mode_set(mode="OBJECT")
        for i in reversed(slots):
            obj.data.materials.pop(index=i)
        if not obj.data.polygons:
            bpy.data.objects.remove(obj)
    return removed


def export_textures(addon_dir, out_dir):
    """Save every texture as PNG, converting the VTFs SourceIO skipped (unsupported shaders)."""
    import bpy

    os.makedirs(out_dir, exist_ok=True)
    saved, notes = set(), []

    def save(image, stem):
        image.filepath_raw = os.path.join(out_dir, stem + ".png")
        image.file_format = "PNG"
        image.save()
        saved.add(stem.lower())

    for image in bpy.data.images:
        stem = os.path.basename(image.name).removesuffix(".vtf")
        # Reading size loads the pixel buffer; has_data stays False until then.
        if image.size[0] > 1 and not image.name.startswith("SIOe_") and stem.lower() not in ENGINE_TEXTURES:
            save(image, stem)

    for folder, _, files in os.walk(os.path.join(addon_dir, "materials")):
        if re.search(r"[/\\](vgui|shared)([/\\]|$)", folder, re.I):
            continue
        for name in files:
            stem = name[:-4]
            if not name.lower().endswith(".vtf") or stem.lower() in saved:
                continue
            before = set(bpy.data.images.keys())
            call_operator(bpy.ops.sourceio.vtf, filepath=os.path.join(folder, name),
                          files=[{"name": name}], directory=folder + os.sep)
            for new in set(bpy.data.images.keys()) - before:
                save(bpy.data.images[new], stem)
                notes.append(f"converted {stem}.png, which SourceIO did not load")
    return sorted(saved), notes


def fix_skeleton(armature):
    """Rename ValveBiped/SFM bones to humanoid names and point hips and limbs along the body."""
    import bpy
    from mathutils import Vector

    taken = {b.name for b in armature.data.bones}
    for bone in armature.data.bones:
        if bone.name.startswith("ValveBiped.Bip01_"):
            new = HUMANOID_NAMES.get(bone.name[len("ValveBiped.Bip01_"):])
        elif bone.name.startswith("bip_"):
            new = SFM_NAMES.get(bone.name[len("bip_"):])
        else:
            new = None
        if new and new not in taken:
            taken.add(new)
            bone.name = new

    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = armature
    armature.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bones = armature.data.edit_bones

    def aim(name, target, roll_axis):
        if name in bones and target in bones and (bones[target].head - bones[name].head).length > 1e-4:
            bones[name].tail = bones[target].head.copy()
            bones[name].align_roll(roll_axis)

    # Source bone tails are arbitrary (thighs often point backward). VRChat compares the thigh
    # rotation with the hips', so aim each limb at its child and give it a consistent roll.
    for side in ("Left", "Right"):
        aim(f"{side}UpperLeg", f"{side}LowerLeg", Vector((0, -1, 0)))
        aim(f"{side}LowerLeg", f"{side}Foot", Vector((0, -1, 0)))
        aim(f"{side}UpperArm", f"{side}LowerArm", Vector((0, 0, 1)))
        aim(f"{side}LowerArm", f"{side}Hand", Vector((0, 0, 1)))

    if {"Hips", "LeftUpperLeg", "RightUpperLeg"} <= set(bones.keys()):
        hips = bones["Hips"]
        length = max(hips.length, 0.05)
        hips.head = (bones["LeftUpperLeg"].head + bones["RightUpperLeg"].head) / 2
        hips.tail = hips.head + Vector((0, 0, length))
        hips.align_roll(Vector((0, -1, 0)))
    bpy.ops.object.mode_set(mode="OBJECT")

    hips = armature.data.bones.get("Hips")
    if not hips:
        return None
    hips_up = hips.matrix_local.to_3x3().inverted() @ Vector((0, 0, 1))
    return {
        side: round(math.degrees((armature.data.bones[f"{side}UpperLeg"].matrix_local.to_3x3() @ hips_up)
                                 .angle(Vector((0, 0, 1)))), 1)
        for side in ("Left", "Right") if f"{side}UpperLeg" in armature.data.bones
    }


def describe_model(armature):
    import bpy

    meshes = [o for o in bpy.data.objects if o.type == "MESH"]
    described = []
    for obj in meshes:
        tris = [0] * max(len(obj.data.materials), 1)
        for poly in obj.data.polygons:
            tris[poly.material_index] += len(poly.vertices) - 2
        described.append({
            "name": obj.name,
            "materials": [m.name if m else None for m in obj.data.materials],
            "material_tris": tris,
            "shape_keys": [k.name for k in obj.data.shape_keys.key_blocks[1:]] if obj.data.shape_keys else [],
        })
    heights = [(armature.matrix_world @ v.co).z for o in meshes for v in o.data.vertices]
    return {
        "meshes": described,
        "total_tris": sum(sum(m["material_tris"]) for m in described),
        "height": round(max(heights) - min(heights), 3) if heights else None,
        "helper_bones": [b.name for b in armature.data.bones if HELPER_BONE.search(b.name)],
        "eye_bones": [b.name for b in armature.data.bones if "eye" in b.name.lower()],
    }


def export_fbx(path):
    import bpy

    bpy.ops.object.select_all(action="DESELECT")
    for obj in bpy.data.objects:
        obj.select_set(obj.type in {"MESH", "ARMATURE"})
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={"ARMATURE", "MESH"},
        add_leaf_bones=False, apply_scale_options="FBX_SCALE_ALL", bake_anim=False,
        use_armature_deform_only=False, mesh_smooth_type="FACE", path_mode="STRIP",
    )


def convert_model(mdl, addon_dir, out_dir, args):
    import bpy

    bpy.ops.wm.read_homefile(use_empty=True)
    enable_sourceio()
    import_mdl(mdl)
    repair_shape_key_names()

    stem = os.path.splitext(os.path.basename(mdl))[0]
    report = {"model": stem, "notes": []}
    if not args.keep_outline:
        report["outline_faces_removed"] = remove_outline_hulls()
    report["textures"], notes = export_textures(addon_dir, os.path.join(out_dir, "Textures"))
    report["notes"] += notes

    armature = next((o for o in bpy.data.objects if o.type == "ARMATURE"), None)
    if armature:
        if not args.no_legfix:
            report["pelvis_thigh_angle"] = fix_skeleton(armature)
        report.update(describe_model(armature))
    else:
        report["notes"].append("no armature: static prop")

    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out_dir, stem + ".blend"))
    if not args.no_fbx:
        export_fbx(os.path.join(out_dir, stem + ".fbx"))
    return report


def write_checklist(path, info, models):
    lines = [f"# {info.get('name') or 'Addon'}", ""]
    if info.get("author"):
        lines += [f"Author: {info['author']}", ""]
    for m in models:
        lines += [f"## {m['model']}", "",
                  f"- Height {m.get('height')} m, {m.get('total_tris')} triangles",
                  f"- Pelvis/thigh angle {m.get('pelvis_thigh_angle')} (VRChat warns below 175)", ""]
        multi = [x["name"] for x in m.get("meshes", []) if len(x["materials"]) > 1]
        lines += ["Run **Tools > Source Avatar Setup** in Unity with this folder's `report.json`. It handles:", "",
                  "- Humanoid import, removing a Jaw mapped to a hair bone",
                  "- Materials, matched per submesh by triangle count",
                  f"- Eye look rotations for {', '.join(m.get('eye_bones', [])) or 'no eye bones'}",
                  f"- Constraints for elbow, knee and forearm-twist helpers (found: {', '.join(m.get('helper_bones', [])) or 'none'})",
                  "- Hand and foot bone order, PhysBone colliders under PhysBone roots", ""]
        if multi:
            lines += ["Meshes with several materials (Unity may reorder their submeshes):",
                      *[f"- `{n}`" for n in multi], ""]
        if m["notes"]:
            lines += ["Notes:", *[f"- {n}" for n in m["notes"]], ""]
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))


def main(argv):
    parser = argparse.ArgumentParser(prog="gma2blend", description=__doc__.splitlines()[0])
    parser.add_argument("addon", help=".gma file or extracted addon folder")
    parser.add_argument("out_dir")
    parser.add_argument("--all-models", action="store_true", help="also convert c_arms / viewmodels")
    parser.add_argument("--keep-outline", action="store_true", help="keep inverted-hull outline faces")
    parser.add_argument("--no-fbx", action="store_true", help="only write .blend files")
    parser.add_argument("--no-legfix", action="store_true", help="leave hip and limb bones untouched")
    args = parser.parse_args(argv)

    out_dir = os.path.abspath(args.out_dir)
    if os.path.isdir(args.addon):
        addon_dir, info = os.path.abspath(args.addon), {"name": os.path.basename(args.addon.rstrip("/\\"))}
    else:
        addon_dir = os.path.join(out_dir, "src")
        info = extract_gma(args.addon, addon_dir)

    mdls = sorted(
        os.path.join(folder, name)
        for folder, _, files in os.walk(os.path.join(addon_dir, "models"))
        for name in files
        if name.lower().endswith(".mdl")
        and (args.all_models or not VIEWMODEL.search(os.path.relpath(os.path.join(folder, name), addon_dir)))
    )
    if not mdls:
        sys.exit("No player models found in the addon.")

    os.makedirs(out_dir, exist_ok=True)
    models = [convert_model(mdl, addon_dir, out_dir, args) for mdl in mdls]
    with open(os.path.join(out_dir, "report.json"), "w", encoding="utf-8") as f:
        json.dump({"addon": info, "materials": read_vmt_materials(addon_dir), "models": models},
                  f, indent=1, ensure_ascii=False)
    write_checklist(os.path.join(out_dir, "report.md"), info, models)
    print(f"gma2blend: wrote {len(models)} model(s) to {out_dir}")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
