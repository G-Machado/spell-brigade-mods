using System.Collections.Generic;
using Il2Cpp;
using Unity.Netcode;
using UnityEngine;

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>Crab-only spawning by rewriting the spawner's EnemyType → prefab table; wave pacing stays native.</summary>
    internal static class Spawns
    {
        // Swap by role: swarm → Crabling, mid melee → FleshCrab, ranged/spawners → ShieldCrab, elites → Crablodon.
        // Not touched: the crabs, bosses and their minions, enemy events, hunt targets, the cursed artifact.
        private static readonly Dictionary<EnemyType, EnemyType> Role = new Dictionary<EnemyType, EnemyType>
        {
            [EnemyType.Tripod] = EnemyType.Crabling,
            [EnemyType.SneakyBug] = EnemyType.Crabling,
            [EnemyType.ExplodingTick] = EnemyType.Crabling,
            [EnemyType.ExplodingTickFast] = EnemyType.Crabling,
            [EnemyType.Mole] = EnemyType.Crabling,
            [EnemyType.Stinkbug] = EnemyType.Crabling,
            [EnemyType.SpawnerHatchling] = EnemyType.Crabling,
            [EnemyType.TeethyWorm_Baby] = EnemyType.Crabling,
            [EnemyType.TeethyWorm_BabyFast] = EnemyType.Crabling,
            [EnemyType.TeethyWorm] = EnemyType.FleshCrab,
            [EnemyType.TeethyWormFast] = EnemyType.FleshCrab,
            [EnemyType.Buffer] = EnemyType.FleshCrab,
            [EnemyType.BufferFast] = EnemyType.FleshCrab,
            [EnemyType.Hoarder] = EnemyType.FleshCrab,
            [EnemyType.FlyingManta] = EnemyType.FleshCrab,
            [EnemyType.Snipjaw] = EnemyType.FleshCrab,
            [EnemyType.Spitter] = EnemyType.ShieldCrab,
            [EnemyType.SpitterSpread] = EnemyType.ShieldCrab,
            [EnemyType.StaticShooter] = EnemyType.ShieldCrab,
            [EnemyType.Spawner] = EnemyType.ShieldCrab,
            [EnemyType.SpawnerEgg] = EnemyType.ShieldCrab,
            [EnemyType.EliteTeethyWorm] = EnemyType.Crablodon,
            [EnemyType.EliteExplodingTick] = EnemyType.Crablodon,
            [EnemyType.EliteBuffer] = EnemyType.Crablodon,
        };

        public static NetworkObject CrablodonPrefab { get; private set; }

        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
        private static float _lastSummary;

        public static void CreateDictionaryPostfix(EnemySpawner __instance)
        {
            var dict = __instance.enemyTypeToPrefab;
            if (dict == null) { CrabLandMod.Log.Error("spawns: enemyTypeToPrefab is null"); return; }
            if (dict.ContainsKey(EnemyType.Crablodon)) CrablodonPrefab = dict[EnemyType.Crablodon];
            CrabLandMod.Log.Msg($"spawns: table entries={dict.Count} active={CrabLandMod.Active} crablodon={(CrablodonPrefab != null ? CrablodonPrefab.name : "null")}");
            if (!CrabLandMod.Active) return;

            int swapped = 0, missing = 0;
            foreach (var kv in Role)
            {
                if (!dict.ContainsKey(kv.Key)) continue;
                if (!dict.ContainsKey(kv.Value) || dict[kv.Value] == null)
                {
                    missing++;
                    CrabLandMod.Log.Error($"spawns: no prefab for crab {kv.Value}; {kv.Key} left native");
                    continue;
                }
                var from = dict[kv.Key];
                dict[kv.Key] = dict[kv.Value];
                swapped++;
                CrabLandMod.Log.Msg($"spawns: map {kv.Key} {(from != null ? from.name : "null")} -> {dict[kv.Key].name}");
            }
            if (swapped == 0) CrabLandMod.Log.Error($"spawns: swapped 0 entries (missing={missing})");
            else CrabLandMod.Log.Msg($"spawns: swapped {swapped} missing={missing}");
        }

        public static void SpawnEnemyPostfix(NetworkObject __result)
        {
            if (__result == null) return;
            var name = __result.name;
            if (!Counts.TryGetValue(name, out var c)) CrabLandMod.Log.Msg($"spawn new prefab={name} active={CrabLandMod.Active}");
            Counts[name] = c + 1;
            if (Time.realtimeSinceStartup - _lastSummary > 60f) LogCounts("periodic");
        }

        public static void LogCounts(string why)
        {
            _lastSummary = Time.realtimeSinceStartup;
            if (Counts.Count == 0) return;
            var total = 0;
            var parts = new List<string>();
            foreach (var p in Counts) { total += p.Value; parts.Add($"{p.Key}={p.Value}"); }
            CrabLandMod.Log.Msg($"spawns ({why}) total={total} {string.Join(" ", parts)}");
        }

        /// <summary>Every live Enemy in the scene, whatever spawned it (waves, groups, events, objectives), every 10 s.
        /// Runs on host and clients.</summary>
        public static System.Collections.IEnumerator Census(int sceneHandle)
        {
            while (UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle == sceneHandle)
            {
                yield return new WaitForSeconds(10f);
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != sceneHandle) yield break;
                var live = new Dictionary<string, int>();
                foreach (var e in Object.FindObjectsOfType<Enemy>())
                {
                    var key = $"{e.gameObject.name}/{e.EnemyType}";
                    live.TryGetValue(key, out var c); live[key] = c + 1;
                }
                var parts = new List<string>();
                foreach (var p in live) parts.Add($"{p.Key}={p.Value}");
                CrabLandMod.Log.Msg($"census active={CrabLandMod.Active} kinds={live.Count}: {string.Join(" ", parts)}");
            }
        }

        public static void ResetCounts() { Counts.Clear(); _lastSummary = Time.realtimeSinceStartup; }

        public static bool StartEventPrefix(EnemyEvent enemyEvent)
        {
            if (!CrabLandMod.Active || CrabLandMod.KeepEnemyEvents.Value) return true;
            CrabLandMod.Log.Msg($"spawns: skipped enemy event {enemyEvent}");
            return false;
        }

        public static void RunConfigurationPostfix(World world, ref RunConfiguration __result)
        {
            if (__result == null) { CrabLandMod.Log.Error($"spawns: no RunConfiguration for {world}"); return; }
            var waves = __result.Waves;
            CrabLandMod.Log.Msg($"spawns: run config world={world} name={__result.name} duration={__result.RunDurationInSeconds} waves={waves?.Count ?? 0}");
            for (var i = 0; i < (waves?.Count ?? 0); i++)
            {
                var probs = waves[i].EnemyProbabilities;
                var parts = new List<string>();
                for (var j = 0; j < (probs?.Count ?? 0); j++) parts.Add($"{probs[j].Type}:{probs[j].Probability}");
                CrabLandMod.Log.Msg($"spawns: native wave {i} {string.Join(" ", parts)}");
            }

            if (CrabLandMod.Active && CrabLandMod.DebugSkipToBoss.Value)
            {
                var copy = Object.Instantiate(__result);
                copy.RunDurationInSeconds = 45f;
                CrabLandMod.Log.Msg($"debug: run duration {__result.RunDurationInSeconds} -> {copy.RunDurationInSeconds}");
                __result = copy;
            }
        }
    }
}
