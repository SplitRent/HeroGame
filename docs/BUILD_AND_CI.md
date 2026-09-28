# Build, Run & CI

## Prerequisites
| Task | Needs |
|---|---|
| Simulation, tests, world host | .NET 8 SDK |
| Unity project | Unity Hub + Unity 6 LTS (6000.0.x) with Linux/Windows build support |
| Asset pipeline | Blender 4.2 LTS, or Python 3.11 + `pip install bpy==4.2.0` |
| Art in git | Git LFS (`git lfs install`) |

## Headless (no Unity needed)
```bash
dotnet test Headless/HeroGame.sln                        # 63 NUnit tests (same sources Unity runs)
dotnet build Headless/UnityCompileCheck                  # compile Runtime+Editor against Unity reference assemblies
dotnet build Headless/UnityCompileCheck -p:UnityEditorBuild=false   # player configuration
dotnet build Headless/UnityCompileCheck -p:ModernBcl=true           # all Unity code vs the .NET 8 class library (newer Unity 6.x)
python3 Tools/Unity/shadow_check.py                                 # no future Unity/.NET type name can clash with ours

cd Headless && dotnet build HeroGame.WorldHost -c Release
H=HeroGame.WorldHost/bin/Release/net8.0/herogame-world
$H new     --save ./saves/dev                # generate Port Arden (vertical slice)
$H run     --save ./saves/dev --days 30      # simulate a month, save incrementally
$H resume  --save ./saves/dev                # catch up the real time the server was offline
$H inspect --save ./saves/dev business       # also: economy | npc <name> | history | news | district
$H bench   --days 7                          # 10k-NPC stress layout benchmark
```

## Unity — first open
1. Open `Game/` in Unity Hub with Unity 6 (6000.0.x). Let packages resolve.
2. `HeroGame ▸ Setup ▸ Configure Project Settings` (Input System handling, linear colour, text serialization). Restart
   if prompted.
3. `Window ▸ Rendering ▸ HDRP Wizard ▸ Fix All` (creates the HDRP asset and default volume profile).
4. `HeroGame ▸ Build Main Menu Scene` and `HeroGame ▸ Build Greybox Vertical Slice`.
5. Open `Assets/_Project/Scenes/MainMenu.unity` and press Play: create your character, then **Continue** / **Host
   local world**. In the greybox: WASD/mouse, Shift sprint, Space jump, Ctrl crouch, E interact, **`** dev console,
   **F3** world/NPC inspector.
6. Optional: import the environment kit — `python Tools/Blender/cli.py kit --art-root Game/Assets/_Project/Art`.

Saves: `Application.persistentDataPath/Saves/{servers|story}/<slot>` (same format as the headless host).

> The Unity scripts are compile-verified against Unity reference assemblies (editor and player) on every push but have
> not been confirmed in a real editor yet. Run **HeroGame ▸ Health Check (run me first)** after opening and send its
> report plus any red Console errors; expect first-open fixes (tracked in `STATUS.md`). The README has the full
> first-run checklist and controls.

## CI
* **`ci.yml`** (every push/PR): .NET build + tests, Unity compile check (editor & player), world smoke test (create →
  30 days → inspect), 10k-NPC benchmark artifact; Blender pipeline tests + FBX kit artifact; `.meta` integrity; JSON
  validation.
* **`unity.yml`** (main + manual): GameCI EditMode tests and Windows client / Linux dedicated-server builds. Enable by
  adding repository secrets `UNITY_LICENSE` (personal: contents of the `.ulf`) **or** `UNITY_SERIAL` (pro), plus
  `UNITY_EMAIL` and `UNITY_PASSWORD`. Without secrets these jobs are skipped.
