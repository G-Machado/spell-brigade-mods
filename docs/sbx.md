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
| `sbx state` | `NetworkStateMachine.Instance.currentState.GetState()` and its runtime type |
| `sbx lobby` | runtime type of `HostState.lobbyService` (only in the Host state) |
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
tools/sbx get 'Il2Cpp.NetworkStateMachine.Instance.IsHost()'
tools/sbx log
```

## Live check

(filled in by the live run)
