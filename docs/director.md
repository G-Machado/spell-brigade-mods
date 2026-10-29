# Crab Director: design

An in-game director that spawns crabs in response to how the players play. It runs on the host,
every frame, from fixed rules; Claude only tunes it offline (logs, `sbx`) and optionally sets
wave-level intent. P4 feature, built after crab-land. Crab facts: `docs/crabs.md`. Every type and
method below exists in `docs/game-map` (build 26133722); RVAs move on every update.

Three parts: what it reads (3 inputs), what it does (2 outputs), how it decides (intensity meter + rules).

## Host only

- Gate: `NetworkStateMachine.IsHost()` `0x26355E0` (or `IsSolo()` `0x2635700`). Clients run nothing.
- Spawns are networked `NetworkObject`s through the game's own `EnemySpawner`, so clients see them
  without client code.
- Fail-soft: every hook goes through `Shared/Hooks`; if the spawn hook is missing the director
  disables itself and logs it; the game's own waves keep running.

## Getting the services (no DI hacking)

| service | how | basis |
|---|---|---|
| `EnemySpawner` | `Singleton<EnemySpawner>.Instance` | `EnemySpawner : Singleton<EnemySpawner>` |
| `RunManager` | `NetworkSingleton<RunManager>.Instance` | `RunManager : NetworkSingleton<RunManager>` |
| `GameplayPlayerManager` | `Singleton<GameplayPlayerManager>.Instance` | |
| `PartyLevelManager` | `NetworkSingleton<PartyLevelManager>.Instance` | |
| `ServerGameplayEventBus` | postfix on its ctor `0x28D7A80` / `0x455`, keep `__instance` | the trackers' ctors are folded (`0x2288C70`), don't hook those |

## Inputs (3)

Sampled on the host at 4 Hz, smoothed with an exponential moving average (τ ≈ 3 s), per player
(`GameplayPlayerManager.AlivePlayers`, `Identities`, `GetClientPlayer(ulong)`).

1. **Pressure: how hard the crabs are hitting them**
   - damage taken per second: `IServerGameplayEventBus.OnDamageTaken` → `OnDamageTakenEventData(VictimClientId, Damage)`
   - HP fraction: `GameplayPlayer.healthContainer` (`PlayerHealthContainer` → `HealthContainer.CurrentHealth / MaxHealth`)
   - enemies chasing: `GameplayPlayer.ChasingEnemyCount`
   - downs: `OnDeath`, `OnReviveUsed`, `OnTeammateRevived`
2. **Output: how fast they are killing**
   - kills per second: `OnEnemyKilled` → `OnEnemyKilledEventData(DamageSourceClientId)` (no enemy type: per-type kills come from the census, below)
   - damage dealt per second: `OnEnemyDamageDealt` → `(DamageSourceClientId, Damage)`
   - build profile: `OnSpellDamageDone` → `OnSpellDamageDoneEventData(ClientId, SpellType, Amount)`, share of damage per `SpellType`
   - party level: `PartyLevelManager.GetLevel()`, `OnLevelUp`
3. **Positioning: where and how they move**
   - speed: delta of `GameplayPlayer.GetTransform().position` per sample (`GetPosition` is fine to *call*; it is folded, never hook it)
   - spread: max pairwise distance between alive players
   - stationary flag: speed < 1 m/s for 4 s

Run context (not an input, a scale): `RunManager.GetCurrentWave()`, `GetCurrentCycle()`,
`GetCurrentProgress()`, `GetWaveDifficultyMultiplier()`; `IRunManager.OnWaveChanged` / `OnCycleStarted`
events (subscribe; the `OnCurrentWaveChanged` method is folded, `0x2654F80`).

**Crab census** (own bookkeeping, needed by the rules): postfix `Enemy.Start` `0x285DA80`, prefix
`Enemy.OnDestroy` `0x285D2C0`, count by `Enemy.enemyType`. Gives live crabs per type and lets the
director attribute kills when a crab it spawned dies.

**Build classification**: `SpellType` has no category in code, so classify by name (assumed, verify live):

| class | `SpellType` |
|---|---|
| projectile | `Orb`, `Boomerang`, `Shotgun`, `Rifle`, `Smg`, `BombLauncher`, `Arrow`, `Splinter`, `Sawblade`, `Book` |
| area / zone | `EnergyZone`, `Proximity`, `Trail`, `Mine`, `Fence`, `Plant`, `FallingStar`, `Meteor`, `Bell`, `Clap` |
| beam | `Laser`, `Beam` |
| melee / other | `Slash`, `Summon`, `Dice` |

A player is "projectile build" when ≥ 60 % of their last 20 s of `OnSpellDamageDone` is projectile.

## Outputs (2)

1. **Spawn a crab group**:
   `EnemySpawner.Instance.SpawnGroupAsync(new EnemyGroupSpawnConfig(EnemyType, Count, Origin, MinSpawnRadius, MaxSpawnRadius, MinSpawnDuration, MaxSpawnDuration, EnemySpawnRotation))`
   (`IEnemySpawner.SpawnGroupAsync` `0x287C750` / `0x21A`, returns `Awaitable`). `Origin` is a
   `Transform` (the target player's), so spawns ring a chosen player; the duration pair spreads the
   group over time; `EnemySpawnRotation.TowardsClosestPlayer` or `Random`.
   Open (live/Ghidra): does `SpawnGroupAsync` respect `maxNumberOfConcurrentEnemies` /
   `HasCapacityForEnemyType` like the wave path does? If not, the director enforces its own cap.
2. **Modulate the game's own spawner**:
   - *relax*: prefix `EnemySpawner.SpawnRandomEnemy(WaveConfiguration, Vector3)` `0x287C9E0` / `0x50`, return false to skip a tick. 0x50 bytes: may be inlined into the spawn coroutine; if it never fires, fall back to a postfix on `GetRandomEnemyForWave` `0x287B860` / `0x26E`.
   - *bias*: postfix `GetRandomEnemyForWave` (returns the `NetworkObject` prefab), replace it with `EnemyPrefabRegistry.GetPrefab(EnemyType)` `0x28741E0` for the crab the rules want.
   - Crab Land already makes the waves crab-only; the director only shifts the mix and the rate. Coordinate with crab-land's hooks on the same methods (`docs/hooks.md`).

## Intensity meter (Left 4 Dead style)

Per player `I ∈ [0, 1]`, party intensity `I_party = max(I_p)` (the most stressed player drives pacing).

- rises with **damage taken** (+ `Damage / MaxHealth × 1.0`), **chasing crabs** (+ 0.01 × `ChasingEnemyCount` per second), **a down** (+ 0.5), **low HP** (HP < 30 %: + 0.05/s)
- decays at −0.04/s when nothing hits the player for 2 s (≈ 25 s from peak to zero)

Phases (party level), cycling as in L4D:

| phase | enter when | director does | leave when |
|---|---|---|---|
| **build-up** | relax ends | spawns by rule, budget ramps from 30 % to 100 % over 40 s | `I_party ≥ 0.85` → peak |
| **peak** | intensity crossed 0.85 | holds the current pressure, no new spawns of its own, game spawner normal | 3–5 s, then relax |
| **relax** | after peak, or team HP low | skips game spawn ticks at 70 %, no director spawns; Crablings only if someone is idle | 20 s **and** `I_party < 0.25`, max 45 s |

Spawn budget is in "crab points" per second (Crabling 1, FleshCrab 3, ShieldCrab 4, Crablodon 10:
**placeholders until the live HP probes**) × wave multiplier `GetWaveDifficultyMultiplier()` ×
player count factor.

## First rule set: Crab Land

Evaluated at 4 Hz during build-up; the first matching rule spends the budget, then a 6 s cooldown per rule.

1. **Breather.** Team HP (mean HP fraction) < 35 % or two downs in 20 s → force **relax**, regardless of phase.
2. **Campers get swarmed.** A player stationary ≥ 4 s → Crabling group, `Count` 8–14, ring 6–10 m around that player, over 2–3 s, `TowardsClosestPlayer`.
3. **Projectile builds meet shields.** For each projectile-build player, keep ≥ 2 live ShieldCrabs within ~15 m: spawn 2 in front (ring 10–14 m), over 1 s. Cap ShieldCrabs at 25 % of live crabs (they're no fun en masse).
4. **Flank the line.** Players spread > 20 m or one player leading the move: FleshCrab group of 3–5 at 12–16 m behind the movement direction (approximate with a ring on the rear player; a directional offset needs an origin `Transform`: use a helper GameObject the director places).
5. **Fast killers get a heavy.** Party kills/s in the top quartile of the last 60 s and no Crablodon alive → one Crablodon at 14–18 m, `SetOverrideImmuneToDespawning(true)` so it isn't culled when unseen.
6. **Default.** Mixed group from the remaining budget: 70 % Crabling, 20 % FleshCrab, 10 % ShieldCrab.

Caps: director crabs ≤ 40 % of live enemies and never above the game's
`maxNumberOfConcurrentEnemies` (read live). Unseen crabs still despawn after 3 s
(`Enemy.TimeUnseenUntilDespawn`), so ring spawns must stay inside the screen-bounds margin. Check
`IEnemySpawnPointManager.IsAvailableOutOfView(Vector3)` / `IsPositionAvailable(Vector3)` before
choosing the ring.

## Hook list (all host-only, fail-soft via `Shared/Hooks`)

| target | kind | RVA / len | use | folded? |
|---|---|---|---|---|
| `ServerGameplayEventBus..ctor()` | postfix | `0x28D7A80` / `0x455` | capture the bus | unique |
| `Enemy.Start()` | postfix | `0x285DA80` / `0x1E1` | census + | unique |
| `Enemy.OnDestroy()` | prefix | `0x285D2C0` / `0x155` | census − | unique |
| `RunManager.OnNetworkSpawn()` | postfix | `0x26E2E40` / `0x217` | run start: reset, subscribe `OnWaveChanged` | unique |
| `RunManager.StartWave(WaveConfiguration, int)` | prefix | `0x26E3BC0` / `0x242` | wave boundary: re-read intent (LLM section) | unique |
| `EnemySpawner.SpawnRandomEnemy(WaveConfiguration, Vector3)` | prefix | `0x287C9E0` / `0x50` | relax throttle | unique; may be inlined |
| `EnemySpawner.GetRandomEnemyForWave(WaveConfiguration)` | postfix | `0x287B860` / `0x26E` | bias the mix / throttle fallback | unique |

Called, never hooked: `EnemySpawner.SpawnGroupAsync` `0x287C750`, `EnemyPrefabRegistry.GetPrefab`
`0x28741E0`, `Enemy.SetOverrideImmuneToDespawning` `0x285D8D0`, `GameplayPlayer.GetPosition`
(`0x25A5E00`, folded), `WaveConfiguration.GetNumberOfEnemiesPerSpawn` (`0x68FD80`, folded),
`RunManager.OnCurrentWaveChanged` (`0x2654F80`, folded: use the `OnWaveChanged` event).

Every RVA above was checked against the map's shared-RVA set (642 RVAs shared by ≥ 2 methods).

## Logging (for tuning by Claude)

One JSON line per second to `<game>/UserData/CrabDirector/<run>.jsonl` (fresh file per run): raw
quantities only. Per player: HP, damage taken, kills, damage dealt, speed, chasing count, spell
damage by `SpellType`. Party: `I_party`, phase, budget, census by `EnemyType`, wave, cycle. One line
per director spawn: rule, type, count, target client. Rules and thresholds come from a config file
(`UserData/CrabDirector/rules.json`), so tuning is a config edit, not a rebuild.

## Optional: wave-level LLM director

Claude sets **intent once per wave**, never per frame; the rules above stay the executor.

- **When**: at `RunManager.StartWave` (prefix above), the mod writes a wave summary (last wave's log
  aggregates) and reads `intent.json` if one newer than the summary exists.
- **Intent** (all optional, clamped by the mod): crab mix weights, budget multiplier 0.5–1.5,
  relax length, rules on/off, a "theme" label for the log. No positions, no per-spawn calls.
- **Transport**: `sbx` (loopback bridge, owned by sbx-bridge) exposes `director.summary` /
  `director.intent`; or a sidecar process calling the API. If no intent arrives by the time the wave
  starts, the last intent (or defaults) applies: the game never waits on the model.
- **Why only intent**: latency (seconds) and cost rule out frame decisions; the meter already reacts
  in 250 ms. The model's job is the slow loop: "they're steamrolling with Rifles, lean on ShieldCrabs
  next wave".

## Live probes this design needs (via `sbx`)

1. Does `SpawnGroupAsync` honour the concurrent-enemy cap and `HasCapacityForEnemyType`?
2. `EnemySpawner.maxNumberOfConcurrentEnemies`, `extraPlayerInfluence` on Crab Land.
3. Crab HP / speed / contact damage (sets the crab-point costs), from the `docs/crabs.md` probe list.
4. `SpellType` classes: confirm projectile vs area per spell (one cast each, watch the spawned objects).
5. Does `SpawnRandomEnemy` fire per spawn (or is it inlined into the coroutine)?
6. `ChasingEnemyCount` behaviour: crabs targeting a player, or only some enemy kinds?
7. Damage numbers: `OnDamageTaken.Damage` before or after armor?
8. Is `ServerGameplayEventBus` constructed once per run or once per session (re-capture on run start?).
