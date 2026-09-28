#!/usr/bin/env python3
"""HeroGame Blender pipeline CLI.

Runs with either Blender or the bpy module:
    blender -b -P Tools/Blender/cli.py -- kit --art-root Game/Assets/_Project/Art
    python Tools/Blender/cli.py kit --art-root /tmp/art        (with `pip install bpy`)
    python Tools/Blender/cli.py kit --only SM_Bench_A --art-root /tmp/art
    python Tools/Blender/cli.py preview --out docs/images/blender_kit_preview.png --textures Game/Assets/_Project/Art/Environment/Textures
    python Tools/Blender/cli.py mcp '{"command": "list_catalog"}'
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
    if args.cmd == "mcp":
        from hg_pipeline import mcp_commands
        print(mcp_commands.run_json(args.payload))
        return 0
    return 2


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    sys.exit(main(argv))
