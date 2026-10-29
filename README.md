# Spell Brigade mods

MelonLoader mods for [The Spell Brigade](https://store.steampowered.com/app/2904000/) (Unity 6, IL2CPP).
Plan and findings: [PLAN.md](PLAN.md). Rules for working in this repo: [CLAUDE.md](CLAUDE.md).

| mod | status |
|---|---|
| `Hello` | test mod: one hook on `MainMenuManager.Awake`, logs `hooks 1/1` |
| `CrabLand` | in progress: crab-only map with a crab boss |

## Setup

1. Own the game on Steam and install it.
2. Install **MelonLoader 0.7.3**: download `MelonLoader.x64.zip` from
   https://github.com/LavaGang/MelonLoader/releases and extract it into the game folder
   (next to `TheSpellBrigade.exe`).
3. Launch the game once and wait for the main menu (the first launch takes a few minutes while
   MelonLoader generates `MelonLoader/Il2CppAssemblies/`), then quit.
4. Install the **.NET SDK** (6 or newer) and **Git for Windows** (the scripts run in Git Bash).
5. Clone this repo and run `scripts/deploy` from Git Bash.
6. Launch the game; `<game>/MelonLoader/Latest.log` should show `hooks N/N` for each mod.

Game installed somewhere else? Set both `GAME_DIR` (scripts, Git Bash path form) and `GameDir`
(MSBuild, Windows path form), or pass `-p:GameDir="D:\...\The Spell Brigade"` to `dotnet build`.

## Game map

`docs/game-map/` holds decompiled signatures of the game's own assemblies (empty bodies + RVAs),
greppable. It is generated, not committed: run `tools/dump` after the game has been launched once
with MelonLoader (needs `dotnet tool install -g ilspycmd`). Rerun it after every game update.

`docs/hooks.md` is the hook catalog: target, purpose, proven by, last verified build.

## Working on a mod

- One folder per mod with a `<Mod>/<Mod>.csproj`; `scripts/deploy` builds and copies every one.
- Every patch goes through `Shared/Hooks` (fail-soft, logs installed/total; 0 installed is an error).
- Only name game types and methods that exist in `docs/game-map` or were seen live.
- A multiplayer change is not done until it has run on two machines.
- Branch per feature, pull request into `master`.
- Never commit game files, interop DLLs or the game map; don't redistribute other people's mods.
