# spell-brigade-mods

MelonLoader 0.7.x mods for The Spell Brigade (Steam 2904000, Unity 6000.0.74f1 IL2CPP).
Plan, constraints and phases: `PLAN.md`. Hook catalog: `docs/hooks.md`.

## Rules for agents

- Never name a game type or method that is not in `docs/game-map` or seen in a live probe.
- Every patch goes through `Shared/Hooks`; report installed and failed counts.
- Probe live with `tools/sbx` before writing a hook (does it fire, do the values look right);
  `docs/sbx.md`.
- The bridge binds 127.0.0.1 only.
- Never edit game files; mods go in `Mods/` only. Back up the save folder before any mod that
  touches saves, and before the first launch of a new mod build.
- Never sweep `Il2Cpp.*` wrapper types with reflection (reading `NativeMethodInfoPtr_*` or any
  static field on many types). Each wrapper's static constructor calls `il2cpp_runtime_class_init`,
  which runs the game's own static constructor right then. Walk il2cpp's native tables instead
  (`IL2CPP.il2cpp_image_get_class`, `il2cpp_class_get_methods`), as `SharedCodeGuard` does.
- A multiplayer change is not done until it has run on two machines.

## Layout

- `Directory.Build.props` — GameDir, MelonNet6, InteropRoot (override GameDir with `-p:GameDir=...`)
- `Shared/` — Hooks (fail-soft + shared-code guard), linked into each mod as source
- `Hello/` — P0 smoke mod, logs `hooks 1/1`
- `scripts/deploy` — build Release, copy mod DLLs to `<game>/Mods`
- `tools/bridge-build` — builds the MelonMCP bridge from a pinned commit (`--deploy` copies it to Mods)
- `tools/sbx` — CLI over the bridge (127.0.0.1:27015): ping, state, get, eval, find, set, invoke, log
- `tools/dump` — regenerates `docs/game-map/` (gitignored); `tools/.bin/` holds downloaded tools (gitignored)

## Facts (build 26133722, 2026-10-28)

- MelonLoader 0.7.3 works on Unity 6000.0.74f1. Interop: game types in the global namespace become
  `Il2Cpp.<Type>`; assemblies are `Il2Cpp<Name>.dll` (e.g. `Il2CppRuntime.dll`) under `InteropRoot`.
- Almost all game code is in `Runtime.dll` (`docs/game-map/Runtime/`), not `Assembly-CSharp`.
- ML's interop step already runs Cpp2IL 2022.1.0-pre-release.21 with RVA attributes and leaves the
  dummy DLLs in `<game>/MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out/`;
  `tools/dump` decompiles those by default (set `CPP2IL=<exe>` to run Cpp2IL standalone).
- `MainMenuManager.Awake` fires ~2 min after `hooks 1/1` on first launch (main menu load is slow).
- Run `tools/dump` from outside `docs/game-map/` (a shell cwd inside it blocks the `rm`).
- Saves: `%USERPROFILE%\AppData\LocalLow\BoltBlasterGames\TheSpellBrigade\`, ten rotating
  `save_slot_N` files plus `save_meta` (`active_slot`), EasySave3 (AES + gzip). If a save can't be
  loaded the game starts a fresh profile and writes it over the next slots; older slots keep the
  old data, and copying a good slot file over the bad ones restores it (no re-encryption needed).
- Until 2026-10-29 `SharedCodeGuard` swept every wrapper type at mod load. That ran
  `SaveMigrator`'s static constructor too early (`Version.Parse(null)`), the save failed to load,
  and the game wrote a fresh profile. Symptom in `Player.log`: `TypeInitializationException` for
  `SaveMigrator` from `SaveManager.TryValidateAgainstExistingSave`.
