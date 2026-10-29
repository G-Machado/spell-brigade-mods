#!/usr/bin/env bash
# Live probes for the Crab Director (docs/director.md + docs/crabs.md, "Live probes").
# Prints raw values only; classification happens in docs/director-probes.md.
# Usage: tools/probes/director.sh [probe ...]   (default: every sbx probe; `list` names them)
# Needs: the game running with the bridge (MelonMCP), on the host, inside a run.
# Env: SBX (path to tools/sbx), GAME_DIR, OUT (default tools/probes/out/director-<ts>.txt, fresh per run).
set -uo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
SBX="${SBX:-/c/Users/gabri/source/repos/spell-brigade-mods-sbx-bridge/tools/sbx}"
game="${GAME_DIR:-/c/Program Files (x86)/Steam/steamapps/common/The Spell Brigade}"
out="${OUT:-$root/tools/probes/out/director-$(date +%Y%m%d-%H%M%S).txt}"
mkdir -p "$(dirname "$out")"; : > "$out"

ok=0; fail=0
say() { printf '%s\n' "$*" | tee -a "$out"; }
# get <label> <expr>: one expression, raw output and exit code.
get() {
  local label="$1" expr="$2" res rc
  res="$(node "$SBX" get "$expr" 2>&1)"; rc=$?
  say "  [$label] rc=$rc :: $expr"
  printf '%s\n' "$res" | sed 's/^/    /' | tee -a "$out"
  if [ $rc -eq 0 ]; then ok=$((ok+1)); else fail=$((fail+1)); fi
}
ev() {
  local label="$1" code="$2" res rc
  res="$(node "$SBX" eval "$code" 2>&1)"; rc=$?
  say "  [$label] rc=$rc :: eval"
  printf '%s\n' "$res" | sed 's/^/    /' | tee -a "$out"
  if [ $rc -eq 0 ]; then ok=$((ok+1)); else fail=$((fail+1)); fi
}
count_enemies() { node "$SBX" find Il2Cpp.Enemy 2000 2>&1 | grep -c .; }

ES='Il2Cpp.EnemySpawner.Instance'
RM='Il2Cpp.RunManager.Instance'
PM='Il2Cpp.GameplayPlayerManager.Instance'
CRABS="Crabling FleshCrab Snipjaw ShieldCrab Crablodon"
DIRLOG="$game/UserData/CrabDirector"

# --- director.md ---------------------------------------------------------------------------------

p_context() {   # run context the rules scale by
  say "## context: host, run, wave"
  get host "Unity.Netcode.NetworkManager.Singleton.IsServer"
  get players "$PM.AlivePlayers.Count"
  get wave "$RM.GetCurrentWave()"
  get cycle "$RM.GetCurrentCycle()"
  get progress "$RM.GetCurrentProgress()"
  get waveMult "$RM.GetWaveDifficultyMultiplier()"
  get scene "UnityEngine.SceneManagement.SceneManager.GetActiveScene().name"
}

p_caps() {      # director 2, crabs 6
  say "## caps: spawner limits (director 2, crabs 6)"
  get maxConcurrent "$ES.maxNumberOfConcurrentEnemies"
  get extraPlayerInfluence "$ES.extraPlayerInfluence"
  get eliteMult "$ES.eliteEnemySpawnMultiplier"
  get currentWave.minConcurrent "$ES.currentWave.minNumberOfConcurrentEnemies"
  get currentWave.interval "$ES.currentWave.spawnIntervalInSeconds"
  get currentWave.perSpawn "$ES.currentWave.numberOfEnemiesPerSpawn"
}

p_spawncap() {  # director 1: does SpawnGroupAsync honour the cap? Spawns 60 Crablings.
  say "## spawncap: SpawnGroupAsync vs maxNumberOfConcurrentEnemies (director 1)"
  get maxConcurrent "$ES.maxNumberOfConcurrentEnemies"
  say "  enemies before: $(count_enemies)"
  get spawn60 "$ES.SpawnGroupAsync(new Il2Cpp.EnemyGroupSpawnConfig(Il2Cpp.EnemyType.Crabling, 60, $PM.AlivePlayers[0].GetTransform(), 8f, 12f, 0f, 1f, Il2Cpp.EnemySpawnRotation.TowardsClosestPlayer))"
  sleep 3
  say "  enemies after 3 s: $(count_enemies)"
}

p_chasing() {   # director 6: ChasingEnemyCount per player, compare with live enemy count
  say "## chasing: ChasingEnemyCount (director 6)"
  get p0.chasing "$PM.AlivePlayers[0].ChasingEnemyCount"
  get p1.chasing "$PM.AlivePlayers[1].ChasingEnemyCount"
  say "  enemies alive: $(count_enemies)"
}

p_bus() {       # director 8: bus constructions per run, read from the mod's log
  say "## bus: ServerGameplayEventBus constructions (director 8)"
  grep -h '"ev":"bus"' "$DIRLOG"/*.jsonl 2>/dev/null | tail -n 20 | sed 's/^/    /' | tee -a "$out" \
    || say "    no bus lines in $DIRLOG (CrabDirector not deployed or never ran)"
  grep -h 'bus captured' "$game/MelonLoader/Latest.log" 2>/dev/null | sed 's/^/    /' | tee -a "$out"
}

p_tick() {      # director 5: does SpawnRandomEnemy fire per spawn? Mod counts both hooks.
  say "## tick: SpawnRandomEnemy vs GetRandomEnemyForWave calls (director 5)"
  grep -h '"ev":"sec"' "$DIRLOG"/*.jsonl 2>/dev/null | tail -n 5 | grep -o '"hookCalls":{[^}]*}' | sed 's/^/    /' | tee -a "$out"
}

p_damage() {    # director 7: OnDamageTaken.Damage vs HP drop, from the mod's per-second lines
  say "## damage: OnDamageTaken.Damage vs HP delta (director 7)"
  grep -h '"ev":"hit"' "$DIRLOG"/*.jsonl 2>/dev/null | tail -n 20 | sed 's/^/    /' | tee -a "$out"
}

p_spells() {    # director 4: SpellType seen per player, from the mod's log
  say "## spells: OnSpellDamageDone by SpellType (director 4; class by name stays assumed)"
  grep -h '"ev":"sec"' "$DIRLOG"/*.jsonl 2>/dev/null | tail -n 1 | grep -o '"spell":{[^}]*}' | sed 's/^/    /' | tee -a "$out"
}

# --- crabs.md -----------------------------------------------------------------------------------

p_prefabs() {   # crabs 1-2: prefab name + stats per crab type
  say "## prefabs: per crab type (crabs 1, 2)"
  for t in $CRABS; do
    local pf="$ES.enemyTypeToPrefab[Il2Cpp.EnemyType.$t]"
    get "$t.name" "$pf.name"
    get "$t.enemyType" "$pf.GetComponent<Il2Cpp.Enemy>().EnemyType"
    get "$t.contactDamage" "$pf.GetComponent<Il2Cpp.Enemy>().contactDamage"
    get "$t.maxHealth" "$pf.GetComponent<Il2Cpp.EnemyHealthContainer>().maxHealth"
    get "$t.isBoss" "$pf.GetComponent<Il2Cpp.EnemyHealthContainer>().isBoss"
    get "$t.scalePlayers" "$pf.GetComponent<Il2Cpp.EnemyHealthContainer>().shouldScaleWithAmountOfPlayers"
    get "$t.staggeredTime" "$pf.GetComponent<Il2Cpp.EnemyStagger>().staggeredTime"
    get "$t.staggerMult" "$pf.GetComponent<Il2Cpp.EnemyStagger>().damageMultiplier"
    get "$t.splitPrefab" "$pf.GetComponent<Il2Cpp.EnemyNetworkSpawnOnDeath>().prefab.name"
    get "$t.splitCount" "$pf.GetComponent<Il2Cpp.EnemyNetworkSpawnOnDeath>().numberOfSpawns"
    get "$t.components" "$pf.GetComponents<UnityEngine.Component>().Length"
  done
}

p_live() {      # crabs 2: live instances (scaled HP), one per type if present
  say "## live: scaled HP of live crabs (crabs 2)"
  node "$SBX" find Il2Cpp.EnemyHealthContainer 50 2>&1 | sed 's/^/    /' | tee -a "$out"
}

p_waves() {     # crabs 4: wave configurations of the current run
  say "## waves: RunConfiguration.Waves (crabs 4)"
  get waves.count "$RM.Configuration.Waves.Count"
  for i in 0 1 2 3 4 5 6 7; do
    get "wave$i.probs" "$RM.Configuration.Waves[$i].EnemyProbabilities.Count"
    get "wave$i.minConcurrent" "$RM.Configuration.Waves[$i].minNumberOfConcurrentEnemies"
    get "wave$i.interval" "$RM.Configuration.Waves[$i].spawnIntervalInSeconds"
    get "wave$i.perSpawn" "$RM.Configuration.Waves[$i].numberOfEnemiesPerSpawn"
    for j in 0 1 2 3 4 5; do
      get "wave$i.p$j" "$RM.Configuration.Waves[$i].EnemyProbabilities[$j].Type + \"=\" + $RM.Configuration.Waves[$i].EnemyProbabilities[$j].Probability"
    done
  done
}

p_elites() {    # crabs 5
  say "## elites: EliteEnemyConfiguration assets (crabs 5)"
  node "$SBX" find Il2Cpp.EliteEnemyConfiguration 20 2>&1 | sed 's/^/    /' | tee -a "$out"
}

p_hunt() {      # crabs 7
  say "## hunt: HuntTarget objects (crabs 7, only during a hunt objective)"
  node "$SBX" find Il2Cpp.HuntTarget 5 2>&1 | sed 's/^/    /' | tee -a "$out"
}

p_petrified() { # crabs 8
  say "## petrified: PetrifiedEnemy objects (crabs 8, only during that objective)"
  node "$SBX" find Il2Cpp.PetrifiedEnemy 10 2>&1 | sed 's/^/    /' | tee -a "$out"
}

p_manual() {    # needs play; listed so nothing is forgotten
  say "## manual (needs play, read from the CrabDirector log afterwards)"
  say "  crabs 3: hit a ShieldCrab front vs back with one projectile and one area spell; compare \"hit\" Damage"
  say "  crabs 9: per-world swaps (GetEnemyTypeForSelectedWorld): run p_waves on two worlds"
}

ALL="context caps chasing prefabs live waves elites hunt petrified bus tick damage spells manual"
[ "${1:-}" = list ] && { echo "$ALL spawncap"; exit 0; }
[ $# -gt 0 ] || set -- $ALL

node "$SBX" ping >/dev/null 2>&1 || { echo "director.sh: bridge not reachable ($SBX ping)" >&2; exit 3; }
say "# director probes $(date -Iseconds)  sbx=$SBX"
for p in "$@"; do
  declare -F "p_$p" >/dev/null || { echo "director.sh: unknown probe $p (try: list)" >&2; exit 2; }
  "p_$p"
done
say "# sbx calls ok=$ok failed=$fail -> $out"
[ $ok -gt 0 ] || { echo "director.sh: every sbx call failed, nothing collected" >&2; exit 1; }
