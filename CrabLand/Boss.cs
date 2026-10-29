using System.Collections.Generic;
using Il2Cpp;
using Unity.Netcode;
using UnityEngine;

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>
    /// The native boss sequence spawns a Crablodon instead: for the duration of SpawnBoss the boss config's
    /// prefab points at the Crablodon prefab, whose health fields carry the native boss's values. Both are
    /// restored right after, so regular Crablodons stay normal. Scale is applied on every machine by the
    /// ClientsAndHost spawn RPC.
    /// </summary>
    internal static class Boss
    {
        private static BossConfig _config;
        private static NetworkObject _nativePrefab;
        private static EnemyHealthContainer _crabHealth;
        private static float _maxHealth;
        private static bool _isBoss, _scalePlayers, _scaleRun, _ignoreDifficulty;
        private static readonly HashSet<ulong> Scaled = new HashSet<ulong>();

        public static void SpawnBossPrefix(EndBossObjective __instance)
        {
            if (!CrabLandMod.Active || !__instance.IsServer) return;
            var cfg = __instance.bossConfigToSpawn;
            var crab = Spawns.CrablodonPrefab;
            if (cfg == null || cfg.BossPrefab == null || crab == null)
            {
                CrabLandMod.Log.Error($"boss: cannot swap config={cfg != null} prefab={cfg?.BossPrefab != null} crablodon={crab != null}; native boss spawns");
                return;
            }
            var native = cfg.BossPrefab.GetComponentInChildren<EnemyHealthContainer>(true);
            var health = crab.GetComponentInChildren<EnemyHealthContainer>(true);
            if (native == null || health == null)
            {
                CrabLandMod.Log.Error($"boss: health container native={native != null} crablodon={health != null}; native boss spawns");
                return;
            }

            _config = cfg; _nativePrefab = cfg.BossPrefab; _crabHealth = health;
            _maxHealth = health.maxHealth; _isBoss = health.isBoss; _scalePlayers = health.shouldScaleWithAmountOfPlayers;
            _scaleRun = health.shouldScaleWithRunProgression; _ignoreDifficulty = health.shouldIgnoreEnemyDifficultyParameters;

            var mult = CrabLandMod.BossHealthMultiplier.Value * CrabLandMod.DebugBossHealthMultiplier.Value;
            health.maxHealth = native.maxHealth * mult;
            health.isBoss = true;
            health.shouldScaleWithAmountOfPlayers = native.shouldScaleWithAmountOfPlayers;
            health.shouldScaleWithRunProgression = native.shouldScaleWithRunProgression;
            health.shouldIgnoreEnemyDifficultyParameters = native.shouldIgnoreEnemyDifficultyParameters;
            cfg.BossPrefab = crab;
            CrabLandMod.Log.Msg($"boss: type={cfg.BossType} native={_nativePrefab.name} nativeMaxHealth={native.maxHealth} " +
                                $"-> {crab.name} maxHealth {_maxHealth} -> {health.maxHealth} (BossHealthMultiplier={CrabLandMod.BossHealthMultiplier.Value} " +
                                $"DebugBossHealthMultiplier={CrabLandMod.DebugBossHealthMultiplier.Value})");
        }

        public static void SpawnBossPostfix() => RestoreIfSwapped();

        public static void RestoreIfSwapped()
        {
            if (_config == null) return;
            _config.BossPrefab = _nativePrefab;
            _crabHealth.maxHealth = _maxHealth; _crabHealth.isBoss = _isBoss;
            _crabHealth.shouldScaleWithAmountOfPlayers = _scalePlayers; _crabHealth.shouldScaleWithRunProgression = _scaleRun;
            _crabHealth.shouldIgnoreEnemyDifficultyParameters = _ignoreDifficulty;
            _config = null;
            CrabLandMod.Log.Msg("boss: native config and Crablodon prefab restored");
        }

        public static void EnemySpawnedRpcPostfix(NetworkObjectReference bossReference)
        {
            if (!bossReference.TryGet(out var boss, null) || boss == null)
            {
                CrabLandMod.Log.Warning("boss: spawn RPC reference did not resolve");
                return;
            }
            var hc = boss.GetComponentInChildren<HealthContainer>(true);
            CrabLandMod.Log.Msg($"boss: spawned prefab={boss.name} id={boss.NetworkObjectId} active={CrabLandMod.Active} " +
                                $"maxHealth={(hc != null ? hc.MaxHealth : -1f)} scale={boss.transform.localScale}");
            Spawns.LogCounts("boss spawn");
            if (CrabLandMod.Active) BossFight.OnBossSpawned(boss.gameObject);
            if (!CrabLandMod.Active || !Scaled.Add(boss.NetworkObjectId)) return;
            var before = boss.transform.localScale;
            boss.transform.localScale = before * CrabLandMod.CurrentBossScale;
            var ehc = boss.GetComponentInChildren<EnemyHealthContainer>(true);
            if (ehc != null) ehc.isBoss = true;
            CrabLandMod.Log.Msg($"boss: scale {before} -> {boss.transform.localScale}");
        }

        public static void BossKilledPostfix()
        {
            CrabLandMod.Log.Msg($"boss: defeated (RunManager.HandleBossKilled) active={CrabLandMod.Active}");
            Scaled.Clear();
            BossFight.OnBossKilled();
        }

        public static void RunSuccessfulPostfix() =>
            CrabLandMod.Log.Msg($"run end: FlagRunAsSuccessful active={CrabLandMod.Active}");
    }
}
