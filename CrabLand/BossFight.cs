using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>
    /// Crab boss mechanics, host-authoritative. Brood: at each HP threshold one ring of Crablings + FleshCrabs.
    /// Shell: at the shell threshold a ring of ShieldCrabs spawns and the boss takes reduced damage until they
    /// are all dead. The shell flag reaches clients in CrabLand.State; damage is cut in TryToDamage wherever it
    /// runs (the log says server= so the live run shows which machine applies it).
    /// </summary>
    internal static class BossFight
    {
        private const float Tick = 0.25f, SpawnDuration = 0.5f;

        /// <summary>Boss health container on this machine (set from the spawn RPC on host and clients).</summary>
        private static IntPtr _bossHealth;
        private static int _fight;   // bumps per boss so a stale coroutine stops

        internal static bool ShellOn { get; private set; }
        internal static int ShieldsLeft { get; private set; }
        private const int HitsLogged = 10;
        private static int _hitsLogged;

        internal static void Reset()
        {
            _bossHealth = IntPtr.Zero; _fight++;
            SetShell(false, 0, "reset");
        }

        /// <summary>Every machine: remember the boss. Host: start the fight loop.</summary>
        internal static void OnBossSpawned(GameObject boss)
        {
            var hc = boss.GetComponentInChildren<HealthContainer>(true);
            if (hc == null) { CrabLandMod.Log.Error("fight: boss has no HealthContainer; no mechanics"); return; }
            _bossHealth = hc.Pointer;
            if (!CrabLandMod.IsHostOrSolo()) return;
            if (!CrabLandMod.BossMechanics.Value) { CrabLandMod.Log.Msg("fight: BossMechanics=false, no brood or shell"); return; }
            MelonCoroutines.Start(Loop(hc, boss.transform, ++_fight));
        }

        internal static void OnBossKilled()
        {
            _fight++;
            if (ShellOn) SetShell(false, 0, "boss dead");
            _bossHealth = IntPtr.Zero;
        }

        private static IEnumerator Loop(HealthContainer hc, Transform boss, int fight)
        {
            var brood = ParseThresholds(CrabLandMod.BroodThresholds.Value);
            var broodDone = new bool[brood.Count];
            var shellDone = false;
            List<HealthContainer> ring = null;
            var dead = new HashSet<IntPtr>();
            var shellStart = 0f;
            CrabLandMod.Log.Msg($"fight: start brood=[{CrabLandMod.BroodThresholds.Value}] crablings={CrabLandMod.BroodCrablings.Value} " +
                                $"fleshCrabs={CrabLandMod.BroodFleshCrabs.Value} shellAt={CrabLandMod.ShellThreshold.Value} " +
                                $"shieldCrabs={CrabLandMod.ShellShieldCrabs.Value} radius={CrabLandMod.RingRadius.Value} " +
                                $"shellDamage={CrabLandMod.ShellDamageMultiplier.Value} maxHealth={hc.SyncedMaxHealth.Value}");

            while (fight == _fight)
            {
                yield return new WaitForSeconds(Tick);
                if (fight != _fight) yield break;
                float cur, max;
                try
                {
                    if (hc == null || hc.WasCollected || hc.IsHealthZero()) { CrabLandMod.Log.Msg("fight: boss gone, loop ends"); yield break; }
                    cur = hc.SyncedCurrentHealth.Value; max = hc.SyncedMaxHealth.Value;
                }
                catch (Exception e) { CrabLandMod.Log.Error($"fight: health read failed, loop ends: {e.Message}"); yield break; }
                if (max <= 0f) continue;
                var frac = cur / max;

                for (int i = 0; i < brood.Count; i++)
                {
                    if (broodDone[i] || frac > brood[i]) continue;
                    broodDone[i] = true;
                    var c = Spawn(EnemyType.Crabling, CrabLandMod.BroodCrablings.Value, boss);
                    var f = Spawn(EnemyType.FleshCrab, CrabLandMod.BroodFleshCrabs.Value, boss);
                    CrabLandMod.Log.Msg($"fight: brood hp={cur}/{max} frac={frac:0.000} threshold={brood[i]} crablings={c} fleshCrabs={f}");
                }

                if (!shellDone && frac <= CrabLandMod.ShellThreshold.Value)
                {
                    shellDone = true;
                    var before = Snapshot(EnemyName(EnemyType.ShieldCrab));
                    var n = Spawn(EnemyType.ShieldCrab, CrabLandMod.ShellShieldCrabs.Value, boss);
                    CrabLandMod.Log.Msg($"fight: shell hp={cur}/{max} frac={frac:0.000} threshold={CrabLandMod.ShellThreshold.Value} shieldCrabsRequested={n} existingShieldCrabs={before.Count}");
                    yield return new WaitForSeconds(SpawnDuration + 0.5f);
                    if (fight != _fight) yield break;
                    ring = CollectRing(before, boss.position, CrabLandMod.RingRadius.Value + 3f);
                    if (ring.Count == 0)
                    {
                        CrabLandMod.Log.Error("fight: shell ring found 0 ShieldCrabs near the boss; shell skipped");
                        ring = null;
                        continue;
                    }
                    shellStart = Time.time;
                    SetShell(true, ring.Count, $"ring of {ring.Count}");
                }

                if (ring != null && ShellOn && Time.time - shellStart > CrabLandMod.ShellMaxSeconds.Value)
                {
                    ring = null;
                    SetShell(false, 0, $"timeout {CrabLandMod.ShellMaxSeconds.Value}s hp={cur}/{max}");
                }

                if (ring != null && ShellOn)
                {
                    int alive = 0;
                    foreach (var s in ring)
                    {
                        if (dead.Contains(s.Pointer)) continue;
                        bool isDead;
                        try { isDead = s == null || s.WasCollected || !s.gameObject.activeInHierarchy || s.IsHealthZero(); }
                        catch { isDead = true; }
                        if (isDead) dead.Add(s.Pointer); else alive++;
                    }
                    if (alive != ShieldsLeft)
                        SetShell(alive > 0, alive, alive > 0 ? $"shieldCrab died hp={cur}/{max}" : $"cracked hp={cur}/{max}");
                }
            }
        }

        private static int Spawn(EnemyType type, int count, Transform origin)
        {
            if (count <= 0) return 0;
            var spawner = Singleton<EnemySpawner>.Instance;
            if (spawner == null) { CrabLandMod.Log.Error($"fight: no EnemySpawner; {type} x{count} not spawned"); return 0; }
            var r = CrabLandMod.RingRadius.Value;
            try
            {
                spawner.SpawnGroupAsync(new EnemyGroupSpawnConfig(type, count, origin, Mathf.Max(0f, r - 1f), r + 1f,
                    0f, SpawnDuration, EnemySpawnRotation.TowardsClosestPlayer));
                return count;
            }
            catch (Exception e) { CrabLandMod.Log.Error($"fight: SpawnGroupAsync {type} x{count} failed: {e.Message}"); return 0; }
        }

        private static string EnemyName(EnemyType t) => "Enemy_" + t;

        private static HashSet<IntPtr> Snapshot(string prefix)
        {
            var set = new HashSet<IntPtr>();
            foreach (var hc in UnityEngine.Object.FindObjectsOfType<EnemyHealthContainer>())
                if (hc.gameObject.name.StartsWith(prefix, StringComparison.Ordinal) && !hc.IsHealthZero()) set.Add(hc.Pointer);
            return set;
        }

        private static List<HealthContainer> CollectRing(HashSet<IntPtr> before, Vector3 center, float maxDistance)
        {
            var ring = new List<HealthContainer>();
            var prefix = EnemyName(EnemyType.ShieldCrab);
            foreach (var hc in UnityEngine.Object.FindObjectsOfType<EnemyHealthContainer>())
            {
                if (!hc.gameObject.name.StartsWith(prefix, StringComparison.Ordinal) || before.Contains(hc.Pointer) || hc.IsHealthZero()) continue;
                if (Vector3.Distance(hc.transform.position, center) > maxDistance) continue;
                ring.Add(hc);
            }
            return ring;
        }

        private static void SetShell(bool on, int left, string why)
        {
            if (ShellOn == on && ShieldsLeft == left) return;
            ShellOn = on; ShieldsLeft = left; _hitsLogged = 0;
            CrabLandMod.Log.Msg($"fight: shell={(on ? "on" : "off")} shieldCrabsLeft={left} ({why})");
            if (CrabLandMod.IsHostOrSolo()) Net.BroadcastState();
        }

        /// <summary>Client: shell state from the host's CrabLand.State.</summary>
        internal static void ApplyHostShell(bool on, int left)
        {
            if (ShellOn == on && ShieldsLeft == left) return;
            ShellOn = on; ShieldsLeft = left; _hitsLogged = 0;
            CrabLandMod.Log.Msg($"fight: host shell={(on ? "on" : "off")} shieldCrabsLeft={left}");
        }

        /// <summary>TryToDamage prefix helper: cut damage to the boss while the shell is up.</summary>
        internal static void ScaleDamage(HealthContainer hc, ref float amount)
        {
            if (!ShellOn || _bossHealth == IntPtr.Zero || hc.Pointer != _bossHealth) return;
            var before = amount;
            amount *= CrabLandMod.ShellDamageMultiplier.Value;
            // Raw in/out per machine: a client hit cut on the client and again on the host would show as host in ≈ client out.
            if (_hitsLogged >= HitsLogged) return;
            _hitsLogged++;
            CrabLandMod.Log.Msg($"fight: shell hit #{_hitsLogged} in={before} out={amount} server={hc.IsServer} owner={hc.IsOwner} " +
                                $"hp={hc.SyncedCurrentHealth.Value}/{hc.SyncedMaxHealth.Value}");
        }

        private static List<float> ParseThresholds(string s)
        {
            var list = new List<float>();
            foreach (var part in (s ?? "").Split(','))
                if (float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0f && v < 1f) list.Add(v);
            if (list.Count == 0) CrabLandMod.Log.Warning($"fight: BroodThresholds '{s}' has no value in (0,1); no brood");
            return list;
        }
    }
}
