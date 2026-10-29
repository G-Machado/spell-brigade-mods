# Director live probes — run 1 (2026-10-29 00:31, solo Crab Land, build 26133722, ML 0.7.3)

Raw values; sources: `<game>/UserData/CrabDirector/20261029-003145.jsonl` (copy in `tools/probes/out/`), `sbx get`.

| probe | value | settles |
|---|---|---|
| SpawnGroupAsync from a mod (host) | spawns; census directorStarted 72 over 93 s; `awaited completed=true after=2.56 s` | callable from a mod; Awaitable needs no pumping |
| `maxNumberOfConcurrentEnemies` | 300 | director cap 2 (game cap) is far off; the 40 % share cap binds first |
| `extraPlayerInfluence` / `eliteEnemySpawnMultiplier` | 0.4 / 1 | — |
| director 5: SpawnRandomEnemy per spawn? | prefix 0 calls vs GetRandomEnemyForWave 28 | inlined: relax throttle must move to the GetRandomEnemyForWave fallback |
| director 6: `ChasingEnemyCount` (player 0) | 47 | counts far more than "nearby"; with ChasingGain 0.003 it alone adds 0.14/s > 0.04/s decay → I pinned at 1, Relax |
| director 8: bus capture | ctor hook refused by Harmony (`NotSupportedException`); bus=0, events=0 this run | fix 33c93a7 (HealthContainer.ServerGameplayEvents), unverified |
| crabs 1: Crabling prefab | `Enemy_Crabling`, `EnemyHealthContainer.maxHealth` 154 | — |
| sbx syntax | `X.Instance` fails; `UnityEngine.Object.FindObjectOfType<Il2Cpp.X>()`, indexers and `GetComponent<T>` work | probe pack needs the rewrite |

Not run (cut for time): other crab prefabs, waves, elites, hunt, petrified, damage-before/after-armor, SpellType classes.
