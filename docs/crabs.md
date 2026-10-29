# Crab bestiary

Static research from `docs/game-map` (build 26133722). The map has signatures and RVAs only, no
bodies: behaviour is inferred from type and field names, and every stat value lives in assets
(prefabs, ScriptableObjects). Anything marked **live** needs a probe once `sbx` exists (list at the
end). RVAs move on every update: re-check them against the map.

## Crabs have no classes of their own

No type in any game assembly is named after a crab. A crab is an **`EnemyType` value** plus a
prefab built from generic enemy components. All five crab-named values appear in `EnemyType`,
`CauseOfDeath` and `SpellTargetType`:

| `EnemyType` | value | petrified variant | notes |
|---|---|---|---|
| `Crabling` | 0 | — | first value; swarm unit by name |
| `FleshCrab` | 1 | `petrifiedFleshCrabPrefab` | |
| `Snipjaw` | 3 | — | **crab? unsettled** (see below) |
| `ShieldCrab` | 5 | `petrifiedShieldCrabPrefab` | no shield type exists; mechanism is **live** |
| `Crablodon` | 7 | `petrifiedCrablodonPrefab` | largest crab by name |
| golden crab | — | — | not an `EnemyType`; most likely the `HuntTarget` objective (below) |

Values are by declaration order (ilspy prints no explicit values, so they run 0, 1, 2, …);
`EnemyType` has 41 values, `Crabling` … `CursedArtifact`.

**Snipjaw.** Nothing in code ties it to the crabs: it sits between `Tripod` and `TeethyWorm`, has
no petrified variant and no crab-named field. The name suggests pincers. Settle it **live** (prefab
name, model, animator).

## Shared enemy pipeline (applies to every crab)

`Enemy : NetworkBehaviour` (Runtime): one per enemy prefab.

- fields: `enemyType`, `contactDamage`, `damageToBreakableProps`, `immuneToDespawning`,
  `hitbox`, `spellTarget`, `damageMultiplier` (`DamageMultiplier`), `enemyDifficultyParameters`
- despawn when unseen: `TimeUnseenUntilDespawn = 3f`, `DespawnCheckInterval = 0.5f`,
  `ShouldDespawnBecauseUnseen()` `0x285DA10`, `SetOverrideImmuneToDespawning(bool)` `0x285D8D0`
- lifecycle: `Awake` `0x285CCC0` / `0x347`, `Start` `0x285DA80` / `0x1E1`, `Update` `0x285DC70` / `0x47D`,
  `OnDestroy` `0x285D2C0` / `0x155`

Components a crab prefab may carry (which ones each crab has is **live**):

| component | what it gives | key members |
|---|---|---|
| `EnemyHealthContainer : HealthContainer` | HP, scaling, kill/XP | `maxHealth`, `shouldScaleWithAmountOfPlayers`, `shouldScaleWithRunProgression`, `isBoss`, `OnXpEarned`; `TakeDamage` `0x285CA40`, `HealthDepleted(bool)` `0x285B630`, `RecordKill` `0x285C0D0`; base `CurrentHealth`, `MaxHealth` |
| `EnemyNavigationController` | movement (navmesh, snapshots to clients) | `EnemyId`, `navigatorPrefab`, `startMovingWhenSpawned`; `Move(Vector3)` `0x2871F60`, `SetNavigatorActive(bool)` `0x2873600`, `SetSpeedMultiplier(float)` `0x28736C0` (9 bytes: likely inlined) |
| `EnemyStagger : IDamageMultiplierProvider` | stagger bar, damage taken × while staggered | `staggeredTime`, `damageMultiplier`, `OnStaggered`, `OnStaggerLost` |
| `DamageMultiplier` | per-enemy damage multiplier | `Set(float)` `0x2712B60` |
| `EnemyNetworkSpawnOnDeath : EnemyDespawner` | spawns N networked prefabs on death (split) | `prefab`, `numberOfSpawns`; `OnHealthDepleted` `0x2896D70` |
| `EnemyLocalSpawnOnDeath : EnemyDespawner` | local (cosmetic) prefab on death | `prefabToInstantiateOnDeath` |
| `EnemyExplodeOnDeath`, `EnemyHealTargetsInRange`, `EnemyProjectileShooter`, `EnemyHideWhenLookedAt` | explode, heal allies, ranged, hide | names only |
| `HealthThresholdSpawner` | spawns at HP thresholds (via `IEnemySpawner`) | `configuration` (`HealthThresholdSpawnConfiguration`), `spawnOrigin`; `TriggerSpawn(int)` `0x26BC700` |

Stats per difficulty/cycle come from `EnemyDifficultyParameters`:
`GetHealthMultiplier(bool)` `0x2857420`, `GetAttackMultiplier()` `0x28571B0` (world × difficulty × cycle).

## Per crab

What code says, and what has to be read live. Roles are **assumed** from name and size until the
live probes fill in HP, speed and contact damage.

### Crabling (0)
- Code: only the enum value and death/target labels.
- Role (assumed): **swarm**: cheap, fast, many; first in `EnemyType`, so likely the base mob.
- Live: HP, speed, `contactDamage`, on-death split (`EnemyNetworkSpawnOnDeath`?), wave weight (`EnemyProbability.Probability`).

### FleshCrab (1)
- Code: enum value; petrified variant `petrifiedFleshCrabPrefab` in `PetrifiedEnemiesObjectiveManager`, count `PetrifiedEnemyConfiguration.PetrifiedFleshCrabAmount`.
- Role (assumed): **bruiser / flanker**: mid HP melee.
- Live: component set, speed relative to Crabling.

### ShieldCrab (5)
- Code: enum value; petrified variant `petrifiedShieldCrabPrefab` / `PetrifiedShieldCrabAmount`. No `Shield*` type in any assembly: the shield is a component combination (candidates: `DamageMultiplier`, `EnemyStagger`, hitbox layout) or animator-driven.
- Role (assumed): **tank / projectile sponge**: blocks frontal fire.
- Live: what blocks (direction? damage multiplier value?), whether area/beam spells bypass it.

### Crablodon (7)
- Code: enum value; petrified variant `petrifiedCrablodonPrefab` / `PetrifiedCrablodonAmount`.
- Role (assumed): **heavy / elite-tier**: the biggest crab.
- Live: HP, `EnemyStagger` presence, whether `EliteEnemyConfiguration` lists it.

### Snipjaw (3)
- Code: enum value only. Crab status unsettled (above).
- Role: unknown; **live**.

### Golden crab (objective, not an enemy type)
- Code: `RunObjectiveDetailsUI.detailsWithGoldenCrabHealthBarPanel`. The only objective with a
  health bar on a single fleeing target is `HuntTargetObjective` → `HuntTarget : NetworkBehaviour`
  (`EnemyHealthContainer`, `HuntTargetNavigator`, `HealthThresholdSpawner`, `deathEffectPrefab`),
  with types `HuntTargetDefault` (12), `HuntTargetBig` (37), `HuntTargetPregnant` (38),
  `HuntTargetBaby` (39). So golden crab ≈ hunt target (**assumed**), and `HuntTargetPregnant`
  spawning `HuntTargetBaby` at HP thresholds fits `HealthThresholdSpawner`.
- `HuntTarget`: `OnHealthDepleted` `0x26C8950`, `EndLifetime()` `0x26C8410`; objective timer `timeLimitInSeconds`.
- Live: confirm the golden model is the HuntTarget prefab.

### Petrified crabs
`PetrifiedEnemiesObjectiveManager` (an objective: `IAmProgressObjective`, `IAmTimedObjective`)
spawns petrified FleshCrab / Crablodon / ShieldCrab statues (`TimeBetweenSpawns = 0.4f`, counts in
`PetrifiedEnemyConfiguration`). Each `PetrifiedEnemy` has a `healthContainer` and a
`regularEnemyPrefab`: breaking one most likely releases the live crab (**assumed**). There is no
petrified Crabling.

## Role table

| role | crab | why (assumed until live) | counter-play it forces |
|---|---|---|---|
| swarm | Crabling | base mob, first enum value | area damage, keep moving |
| bruiser / flank | FleshCrab | mid-tier melee | watch the sides |
| tank | ShieldCrab | the name; a frontal block | angle shots, area/beam spells |
| heavy / elite | Crablodon | largest | burst, kite |
| objective | golden crab (`HuntTarget`) | timed hunt target | chase under pressure |
| unknown | Snipjaw | — | — |

## Boss-capable?

- `BossType` = `Squid`, `Toad`, `Hydra`: **no crab boss**. `BossConfig` (ScriptableObject) points at a
  `BossPrefab` `NetworkObject` + `BossIntroSequencePrefab`; boss behaviour is `BossBehaviourConfig`
  (record: weighted `NormalBossBehaviours` / `CorruptedBossBehaviours`, backup references), phases via
  `BossPhase` / `PhaseManager`. A crab prefab has none of that, so a real crab boss needs a new
  prefab, which is ruled out (no new assets).
- **Elite path instead:** `EliteEnemyConfiguration` (ScriptableObject: weighted `EnemyType` list,
  `SpawnTime`, `EnemySpawnMultiplier`) feeds `EnemySpawner.SpawnEliteEnemy` `0x287C390`. Existing
  elites are `EliteTeethyWorm` / `EliteExplodingTick` / `EliteBuffer`; no elite crab type exists.
  A "boss-like" Crablodon = a Crablodon spawned through the elite path or with `DamageMultiplier` /
  HP scaled by the director, plus `SetOverrideImmuneToDespawning(true)`.
- `EnemyHealthContainer.isBoss` is a serialized flag on the prefab: setting it at runtime on a
  Crablodon may give a boss HP bar (**live**).

## Live probes (for `sbx`)

Values that live in assets, not code:

1. `EnemyPrefabRegistry.GetPrefab(t)` for t ∈ {0,1,3,5,7}: prefab names and component list (settles Snipjaw and ShieldCrab's mechanism).
2. Per crab prefab: `EnemyHealthContainer.maxHealth`, `Enemy.contactDamage`, navigator speed, `EnemyStagger.staggeredTime` / `damageMultiplier`, `EnemyNetworkSpawnOnDeath.prefab` / `numberOfSpawns`.
3. ShieldCrab: hit it from front vs back with one projectile spell and one area spell; log `OnEnemyDamageDealt` amounts.
4. Every `WaveConfiguration` in the current run: its `EnemyProbability` list (Type, Probability), `minNumberOfConcurrentEnemies`, `spawnIntervalInSeconds`, `numberOfEnemiesPerSpawn`: which waves have crabs and how many.
5. `EliteEnemyConfiguration` assets: is any crab in the weighted list?
6. `EnemySpawner.maxNumberOfConcurrentEnemies`, `extraPlayerInfluence`, `eliteEnemySpawnMultiplier`.
7. Hunt objective: prefab model of `HuntTarget` (golden?), `HealthThresholdSpawnConfiguration` entries.
8. `PetrifiedEnemy.regularEnemyPrefab` per statue: does breaking it spawn the live crab?
9. Per world: `WaveConfiguration` world-specific variant replacement (`GetEnemyTypeForSelectedWorld`): do crabs get swapped per world?
