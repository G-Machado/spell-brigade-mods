# sbx: live access to the running game

`tools/sbx` is a Bash-callable CLI over an in-game bridge. Agents use it instead of an MCP server
(no schema cost per session, same as the `unity` CLI).

## Bridge choice: MelonMCP @ ce87f4f782

Surveyed from source on 2026-10-28, nothing run:

| | MelonMCP `elliotttate/MelonMCP` @ `ce87f4f782` | UnityInfoMCP `snowyegret23/UnityInfoMCP` @ `ffa6ca058e` |
|---|---|---|
| transport | line-delimited MCP JSON-RPC over TCP | line-delimited JSON-RPC over TCP |
| bind / port | `TcpListener(IPAddress.Loopback, 27015)` (hard-coded) | `BindHost`, first free port in 16001–16100 |
| eval | `execute_csharp`, `evaluate_expression` (reflection interpreter, not Roslyn) | none |
| get / set / invoke | `get_type_info`, `inspect_component`, `set_property`, `invoke_method`, statics via eval | component fields get/set only; no invoke, no statics |
| ML net6 build | pure reflection, needs only MelonLoader, 0Harmony, Newtonsoft (all in `MelonLoader/net6`) | multi-target csproj against DLLs committed in `includes/` |
| licence | MIT | MIT |

**Picked MelonMCP**: the only one with eval + set + invoke. UnityInfoMCP is a GameObject/component
inspector; `NetworkStateMachine.currentState` and `HostState.lobbyService` are private fields
behind an interface, which needs eval or invoke.

Known limits: the evaluator is a regex/reflection interpreter, so complex C# (lambdas, LINQ) may
not parse; port 27015 is Steam's default game-server port (free on this machine with the game
running, checked by `netstat` 2026-10-28). If it ever collides, patch the constant in
`MelonMCPPlugin.GetConfiguredPort` from `tools/bridge-build` and set `SBX_PORT`.

## Setup

```bash
tools/bridge-build            # clone the pin into tools/.bin/MelonMCP-<sha>/, check the bind, build
tools/bridge-build --deploy   # same, then copy MelonMCP.dll into <game>/Mods (game must be closed)
```

`bridge-build` never downloads a binary. It compiles the pinned source unmodified through a
generated wrapper csproj (`tools/.bin/MelonMCP-build/`) that picks up `Directory.Build.props`. It
refuses to build unless the source holds exactly one listener, `TcpListener(IPAddress.Loopback, …)`.

## Commands

| command | does |
|---|---|
| `sbx ping` | exit 0 when the bridge answers, exit 3 when it isn't reachable |
| `sbx state` | `NetworkStateMachine.currentState.GetState()` and its runtime type |
| `sbx lobby` | runtime type of `HostState.lobbyService` (current state, else `GetStateFor(Host)` without entering it) |
| `sbx get <expr>` | evaluate one C# expression |
| `sbx eval <code>` | run statements; `return` a value |
| `sbx find <Type> [limit]` | live objects of a Unity type |
| `sbx set <goPath> <Component> <member> <value>` | set a component field/property |
| `sbx invoke <goPath> <Component> <method> [json-args]` | call a component method |
| `sbx tools` / `sbx call <tool> [json]` | list bridge tools / raw `tools/call` |
| `sbx log [all]` | `hooks N/M` lines from `MelonLoader/Latest.log` (no bridge needed) |

Exit codes: 0 ok, 1 bridge error, 2 usage, 3 not reachable. Env: `SBX_PORT`, `SBX_TIMEOUT_MS`,
`GAME_DIR`. Game types are `Il2Cpp.<Type>` (interop namespace); private game fields are interop
properties of the same name.

## Examples

```bash
tools/sbx ping
tools/sbx state
tools/sbx get 'UnityEngine.Object.FindObjectOfType<Il2Cpp.NetworkStateMachine>().IsHost()'
tools/sbx log
```

## Evaluator rules (live, build 26133722)

- Reach singletons with `UnityEngine.Object.FindObjectOfType<Il2Cpp.T>()`: statics inherited from a
  generic base (`SingletonPersistent<T>.Instance`) are not found, and generic type names don't parse.
- Interface-typed fields come back as the interface wrapper (`Il2Cpp.INetworkState`). Interface
  methods work; for the runtime type cast first: `.Cast<Il2CppSystem.Object>().GetIl2CppType().FullName`.
- Private fields and methods are reachable by their game-map names (`currentState`, `GetStateFor`).
- Enums: `Il2Cpp.NetworkState.Host`. Generic methods: `Cast<Il2Cpp.HostState>()`.
- The bridge answers ~30 s after launch; `NetworkStateMachine` exists once StartMenu loads (~1 min).
- Harmless upstream errors at load: `RunInBackgroundPatch` (Harmony target missing) and
  "Could not subscribe to Msg logs".

## Live check (2026-10-29, build 26133722, ML 0.7.3)

```
$ netstat -ano | grep :27015
  TCP    127.0.0.1:27015        0.0.0.0:0              LISTENING       167240
$ tools/sbx ping
bridge ok on 127.0.0.1:27015
# at StartMenu:
currentState.GetState()                                     -> Offline   (runtime type OfflineState)
GetStateFor(Host).Cast<Il2Cpp.HostState>().lobbyService type -> SteamLobbyService (state still Offline)
$ tools/sbx log
[CrabLand] hooks 22/22
[Hello] hooks 1/1
```
