# spell-brigade-mods

MelonLoader 0.7.x mods for The Spell Brigade (Steam 2904000, Unity 6000.0.74f1 IL2CPP).
Plan, constraints and phases: `PLAN.md`. Hook catalog: `docs/hooks.md`.

## Rules for agents

- Never name a game type or method that is not in `docs/game-map` or seen in a live probe.
- Every patch goes through `Shared/Hooks`; report installed and failed counts.
- The bridge binds 127.0.0.1 only.
- Never edit game files; mods go in `Mods/` only. Back up the save folder before any mod that
  touches saves.
- A multiplayer change is not done until it has run on two machines.

## Layout

- `Directory.Build.props` — GameDir, MelonNet6, InteropRoot (override GameDir with `-p:GameDir=...`)
- `Shared/` — Hooks (fail-soft + shared-code guard), linked into each mod as source
- `Hello/` — P0 smoke mod, logs `hooks 1/1`
- `scripts/deploy` — build Release, copy mod DLLs to `<game>/Mods`
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
