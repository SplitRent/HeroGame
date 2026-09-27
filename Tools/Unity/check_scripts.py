#!/usr/bin/env python3
"""Unity script sanity checks that the .NET compile check cannot see.

Unity binds a serialized component to its script by the script *file*: a MonoBehaviour whose class name differs
from its file name (or that shares a file with another MonoBehaviour) is saved into scenes/prefabs as a
"missing script". This walks Runtime/ and Editor/ and fails on any such class.

    python3 Tools/Unity/check_scripts.py
"""
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(__file__), "..", "..", "Game", "Assets", "_Project")
UNITY_BASES = {"MonoBehaviour", "ScriptableObject", "EditorWindow", "Editor"}
CLASS = re.compile(r"\bclass\s+(\w+)\s*(?::\s*([\w\.]+))?")


def main():
    declared = {}
    for folder in ("Runtime", "Editor"):
        for dirpath, _, files in os.walk(os.path.join(ROOT, folder)):
            for f in files:
                if not f.endswith(".cs"):
                    continue
                path = os.path.join(dirpath, f)
                with open(path, encoding="utf-8") as fh:
                    for m in CLASS.finditer(fh.read()):
                        declared.setdefault(m.group(1), []).append((path, (m.group(2) or "").split(".")[-1]))

    def is_unity_object(name, seen=()):
        if name in UNITY_BASES:
            return True
        if name in seen or name not in declared:
            return False
        return any(base and is_unity_object(base, seen + (name,)) for _, base in declared[name])

    problems = []
    for name, entries in declared.items():
        for path, base in entries:
            if base and is_unity_object(base) and os.path.splitext(os.path.basename(path))[0] != name:
                problems.append(f"{os.path.relpath(path, ROOT)}: {name} must live in {name}.cs")
    for p in sorted(problems):
        print(p)
    print(f"{len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
