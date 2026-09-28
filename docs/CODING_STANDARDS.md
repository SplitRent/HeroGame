# Coding Standards & Conventions

## Repository layout
```
Game/                          Unity 6 project
  Assets/_Project/
    Core/          HeroGame.Core          engine-agnostic simulation (no UnityEngine)
    Persistence/   HeroGame.Persistence   JSON, content, saves, journal (no UnityEngine)
    Runtime/       HeroGame.Runtime       MonoBehaviours / presentation
    Runtime.Input/ HeroGame.Runtime.Input Input System adapter
    Editor/        HeroGame.Editor        editor tools, import rules, CI builds
    Tests/EditMode HeroGame.Tests.EditMode NUnit tests (also run by dotnet test)
    Resources/UI   UXML/USS
    Art/ Audio/ Prefabs/ Scenes/ Generated/
  Assets/StreamingAssets/Data  data-driven content (JSON)
Headless/                      .NET solution: builds Core/Persistence from Game/, tests, world host, compile check
Tools/Blender                  asset pipeline (Python)
Tools/Unity                    repo tooling (meta integrity)
docs/                          design docs, status, reports
```

## C#
* **Language level C# 9** (Unity 6). No records, `init`, file-scoped namespaces, global usings, or required members.
  `Headless/Directory.Build.props` pins `LangVersion 9.0` and **treats warnings as errors**.
* Block-scoped namespaces matching folders: `HeroGame.<Assembly>.<Folder>`.
* `using HeroGame.*;` directives and all aliases go **inside** the namespace block; only `System.*`, `UnityEngine.*`,
  `UnityEditor.*` and third-party usings sit at the top of the file. Inner usings win over outer ones, so types that
  newer Unity or .NET versions add (`UnityEngine.EntityId`, `System.Diagnostics.ActivityKind`) can never make our
  names ambiguous. `Tools/Unity/shadow_check.py` enforces this in CI.
* Naming: `PascalCase` types/methods/properties/public fields; `_camelCase` private fields; `camelCase` locals and
  parameters; `I` prefix for interfaces; constants `PascalCase`.
* One primary type per file (small closely related types — enums, DTOs — may share a file).
* Keep classes focused. If a class has more than one reason to change, split it. No "GameManager".
* **Core rules:** no `UnityEngine`, no static mutable state, no `System.Random`/`DateTime.Now` in simulation (use
  `DeterministicRandom` and `WorldClock`), no floating point for money (`Money`/cents), no dictionaries keyed by
  `EntityId` in persisted DTOs (serialize lists), iterate collections in stable id order when results matter.
* Public serialized data classes use public fields with defaults (Newtonsoft + Unity friendly) and parameterless
  constructors.
* Runtime rules: no `Find*`/`GetComponent` in `Update`; cache references; no allocations in per-frame hot paths
  (reuse lists); dev tools behind `#if UNITY_EDITOR || DEVELOPMENT_BUILD`.
* Comments explain *why*, not *what*. XML doc summaries on public types and non-obvious members.

## Tests
* NUnit **3.x classic asserts only** (`Assert.AreEqual`, `Assert.IsTrue`, …) so tests run in Unity's bundled NUnit and
  in `dotnet test`. No `Assert.Multiple`, no NUnit 4 constraint-only APIs.
* Every bug fix gets a regression test. Every "TESTED" status in `STATUS.md` names its tests.
* Tests must be deterministic: fixed seeds, no wall-clock time.

## Data
* JSON content in `StreamingAssets/Data`, enums as strings, ids as readable keys. Validate with
  `ContentLoader.Validate` (`HeroGame ▸ Validate ▸ Content Data`). Never reference content by list index.

## Assets
* Names per `ASSET_PIPELINE.md` §3. Validation: `HeroGame ▸ Validate ▸ Asset Naming` (Unity) and `hg_pipeline.validate`
  (Blender).
* Every asset has its `.meta` committed. New files: `python3 Tools/Unity/unity_meta.py generate` (deterministic GUIDs)
  or let Unity create them — CI runs `unity_meta.py check`.
* Binary files go through Git LFS.

## Git
* Branch per change, PR to `main`, CI green before merge. Commit messages: imperative subject ≤ 72 chars, body explains
  why. Never commit `Library/`, `Temp/`, builds, or secrets.
