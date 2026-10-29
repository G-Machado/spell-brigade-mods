# Spell Brigade mods — plan

Status 2026-09-26: research done, nothing built. Game build 25343385 (1.1.4),
installed at `C:\Program Files (x86)\Steam\steamapps\common\The Spell Brigade`.
2026-10-28: game patched to build 26133722 (2026-10-22); Unity, metadata v31 and the LAN /
state-machine class names unchanged. Still nothing built, no loader installed.

## Goals

1. Co-op over local wifi by direct IP, every PC owning its own copy.
2. A workflow where Claude agents build mods that are grounded in the game's real code and
   verified in the running game.

## Findings

| finding | source | confidence |
|---|---|---|
| Unity 6000.0.74f1, IL2CPP | `UnityPlayer.dll` version, `GameAssembly.dll` | confirmed |
| Netcode for GameObjects 1.x + Unity Transport 2; EOS/Steam/Switch transports | `ScriptingAssemblies.json` | confirmed |
| No active anti-cheat: no EAC install, EOS AntiCheat is bindings only | install folder, metadata | confirmed present-but-unused (inactive is assumed) |
| Direct-IP code ships: `DirectConnectionLobbyService`, dev `LANOptionsPanel` (host/client buttons, no IP field) | `global-metadata.dat` | present: confirmed; working: **unknown** |
| Connection flow: `NetworkStateMachine` + Offline/Online/Solo/Host/ConnectingToHost/ClientMainMenu states; `HostState.lobbyService`, `HostState.OnApprovalCheck` | metadata, MorePlayers source | confirmed |
| 4-player cap is an inlined native constant (`cmp reg,4`) | Everelsu MorePlayers `CodePatch` | confirmed by a working mod |
| Saves are EasySave3, AES-CBC with a PBKDF2 key | te-chan / oAntz save editors | confirmed by their code |
| Ecosystem: 16 Nexus + 5 Thunderstore mods; only Everelsu's MelonLoader 0.7.x suite is current | Nexus GraphQL, Thunderstore API | confirmed |

## IL2CPP constraints every mod must respect

1. **No method bodies** in the interop assemblies: only signatures. Logic comes from
   disassembly (Cpp2IL, Ghidra) or from probing the running game.
2. **Inlining.** Small methods are compiled into their callers; a Harmony patch on them does not
   fire at inlined call sites. Patch the caller, or patch native code.
3. **Constants live in machine code.** Harmony cannot change them; rewrite the instruction with
   Iced, matched by pattern, never by offset.
4. **Identical code folding.** Distinct methods can share one native body; patching one changes
   all. Detect it and refuse (the SharedCodeGuard idea).
5. **Updates move everything.** Install hooks one by one, fail-soft, log each result, report
   `installed/total`. Zero installed is an error.

## Architecture

```
spell-brigade-mods/
  CLAUDE.md             agent rules (seeded from "Rules for agents" below)
  PLAN.md
  Directory.Build.props GameDir, MelonNet6, InteropRoot
  Shared/               Hooks (fail-soft + shared-code guard), NativePatch (Iced), Log
  Lan/                  the LAN mod
  tools/sbx             CLI to the in-game bridge (HTTP/TCP on 127.0.0.1)
  tools/dump            game-map generator
  docs/game-map/        generated signatures per namespace, greppable
  docs/hooks.md         hook catalog: target, purpose, proven by, last verified build
  scripts/deploy        build, copy DLLs to <game>/Mods
```

`Shared/` is our own code. Everelsu's repo has no license: study it, don't copy it.

### Agent API: three layers

- **Static knowledge:** `docs/game-map` + `docs/hooks.md`. The agent greps these first and never
  invents a type or method name.
- **Live access:** an existing in-game bridge (UnityInfoMCP or MelonMCP, both MelonLoader,
  loopback by default) driven through `sbx` from Bash: inspect, get/set, invoke, eval, patch.
  Build our own bridge only if both fall short.
- **Guardrails:** every patch goes through `Shared/Hooks`; the bridge binds 127.0.0.1 only; the
  hook count is checked after every launch.

### Game map recipe (P1)

The metadata is v31 (Unity 6). `ilspycmd` 11 and the .NET 10 SDK are installed; Ghidra is not.

| need | source | output |
|---|---|---|
| greppable signatures | Cpp2IL `--output-as dummydll` → `ilspycmd -p` | one `.cs` per type, empty bodies, `[Address(RVA=…)]` and `[FieldOffset]` attributes |
| compile references | MelonLoader first launch → `<game>/MelonLoader/Il2CppAssemblies/` | interop DLLs (don't decompile these: the bodies are marshalling noise) |
| method logic, when needed | Ghidra on `GameAssembly.dll`, functions named from the Cpp2IL RVAs | C-like pseudocode for single methods |
| runtime truth | the bridge / UnityExplorer | live objects, bound services, field values |

Dump only the game assemblies (`Assembly-CSharp`, `Networking.*`, `GameContracts`, `Runtime`);
skip Unity and System. Il2CppDumper probably can't read v31 (assumed); skip it. Run the dump in a
fork: the output goes to disk and agents grep it.

### The loop for one change

1. Grep the game map and hook catalog for a candidate hook.
2. Probe it live with `sbx`: does it fire, do the arguments look right.
3. Patch it live with eval: does the effect show.
4. Write it into the mod, build, deploy.
5. Restart; `sbx` reads the log for `hooks N/N`; the user playtests for feel.

### Who does what

- **Claude, main session:** search, code, build, verify through the bridge.
- **Forks:** noisy, bounded work (dumps, build logs, wide greps), reporting summaries.
- **User:** launches the game, playtests, runs the second PC, judges feel.

## Phases

| phase | work | exit criterion |
|---|---|---|
| P0 Workspace | repo, props, deploy script, MelonLoader 0.7.x, interop generated, `Shared/Hooks` | hello mod logs `hooks 1/1` in the MelonLoader log |
| P1 Agent API | dump game map, seed hook catalog, try UnityInfoMCP + MelonMCP, write `sbx` | from Bash, read the current `NetworkStateMachine` state and `HostState.lobbyService` type |
| P2 LAN spike | live only: host via `DirectConnectionLobbyService` + Unity Transport on 0.0.0.0:7777, client joins by IP | two PCs in the same run. **Gate:** if the direct path is broken, implement our own lobby service or fall back to Steam P2P |
| P3 LAN mod | ModMenu page Host / Join IP / port, firewall rule helper, version check, MorePlayers compatibility | a full run on two PCs with the router offline from the internet |
| P4 Content | mods from the hook catalog: run rules, spells/stats, UI | per mod: fail-soft hooks, ModMenu page, catalog entries updated |

## Risks

- The direct path may be half-wired dev code. The P2 gate decides.
- The menus may require an EOS login when offline; may need a patch to the login check or the
  Solo state.
- Game updates break hooks and native patches; mitigated by fail-soft install and the catalog's
  "last verified build" column.
- Neither bridge has been tested on Unity 6000.0.74; P1 tries both.
- ModMenu may have no text input for the IP (`KeyboardCapture.cs` hints it might); the fallback
  is a MelonPreferences entry.
- Legal: reimplement rather than copy unlicensed code, don't redistribute others' mods, every PC
  owns the game.

## Rules for agents (seed for CLAUDE.md)

- Never name a game type or method that is not in `docs/game-map` or seen in a live probe.
- Every patch goes through `Shared/Hooks`; report installed and failed counts.
- The bridge binds 127.0.0.1 only.
- Never edit game files; mods go in `Mods/` only. Back up the save folder before any mod that
  touches saves.
- A multiplayer change is not done until it has run on two machines.

## Decisions (2026-10-28)

1. Loader: **MelonLoader 0.7.x** (every current mod uses it).
2. Wifi setup: **every PC owns the game and has internet**; offline-router play is not a goal.
3. MorePlayers: **skip for now**; revisit as a P3 compatibility item.

## References

- Everelsu mods: https://github.com/Everelsu/SpellBrigade
- TheSpellBrigadeHelper: https://github.com/moemoli/TheSpellBrigadeHelper
- Save editor: https://github.com/te-chan/the-spell-brigade-save-editor
- UnityInfoMCP: https://github.com/snowyegret23/UnityInfoMCP
- MelonMCP: https://github.com/elliotttate/MelonMCP
- bepinex-mcp: https://github.com/rkuhn153/bepinex-mcp
- Nexus GraphQL: POST https://api-router.nexusmods.com/graphql (keyless)
- Thunderstore: https://thunderstore.io/c/the-spell-brigade/api/v1/package/
