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

## Candidates (Crab Land)

How a map works (map-only, build 26133722): `World` (Networking.Shared) is a 4-value enum; the
lobby syncs it as `MainMenuManager.CurrentWorld` (`NetworkVariable<World>`); the scene is
`Assets/Scenes/<World>.unity` (globalgamemanagers); `WorldConfigurationRepository` holds UI data
only (icon, thumbnail, name entry). Waves: `RunManager.GetRunConfigurationForWorld` →
`RunConfiguration.Waves[].EnemyProbabilities` (`EnemyType` + weight); `EnemySpawner` maps
`EnemyType` → prefab via its `enemyTypeToPrefab` dictionary. Boss: `EndBossObjective` spawns
`bossConfigToSpawn.BossPrefab` (a `NetworkObject`); HP is `EnemyHealthContainer.maxHealth` on the
prefab, bar bound by `SetBossHealthContainerRpc`. A new `World` value would reach clients, saves
(`CompletedDifficultiesPerWorld`) and a missing scene, so Crab Land = base world + a mod flag.

| target | purpose | RVA / len | notes |
|---|---|---|---|
| `WorldsPanel.InitializeButtons()` | add the cloned "Crab Land" button | `0x2621670` / `0x3CD` | `worldButtons` list; clone not added to it |
| `WorldsPanel.OnWorldButtonClicked(World)` | clear the flag on a native pick | `0x2621DE0` / `0xF5` | |
| `MainMenuManager.OnNetworkSpawn()` | register named messages; auto-start | `0x26062B0` / `0x55C` | |
| `MainMenuManager.StartLevelOnServerRpc()` | debug auto-start | `0x2608120` / `0x1C1` | called, not patched |
| `EnemySpawner.CreateEnemyTypeToPrefabDictionary()` | remap every non-crab type to a crab prefab | `0x287B2D0` / `0x171` | data swap; catches waves, elites, groups |
| `EnemySpawner.SpawnEnemy(NetworkObject, Vector3, EnemySpawnRotation)` | log spawned prefab names | `0x287C600` / `0x14B` | private |
| `EnemyEventStarterController.StartEvent(EnemyEvent, CancellationToken)` | skip non-crab enemy events | `0x2895570` / `0x167` | plain class |
| `RunManager.GetRunConfigurationForWorld(World)` | log native pool; debug short run | `0x26E2860` / `0x89` | |
| `RunManager.HandleBossKilled()` | log boss defeated | `0x26E2A50` / `0x107` | |
| `EndBossObjective.SpawnBoss()` | swap `BossPrefab` to Crablodon, boss HP | `0x26BB150` / `0x4DB` | prefix + postfix restore |
| `EndBossObjective.InvokeEnemySpawnedRpc(NetworkObjectReference)` | boss scale on every machine | `0x26BA830` / `0x238` | ClientsAndHost RPC body |
| `EndBossObjective.FlagRunAsSuccessful()` | log run end | `0x26BA4C0` / `0xB6` | |
| `HealthContainer.TryToDamage(float, DamageInfo, bool)` | debug god mode (players only) | `0x28CD750` / `0x165` | |

Live findings (CrabLand 0.1.0, build 26133722, 2026-10-28/29): all 12 install (`hooks 12/12`).
- `CreateEnemyTypeToPrefabDictionary` fires on level load (37 entries); swapping 24 → census shows only Crabling/FleshCrab/ShieldCrab. Enemy events log as `Line` and are skipped.
- `WorldsPanel.InitializeButtons` fires when the panel opens, not on lobby load; `WorldsPanel.Show()` before that throws (`Single` on empty `worldButtons`); calling `Initialize()` directly NREs.
- Clone's `OnClickedAction` is never invoked by a click: the panel wires native buttons itself. Rewired via `OnClicked` + `Button.onClick` (untested).
- `RunManager.GetRunConfigurationForWorld` never fired in a run: inlined or another path. Run length cannot be cut there.
- Game time froze ~14 min with the window unfocused (coroutine `WaitForSeconds` stalled) — assumed pause-on-focus-loss.
- Static (game-map, bodies stubbed, so no call graph): objectives end via `ObjectiveDirector.NotifyObjectiveEnded(IAmObjective, bool isSuccessful, float timeLeft)` / event `OnObjectiveEnded`; `SuccessfullyCompletedObjectives` counts them; `ObjectiveDeterminer.Determine(waveNumber, numberOfWaves)` picks the objective per wave.
- Infusion path (static): `StoreManager.ChooseSpellImprovement` → `CheckForSpellInfusion(spell, level)` → `IInfusionStoreAvailabilityChecker.InfusionStoreShouldBeOffered(spell, spellLevel)` (`baseLevels`/`adjustedLevels`, double infusion) → `OfferSpellInfusion(spell)` → player picks → `ChooseSpellInfusion` → `ImprovementsManager.AddSpellInfusion(SpellType, ElementType)` (host; `SendSpellInfusionRpc(clientId, SpellInfusionMessage)` per client). `StoreType` = LevelUp|Infusion|Variant|Artifact|Debug.
- "Upgrade tiers" in code = meta progression (`UpgradeTierConfiguration`, `UpgradeManager.GetMaxLevel/IsMaxLevel(UpgradeIdentifier)`), persisted in saves; in-run spell improvements are rarity items, not tiers.
- Live (probe run 00:09–00:13, 2026-10-29): `ObjectiveDirector.StartObjective(Objective.Hunt)` from code starts a real Hunt; `NotifyObjectiveEnded(success=True)` fires on completion; `StartObjective(Objective.EndBoss)` from code runs the full native boss sequence (Hydra 165000 → Crablodon swap, scale ×3, `HandleBossKilled`, back to MainMenu). `FlagRunAsSuccessful` did not log.
- Live: infusion offered at spell levels `baseLevels=[7,14]` (`adjustedLevels=[6,13]`); `DetermineSpellLevel` +1 per level-up pick. `SpellAttributes` stores no level (only Element/AmountOfInfusions): level is computed, no setter seen, max level unknown. `ChooseSpellImprovement` prefix never logged although levels rose.
- `ChallengeNotification.QueueChallenge(ChallengeId)` (private, `Singleton<ChallengeNotification>`): queues the in-game unlock popup; CrabLand prefix skips it when `HideAchievementPopups` (unlock happens before, elsewhere — assumed, live check pending). Steam overlay toasts are not game code.
- Live (full Crab Land run 01:02–01:14, 2026-10-29, ShortRun=false): `ObjectiveDeterminer.Determine` never fired in ~12 min and no objective started; no enemy event logged either. Cause unknown; `RunManager.StartWave(wave, numberOfWaves)` probe added (logs `objectiveDeterminer.unlockedObjectives`/`waveNumbersForObjectives` in every run).
- Objectives unlock through profile progress (user: the progress screen lists none). Crab Land now starts its own: host timer, `ObjectivePool` (default Hunt,Capture,Collect) every `ObjectiveInterval` (150 s) when none is active. Only Hunt verified from code.
