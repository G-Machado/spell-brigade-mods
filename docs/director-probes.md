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

# Run 2 (2026-10-29 16:26 and 16:43, solo Crab Land, idle player, CrabLand auto-start)

Logs `20261029-162656.jsonl` (defaults, 307 s) and `20261029-164343.jsonl` (test-only `PeakEnter` 0.3, rules.json restored after).
Build: throttle on `SpawnEnemy` inside the coroutine tick, `ChasingCap` 7, probe pack on `FindObjectOfType`.

| probe | value | settles |
|---|---|---|
| hooks | 6/6, `sharers=0` for coroutine `MoveNext`, `SpawnEnemy`, `GetRandomEnemyForWave` | — |
| director 8: bus capture | `bus captured n=1` 3 s after run start; events 2772 in 307 s; 5 `hit` lines | HealthContainer path works (33c93a7). Per run vs per session: not run (no second run in one session, see below) |
| director 5: tick gating | `randomSpawn` = `getRandomEnemyForWave` every second (255 = 255) | the gate sees exactly the coroutine's spawns |
| throttle | 31 skipped of 66 tick spawns over the Relax span, 0 outside Relax | throttle live; rate below RelaxSkipChance 0.7 (span includes a 4 s BuildUp gap) |
| meter, defaults | I 0.39 at 15 s (4 hits of 145.6 on 1200 HP), then decays to 0 and stays; never Peak | chasing no longer pins I; an idle auto-casting player clears waves without damage after 15 s |
| meter, PeakEnter 0.3 | BuildUp → Peak (0.36) → Relax → BuildUp `relax over I=0.00 after=20s` → Peak 0.94 (player down) | full cycle; min I in Relax 0 < RelaxExit |
| director 6: chasing | `chasing` = live enemy count, solo (14–47) | counts every crab: hence `ChasingCap` |
| director 7: damage | `damage` 145.64 = HP drop 914.71 → 769.07 | the event is after mitigation (or the player has no armor) |
| sbx | `FindObjectOfType<Il2Cpp.EnemySpawner>()` 300, `…RunManager>().GetCurrentWave()` 7; `FindObjectOfType<Il2Cpp.GameplayEndManager>` "not found", `sbx invoke GameplayEndManager …` "GameObject not found" | probe pack form works; no way to end a run from sbx yet |

Not run: probe 8 (needs a second Crab Land run in one session; CrabLand's `Active` flag isn't reachable from eval and auto-start fires once per launch), other crab prefabs' HP, waves, elites, hunt, petrified, SpellType classes (seen: Arrow, EnergyZone, projShare 0.50).
Also seen: the deployed CrabLand.dll still hooks `ObjectiveDeterminer.Determine` (NRE in the trampoline): it predates 82689b5.
Objectives: with that stale CrabLand.dll no native objective ever started (Determine NRE at wave 1), so objective waves 3/7/11/15, hunt and petrified could not fire in run 2; re-run them after the redeploy (CrabLand.dll redeployed from 82689b5 after run 2). The meter, throttle, bus and damage rows don't depend on objectives.
