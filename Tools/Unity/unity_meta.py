#!/usr/bin/env python3
"""
Unity .meta integrity tool.

Unity identifies every asset by the GUID in its .meta file. If two people import the same new file
without its .meta, each machine invents a different GUID and references break. This tool:

  generate  create missing .meta files with a deterministic GUID derived from the asset path
            (so the same file gets the same GUID on every machine, even before Unity sees it)
  check     fail if any asset lacks a .meta, or any .meta has no asset (used by CI)

Minimal metas (fileFormatVersion + guid) are valid; Unity fills in importer settings on first import.

Usage: python3 Tools/Unity/unity_meta.py [generate|check] [--root Game/Assets]
"""
import argparse
import hashlib
import os
import sys

IGNORED_NAMES = {".DS_Store", "Thumbs.db"}
IGNORED_SUFFIXES = (".tmp", "~")


def is_ignored(name: str) -> bool:
    return name.startswith(".") or name in IGNORED_NAMES or name.endswith(IGNORED_SUFFIXES)


def deterministic_guid(rel_path: str) -> str:
    return hashlib.md5(("herogame:" + rel_path.replace(os.sep, "/")).encode("utf-8")).hexdigest()


def walk(root: str):
    """Yields (path, is_dir) for every asset Unity would import under root."""
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if not is_ignored(d))
        for d in dirnames:
            yield os.path.join(dirpath, d), True
        for f in sorted(filenames):
            if is_ignored(f) or f.endswith(".meta"):
                continue
            yield os.path.join(dirpath, f), False


def meta_text(guid: str, is_dir: bool) -> str:
    if is_dir:
        return ("fileFormatVersion: 2\nguid: %s\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n"
                "  userData: \n  assetBundleName: \n  assetBundleVariant: \n" % guid)
    return "fileFormatVersion: 2\nguid: %s\n" % guid


def generate(root: str, project_root: str) -> int:
    created = 0
    for path, is_dir in walk(root):
        meta = path + ".meta"
        if os.path.exists(meta):
            continue
        rel = os.path.relpath(path, project_root)
        with open(meta, "w", newline="\n") as fh:
            fh.write(meta_text(deterministic_guid(rel), is_dir))
        created += 1
    print("created %d .meta file(s)" % created)
    return 0


def check(root: str) -> int:
    problems = []
    guids = {}
    for path, _ in walk(root):
        meta = path + ".meta"
        if not os.path.exists(meta):
            problems.append("missing meta: " + path)
            continue
        with open(meta) as fh:
            for line in fh:
                if line.startswith("guid:"):
                    guid = line.split(":", 1)[1].strip()
                    if guid in guids:
                        problems.append("duplicate guid %s: %s and %s" % (guid, guids[guid], path))
                    guids[guid] = path
                    break
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if not is_ignored(d)]
        for f in filenames:
            if f.endswith(".meta") and not os.path.exists(os.path.join(dirpath, f[:-5])):
                problems.append("orphan meta: " + os.path.join(dirpath, f))
    for p in problems:
        print(p)
    print("%d problem(s), %d assets" % (len(problems), len(guids)))
    return 1 if problems else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["generate", "check"])
    parser.add_argument("--root", default=os.path.join(os.path.dirname(__file__), "..", "..", "Game", "Assets"))
    args = parser.parse_args()
    root = os.path.normpath(args.root)
    project_root = os.path.dirname(root)
    return generate(root, project_root) if args.command == "generate" else check(root)


if __name__ == "__main__":
    sys.exit(main())
