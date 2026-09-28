# CLAUDE.md — working agreement for AI development sessions

Read `docs/STATUS.md` and the latest `docs/reports/DEV_REPORT_*.md` before changing anything. The TDD
(`docs/TECHNICAL_DESIGN.md`) is normative.

## Commands
```bash
dotnet test Headless/HeroGame.sln                                   # all simulation/persistence tests
dotnet build Headless/UnityCompileCheck                             # Unity runtime+editor compile check
dotnet build Headless/UnityCompileCheck -p:UnityEditorBuild=false   # player configuration
dotnet build Headless/UnityCompileCheck -p:ModernBcl=true           # all Unity code vs the .NET 8 class library (newer Unity 6.x)
python3 Tools/Unity/shadow_check.py                                 # no future Unity/.NET type name can clash with ours
python3 Tools/Unity/unity_meta.py generate && python3 Tools/Unity/unity_meta.py check   # after adding assets
python -m unittest discover -s Tools/Blender/tests                  # pipeline tests (bpy tests need bpy==4.2.0, py3.11)
```

## Rules
* `Game/Assets/_Project/Core` must never reference UnityEngine, IO or wall-clock time. Use `DeterministicRandom`,
  `WorldClock`, `Money` (integer cents). Iterate in stable id order when results matter.
* Every money/ownership change goes through `TransactionProcessor` (`WorldTransaction`). Never edit balances directly.
* `using HeroGame.*;` directives and aliases go inside the namespace block; only System/Unity/third-party usings sit
  at the top of the file (newer Unity versions add types like `UnityEngine.EntityId`; `shadow_check.py` enforces it).
* C# 9 only (Unity 6). Warnings are errors in the headless build. NUnit 3 classic asserts only in tests.
* New data belongs in `Game/Assets/StreamingAssets/Data/*.json`; extend `ContentLoader.Validate` for new references.
* Add a `.meta` for every new file/folder under `Game/Assets` (`unity_meta.py generate`).
* Never raise a status in `docs/STATUS.md` without evidence (test name, measurement, playable scene). Unity code that
  has not run inside Unity stays "IN DEVELOPMENT".
* Placeholders must be listed in `docs/ASSET_TRACKER.md`.
* Do not use real brands, real people, or other games' IP. The working title lives only in `GameInfo.WorkingTitle`.
