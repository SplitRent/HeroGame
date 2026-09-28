#!/usr/bin/env python3
"""HeroGame Blender pipeline CLI.

Runs with either Blender or the bpy module:
    blender -b -P Tools/Blender/cli.py -- kit --art-root Game/Assets/_Project/Art
    python Tools/Blender/cli.py kit --art-root /tmp/art        (with `pip install bpy`)
    python Tools/Blender/cli.py kit --only SM_Bench_A --art-root /tmp/art
    python Tools/Blender/cli.py preview --out docs/images/blender_kit_preview.png --textures Game/Assets/_Project/Art/Environment/Textures
    python Tools/Blender/cli.py mcp '{"command": "list_catalog"}'
    blender -b -P Tools/Blender/cli.py -- human --mpfb "<MPFB extension folder>" --art-root Game/Assets/_Project/Art
"""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


def main(argv):
    parser = argparse.ArgumentParser(prog="hg-blender")
    sub = parser.add_subparsers(dest="cmd", required=True)
    k = sub.add_parser("kit", help="build and export the environment kit")
    k.add_argument("--art-root", required=True)
    k.add_argument("--only", nargs="*")
    k.add_argument("--lenient", action="store_true", help="export even if validation reports errors")
    p = sub.add_parser("preview", help="render the kit as a textured street scene (Cycles)")
    p.add_argument("--out", required=True)
    p.add_argument("--textures", required=True, help="folder with the kit's T_*_BC/_N textures")
    p.add_argument("--samples", type=int, default=64)
    h = sub.add_parser("human", help="build the realistic human body (blend shapes, rig) from MakeHuman data")
    src = h.add_mutually_exclusive_group(required=True)
    src.add_argument("--mpfb", help="the MPFB extension folder (production: CC0 MakeHuman assets)")
    src.add_argument("--makehuman-dump", help="makehuman-data npm package folder (development only, never commit its output)")
    h.add_argument("--art-root", help="writes <art-root>/Characters/Human/SK_Human.fbx")
    h.add_argument("--out", help="output folder instead of the art root")
    h.add_argument("--check", action="store_true", help="only report what the data provides")
    m = sub.add_parser("mcp", help="run one MCP command given as JSON")
    m.add_argument("payload")
    args = parser.parse_args(argv)

    if args.cmd == "kit":
        from hg_pipeline import kit
        reports = kit.build_kit(os.path.abspath(args.art_root), only=args.only, strict=not args.lenient)
        for r in reports:
            lods = ", ".join("%s=%d" % (k.rsplit("_", 1)[-1], v) for k, v in r["lods"].items())
            warnings = sum(1 for i in r["issues"] if i["severity"] == "warning")
            print("%-22s %-16s tris[%s] hulls=%d materials=%d warnings=%d" % (r["asset"], r["category"], lods, len(r["collision_hulls"]), len(r["materials"]), warnings))
        print("exported %d asset(s) to %s" % (len(reports), os.path.abspath(args.art_root)))
        return 0
    if args.cmd == "preview":
        from hg_pipeline import preview
        print(preview.render(args.out, os.path.abspath(args.textures), samples=args.samples))
        return 0
    if args.cmd == "human":
        from hg_pipeline import makehuman as mh_data
        data = mh_data.MakeHumanData.from_mpfb(args.mpfb) if args.mpfb else mh_data.MakeHumanData.from_threejs_dump(args.makehuman_dump)
        print(data.report())
        if args.check:
            return 0
        from hg_pipeline import humans
        out = args.out or (os.path.join(os.path.abspath(args.art_root), "Characters", "Human") if args.art_root else None)
        if out is None:
            print("give --art-root or --out")
            return 2
        root = mh_data.MakeHumanData.find_mpfb_data(args.mpfb) if args.mpfb else os.path.join(args.makehuman_dump, "public", "data", "skins")
        skin = humans.find_skin_texture(root) if root else None
        print("skin detail from:", skin or "(none: flat tone)")
        manifest = humans.build_and_export(data, out, skin)
        print("SK_Human: %d vertices, %d bones, %d macro shapes, missing targets: %s" % (
            manifest["Vertices"], len(manifest["Bones"]), len(manifest["Macros"]), ", ".join(manifest["MissingTargets"]) or "none"))
        print("wrote", out)
        return 0
    if args.cmd == "mcp":
        from hg_pipeline import mcp_commands
        print(mcp_commands.run_json(args.payload))
        return 0
    return 2


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    sys.exit(main(argv))
