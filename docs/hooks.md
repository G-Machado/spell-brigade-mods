# Hook catalog

One row per hook target. RVAs come from `docs/game-map` (`tools/dump`) and move on every game
update; re-check them against the map before trusting a row. Build = Steam build id.

**Status:** `live` = installed by a mod and seen firing; `candidate` = from the map only, never patched.

## Live

| target | purpose | RVA / len | proven by | last verified build |
|---|---|---|---|---|
| `MainMenuManager.Awake()` (Runtime) | P0 smoke: fires when the main menu loads | `0x2604FE0` / `0x37C` | `Hello` mod: `hooks 1/1` + `MainMenuManager.Awake fired` in `MelonLoader/Latest.log` | 26133722 (2026-10-28; guard logged rva=0x2604FE0, sharers=0) |

## Candidates (P2 LAN spike)

| target | purpose | RVA / len | notes |
|---|---|---|---|
| `DirectConnectionLobbyService.InitializeAsHost()` | host without Steam/EOS lobby | `0x2624530` / `0x17C` | async: logic in the nested state machine's `MoveNext` |
| `DirectConnectionLobbyService.ConnectWithLobby(string lobbyCode)` | join by IP (lobby code as address?) | `0x26242C0` / `0xCB` | async; `ConnectWithLobby(ILobby)` at `0x2624390` |
| `DirectConnectionLobbyService.GetLobbyCode()` | what the host shows to share | `0x2624500` / `0x2C` | small: may be inlined at call sites |
| `LANOptionsPanel.Awake()` / `Start()` | the hidden dev LAN panel; surface it | `0x25F3D70` / `0xC1`, `0x25F3FB0` / `0xED` | |
| `LANOptionsPanel.StartHost()` / `StartClient()` | dev host/join buttons | `0x25F3F30` / `0x7D`, `0x25F3E40` / `0xEE` | no IP field |
| `NetworkStateMachine.TransitionTo(NetworkState)` | observe/drive state changes | `0x2635C10` / `0xDD` | async |
| `NetworkStateMachine.GetStateFor(NetworkState)` | which `INetworkState` each state maps to | `0x2635390` / `0x1EB` | |
| `NetworkStateMachine.IsServerFull()` | player cap | `0x2635640` / `0xB6` | MorePlayers rewrites a `cmp reg,4` here (Iced, not Harmony) |
| `HostState.Enter()` | host start; holds `lobbyService` (ctor arg, readonly field) | `0x2644B00` / `0x511` | async |
| `HostState.OnApprovalCheck(ConnectionApprovalRequest, ConnectionApprovalResponse)` | connection approval (version check, cap) | `0x26453F0` / `0x2FD` | |

## Do not hook (shared native body)

Identical code folding: patching one patches all. `SharedCodeGuard` refuses these.

- `0x691A80` (len 3, empty `ret`): `DirectConnectionLobbyService.ResetLobbyState/Invite/StartGame/SetPrivate/SetPublic/SetLobbyConfig`, and many more
- `0x6B3760` (len 3): `DirectConnectionLobbyService.IsLobbyCodeValid/HasPlatformLobby`, and others
- `0x230A510` (len 0x12): `DirectConnectionLobbyService.GetPlayerId/GetPlatformLobbyId`
- `0x2605970`: `MainMenuManager.IsEndlessModeChanged` and `IsPeacefulModeChanged`
- 693 RVAs in the game map are shared by two or more methods (`grep -rh 'RVA = ' docs/game-map | sort | uniq -d`)
