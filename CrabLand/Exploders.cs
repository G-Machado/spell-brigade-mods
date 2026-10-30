using System;
using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>
    /// Exploding crabs: a share of FleshCrabs glow red and, on death, blow up with the ExplodingTick's own
    /// explosion and burst into Crablings. Every machine picks the same crabs from the network id, shows the
    /// glow and plays the explosion (as the native EnemyExplodeOnDeath does); only the host spawns the babies.
    /// </summary>
    internal static class Exploders
    {
        private const float Tick = 0.5f, BabyRadius = 1.5f, Scale = 1.15f;
        private static readonly Color Glow = new Color(1f, 0.25f, 0.05f);

        private static readonly Dictionary<IntPtr, Transform> Live = new();   // EnemyHealthContainer -> enemy root
        private static readonly HashSet<IntPtr> Seen = new();                  // Enemy pointers already judged
        private static int _marked, _exploded, _babies, _run;

        internal static void Reset()
        {
            Live.Clear(); Seen.Clear();
            _marked = _exploded = _babies = 0; _run++;
        }

        private static bool On => CrabLandMod.Active && CrabLandMod.ExploderShare.Value > 0f;

        // Same answer on every machine for the same network id.
        private static bool Picked(ulong netId)
        {
            var h = (uint)(netId * 2654435761UL >> 7);
            return h % 1000u < (uint)Mathf.RoundToInt(Mathf.Clamp01(CrabLandMod.ExploderShare.Value) * 1000f);
        }

        internal static IEnumerator Loop(int sceneHandle)
        {
            var run = _run;
            while (run == _run && UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle == sceneHandle)
            {
                yield return new WaitForSeconds(Tick);
                if (run != _run) yield break;
                if (!On) continue;
                try { Scan(); }
                catch (Exception e) { CrabLandMod.Log.Error($"exploders: scan failed: {e}"); }
            }
            CrabLandMod.Log.Msg($"exploders: loop end marked={_marked} exploded={_exploded} babies={_babies} live={Live.Count}");
        }

        private static void Scan()
        {
            foreach (var e in Object.FindObjectsOfType<Enemy>())
            {
                if (e.EnemyType != EnemyType.FleshCrab || !Seen.Add(e.Pointer)) continue;
                var net = e.GetComponent<NetworkObject>();
                if (net == null || !net.IsSpawned || !Picked(net.NetworkObjectId)) continue;
                var hc = e.GetComponentInChildren<EnemyHealthContainer>(true);
                if (hc == null || hc.CurrentHealth <= 0f) continue;
                Mark(e, hc, net.NetworkObjectId);
            }
        }

        private static void Mark(Enemy e, EnemyHealthContainer hc, ulong netId)
        {
            Live[hc.Pointer] = e.transform;
            e.transform.localScale *= Scale;
            var go = new GameObject("CrabLandExploderGlow");
            go.transform.SetParent(e.transform, false);
            go.transform.localPosition = Vector3.up * 1.2f;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = Glow;
            light.range = 4f;
            light.intensity = 0f;
            MelonCoroutines.Start(Pulse(light, _run));
            _marked++;
            CrabLandMod.Log.Msg($"exploders: marked #{_marked} netId={netId} hp={hc.CurrentHealth} at {e.transform.position}");
        }

        private static IEnumerator Pulse(Light light, int run)
        {
            while (run == _run && light != null && !light.WasCollected)
            {
                light.intensity = 2f + 6f * (0.5f + 0.5f * Mathf.Sin(Time.time * 8f));
                yield return null;
            }
        }

        /// <summary>Hook: EnemyHealthContainer.HealthDepleted postfix (runs on every machine via the clients RPC).</summary>
        public static void HealthDepletedPostfix(EnemyHealthContainer __instance)
        {
            if (!On || __instance == null || !Live.TryGetValue(__instance.Pointer, out var root)) return;
            Live.Remove(__instance.Pointer);
            try
            {
                var pos = root != null ? root.position : __instance.transform.position;
                Explode(pos);
                if (CrabLandMod.IsHostOrSolo()) SpawnBabies(pos);
            }
            catch (Exception e) { CrabLandMod.Log.Error($"exploders: death failed: {e}"); }
        }

        private static void Explode(Vector3 pos)
        {
            _exploded++;
            var src = Spawns.NativePrefab(EnemyType.ExplodingTick)?.GetComponentInChildren<EnemyExplodeOnDeath>(true);
            var prefab = src != null ? src.explosionPrefab : null;
            if (prefab == null) { CrabLandMod.Log.Error($"exploders: no ExplodingTick explosion prefab; #{_exploded} at {pos} has no blast"); return; }
            var go = Object.Instantiate(prefab, pos, Quaternion.identity);
            var ex = go.GetComponent<Explosion>() ?? go.GetComponentInChildren<Explosion>();
            if (ex == null) { CrabLandMod.Log.Error($"exploders: {prefab.name} has no Explosion"); Object.Destroy(go); return; }
            var info = new ExplosionDamageInfo { DamageOnEnemies = 0f, DamageOnPlayers = src.explosionDamageOnPlayers, Damage = src.explosionDamageOnPlayers };
            ex.Explode(info, src.explosionRadius, default);
            CrabLandMod.Log.Msg($"exploders: exploded #{_exploded} at {pos} prefab={prefab.name} radius={src.explosionRadius} " +
                                $"damageOnPlayers={src.explosionDamageOnPlayers} damageOnEnemies(native, unused)={src.explosionDamageOnEnemies}");
        }

        private static void SpawnBabies(Vector3 pos)
        {
            var n = CrabLandMod.ExploderBabies.Value;
            var spawner = Singleton<EnemySpawner>.Instance;
            if (spawner == null || n <= 0) { CrabLandMod.Log.Error($"exploders: babies at {pos} spawned nothing (spawner={spawner != null} n={n})"); return; }
            var anchor = new GameObject("CrabLandExploderBabies");
            anchor.transform.position = pos;
            spawner.SpawnGroupAsync(new EnemyGroupSpawnConfig(EnemyType.Crabling, n, anchor.transform, 0f, BabyRadius,
                0f, 0.2f, EnemySpawnRotation.TowardsClosestPlayer));
            Object.Destroy(anchor, 5f);
            _babies += n;
            CrabLandMod.Log.Msg($"exploders: babies {n} at {pos} total={_babies}");
        }
    }
}
