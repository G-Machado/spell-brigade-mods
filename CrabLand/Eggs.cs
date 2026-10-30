using System;
using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppVContainer.Unity;
using MelonLoader;
using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>
    /// Egg-laying Crablodons, host only. Every live Crablodon lays an egg (the smallest petrified crab statue,
    /// a networked spawn) every EggLayInterval s, capped per layer and overall. An unbroken egg hatches after
    /// EggHatchSeconds into Crablings at its spot; a broken egg releases nothing (the native release of the
    /// statue's regular crab is suppressed for eggs).
    /// </summary>
    internal static class Eggs
    {
        private const float Tick = 0.5f, HatchRadius = 1.5f, LayOffset = 1.5f;

        private sealed class Egg
        {
            public PetrifiedEnemy Statue;
            public NetworkObject Net;
            public IntPtr Ptr, Layer;
            public float HatchAt;
            public Vector3 Pos;
        }

        private static readonly List<Egg> Live = new();
        private static readonly HashSet<IntPtr> Broken = new();
        private static readonly Dictionary<IntPtr, float> NextLay = new();
        private static PetrifiedEnemy _prefab;
        private static int _laid, _hatched, _broken, _lost, _run;

        internal static void Reset()
        {
            Live.Clear(); Broken.Clear(); NextLay.Clear();
            _prefab = null; _laid = _hatched = _broken = _lost = 0; _run++;
        }

        internal static IEnumerator Loop(int sceneHandle)
        {
            var run = _run;
            while (run == _run && UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle == sceneHandle)
            {
                yield return new WaitForSeconds(Tick);
                if (run != _run) yield break;
                if (!CrabLandMod.Active || !CrabLandMod.EggCrabs.Value || !CrabLandMod.IsHostOrSolo()) continue;
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsListening) continue;
                try { Step(); }
                catch (Exception e) { CrabLandMod.Log.Error($"eggs: step failed: {e}"); }
            }
            CrabLandMod.Log.Msg($"eggs: loop end laid={_laid} hatched={_hatched} broken={_broken} lost={_lost} live={Live.Count}");
        }

        private static void Step()
        {
            var now = Time.time;
            for (var i = Live.Count - 1; i >= 0; i--)
            {
                var egg = Live[i];
                if (Broken.Contains(egg.Ptr)) { Live.RemoveAt(i); continue; }
                if (egg.Statue == null || egg.Net == null || !egg.Net.IsSpawned)
                {
                    Live.RemoveAt(i); _lost++;
                    CrabLandMod.Log.Msg($"eggs: egg gone without break or hatch at {egg.Pos} lost={_lost}");
                    continue;
                }
                egg.Pos = egg.Statue.transform.position;
                if (now >= egg.HatchAt) { Live.RemoveAt(i); Hatch(egg); }
            }

            var interval = Mathf.Max(1f, CrabLandMod.EggLayInterval.Value);
            var seen = new HashSet<IntPtr>();
            foreach (var e in Object.FindObjectsOfType<Enemy>())
            {
                if (e.EnemyType != EnemyType.Crablodon) continue;
                var hc = e.GetComponentInChildren<HealthContainer>(true);
                if (hc != null && hc.CurrentHealth <= 0f) continue;
                if (BossFight.IsBoss(hc)) continue;   // the boss never lays
                seen.Add(e.Pointer);
                if (!NextLay.TryGetValue(e.Pointer, out var next)) { NextLay[e.Pointer] = now + interval; continue; }
                if (now < next) continue;
                NextLay[e.Pointer] = now + interval;
                if (Live.Count >= CrabLandMod.EggMaxTotal.Value || CountFor(e.Pointer) >= CrabLandMod.EggsPerLayer.Value) continue;
                Lay(e);
            }
            if (NextLay.Count > seen.Count)
                foreach (var k in new List<IntPtr>(NextLay.Keys)) if (!seen.Contains(k)) NextLay.Remove(k);
        }

        private static int CountFor(IntPtr layer)
        {
            var n = 0;
            foreach (var egg in Live) if (egg.Layer == layer) n++;
            return n;
        }

        private static PetrifiedEnemy Prefab()
        {
            if (_prefab != null) return _prefab;
            PetrifiedEnemy pick = null;
            foreach (var p in Resources.FindObjectsOfTypeAll<PetrifiedEnemy>())
            {
                if (p.gameObject.scene.IsValid()) continue;   // live instance, not a prefab
                var regular = p.regularEnemyPrefab;
                var type = regular != null ? regular.GetComponent<Enemy>()?.EnemyType : null;
                CrabLandMod.Log.Msg($"eggs: statue prefab {p.name} regular={(regular != null ? regular.name : "null")} type={type} " +
                                    $"maxHealth={p.healthContainer?.MaxHealth} scale={p.transform.localScale}");
                if (pick == null || type == EnemyType.FleshCrab) pick = p;
            }
            if (pick == null) CrabLandMod.Log.Error("eggs: no petrified statue prefab loaded; no eggs");
            else CrabLandMod.Log.Msg($"eggs: egg prefab {pick.name}");
            return _prefab = pick;
        }

        private static void Lay(Enemy layer)
        {
            var prefab = Prefab();
            if (prefab == null) return;
            var t = layer.transform;
            var pos = t.position - t.forward * LayOffset;
            var go = Object.Instantiate(prefab.gameObject, pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            var resolver = Singleton<LifetimeScopeAccessor>.Instance?.GetContainer();
            if (resolver != null) ObjectResolverUnityExtensions.InjectGameObject(resolver, go);
            else CrabLandMod.Log.Warning("eggs: no LifetimeScopeAccessor container; egg not injected");
            var net = go.GetComponent<NetworkObject>();
            net.Spawn(true);
            var statue = go.GetComponent<PetrifiedEnemy>();
            Live.Add(new Egg { Statue = statue, Net = net, Ptr = statue.Pointer, Layer = layer.Pointer, Pos = pos,
                               HatchAt = Time.time + Mathf.Max(1f, CrabLandMod.EggHatchSeconds.Value) });
            _laid++;
            CrabLandMod.Log.Msg($"eggs: laid #{_laid} at {pos} live={Live.Count} injected={resolver != null} " +
                                $"hp={statue.healthContainer?.CurrentHealth}/{statue.healthContainer?.MaxHealth} netId={net.NetworkObjectId}");
        }

        private static void Hatch(Egg egg)
        {
            var pos = egg.Statue.transform.position;
            egg.Net.Despawn(true);
            var n = CrabLandMod.EggHatchCrablings.Value;
            var spawner = Singleton<EnemySpawner>.Instance;
            if (spawner == null || n <= 0) { CrabLandMod.Log.Error($"eggs: hatch at {pos} spawned nothing (spawner={spawner != null} n={n})"); return; }
            var anchor = new GameObject("CrabLandEggHatch");
            anchor.transform.position = pos;
            spawner.SpawnGroupAsync(new EnemyGroupSpawnConfig(EnemyType.Crabling, n, anchor.transform, 0f, HatchRadius,
                0f, 0.3f, EnemySpawnRotation.TowardsClosestPlayer));
            Object.Destroy(anchor, 5f);
            _hatched++;
            CrabLandMod.Log.Msg($"eggs: hatched #{_hatched} at {pos} crablings={n} live={Live.Count}");
        }

        private static bool IsEgg(IntPtr ptr)
        {
            foreach (var egg in Live) if (egg.Ptr == ptr) return true;
            return false;
        }

        /// <summary>Despawn a broken egg (once). The native break would release the statue's regular crab.</summary>
        private static void Break(PetrifiedEnemy statue, string via)
        {
            if (!Broken.Add(statue.Pointer)) return;
            _broken++;
            CrabLandMod.Log.Msg($"eggs: broken #{_broken} via {via} at {statue.transform.position} " +
                                $"hp={statue.healthContainer?.CurrentHealth} server={statue.IsServer}");
            var net = statue.GetComponent<NetworkObject>();
            if (net != null && net.IsSpawned) net.Despawn(true);
        }

        /// <summary>Hook: PetrifiedEnemy.OnHealthDepleted prefix. An egg breaks into nothing.</summary>
        public static bool OnHealthDepletedPrefix(PetrifiedEnemy __instance)
        {
            var isEgg = IsEgg(__instance.Pointer);
            CrabLandMod.Log.Msg($"eggs: statue OnHealthDepleted egg={isEgg} server={__instance.IsServer} name={__instance.name}");
            if (!isEgg) return true;
            Break(__instance, "OnHealthDepleted");
            return false;
        }

        /// <summary>Hook: PetrifiedEnemy.ReplaceWithRegularEnemy prefix. Same for the release path itself.</summary>
        public static bool ReplaceWithRegularEnemyPrefix(PetrifiedEnemy __instance)
        {
            var isEgg = IsEgg(__instance.Pointer);
            CrabLandMod.Log.Msg($"eggs: statue ReplaceWithRegularEnemy egg={isEgg} server={__instance.IsServer} name={__instance.name}");
            if (!isEgg) return true;
            Break(__instance, "ReplaceWithRegularEnemy");
            return false;
        }
    }
}
