using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MelonLoader.Utils;
using SpellBrigadeMods.Shared;
using Unity.Netcode;
using UnityEngine;

[assembly: MelonInfo(typeof(SpellBrigadeMods.CrabDirector.CrabDirectorMod), "CrabDirector", "0.1.0", "spell-brigade-mods")]
[assembly: MelonGame(null, "The Spell Brigade")]

namespace SpellBrigadeMods.CrabDirector
{
    /// <summary>
    /// Crab Director (docs/director.md): on the host, reads pressure / output / positioning, runs an
    /// intensity meter with build-up / peak / relax phases, and spawns crab groups by rule through the
    /// game's own EnemySpawner.SpawnGroupAsync. Off by default.
    /// </summary>
    public sealed class CrabDirectorMod : MelonMod
    {
        internal static MelonLogger.Instance Log;
        internal static MelonPreferences_Entry<bool> Enabled, OnlyInCrabLand, LogDecisions;

        private static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "CrabDirector");
        private static IntPtr _run;
        private static string _idleReason;
        private static PropertyInfo _crabLandActive;
        private static FieldInfo _crabLandBoss;
        private static bool _crabLandLooked;
        private static readonly HashSet<string> Reported = new HashSet<string>();

        /// <summary>The director is live this frame: enabled, host, in a run, and in Crab Land unless that's off.</summary>
        internal static bool Running { get; private set; }

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            var cat = MelonPreferences.CreateCategory("CrabDirector");
            Enabled = cat.CreateEntry("Enabled", false, description: "Run the Crab Director (host only)");
            OnlyInCrabLand = cat.CreateEntry("OnlyInCrabLand", true, description: "Only direct while CrabLand's Crab Land is the selected map");
            LogDecisions = cat.CreateEntry("LogDecisions", true, description: "Echo phase changes and spawns to the MelonLoader log (the jsonl log is always written)");
            Log.Msg($"prefs Enabled={Enabled.Value} OnlyInCrabLand={OnlyInCrabLand.Value} LogDecisions={LogDecisions.Value}");

            new Hooks(HarmonyInstance, LoggerInstance)
                .Add("ServerGameplayEventBus..ctor", () => AccessTools.Constructor(typeof(ServerGameplayEventBus), Type.EmptyTypes),
                    postfix: P(typeof(Inputs), nameof(Inputs.BusCtorPostfix)))
                .Add<Enemy>("Start", postfix: P(typeof(Census), nameof(Census.StartPostfix)))
                .Add<Enemy>("OnDestroy", prefix: P(typeof(Census), nameof(Census.OnDestroyPrefix)))
                .Add<RunManager>("OnNetworkSpawn", postfix: P(typeof(CrabDirectorMod), nameof(RunSpawnedPostfix)))
                .Add<EnemySpawner>("SpawnRandomEnemy", new[] { typeof(WaveConfiguration), typeof(Vector3) },
                    prefix: P(typeof(Director), nameof(Director.SpawnRandomEnemyPrefix)))
                .Add<EnemySpawner>("GetRandomEnemyForWave", new[] { typeof(WaveConfiguration) },
                    postfix: P(typeof(Director), nameof(Director.GetRandomEnemyForWavePostfix)))
                .Install();
        }

        private static MethodInfo P(Type t, string name) => t.GetMethod(name);

        public static void RunSpawnedPostfix(RunManager __instance) =>
            Log.Msg($"RunManager.OnNetworkSpawn host={IsHostOrSolo()} enabled={Enabled.Value}");

        public override void OnUpdate()
        {
            try
            {
                var reason = IdleReason();
                Running = reason == null;
                if (reason != _idleReason)
                {
                    _idleReason = reason;
                    Log.Msg(reason == null ? "director: running" : $"director: idle ({reason})");
                }
                if (!Running) return;

                var rm = RunManager.Instance;
                if (rm.Pointer != _run)
                {
                    _run = rm.Pointer;
                    StartRun();
                }
                Director.Tick();
            }
            catch (Exception e) { Once("tick", e); }
        }

        private static string IdleReason()
        {
            if (!Enabled.Value) return "Enabled off";
            if (!IsHostOrSolo()) return "not host";
            if (RunManager.Instance == null || EnemySpawner.Instance == null || GameplayPlayerManager.Instance == null) return "no run";
            if (OnlyInCrabLand.Value && !CrabLandActive()) return "not Crab Land (OnlyInCrabLand)";
            return null;
        }

        private static void StartRun()
        {
            var tuning = Tuning.Load(Path.Combine(Dir, "rules.json"), m => Log.Msg(m));
            JsonLog.Open(Dir);
            Director.BeginRun(tuning);
            JsonLog.Write(new Dictionary<string, object>
            {
                ["ev"] = "run", ["t"] = Time.time, ["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                ["crabLand"] = CrabLandActive(), ["bus"] = Inputs.BusCaptures,
            });
            Log.Msg($"run start: log {JsonLog.Path} bus={Inputs.BusCaptures} crabLand={CrabLandActive()}");
        }

        public override void OnApplicationQuit() => JsonLog.Close();

        internal static bool IsHostOrSolo()
        {
            var nm = NetworkManager.Singleton;
            return nm == null || !nm.IsListening || nm.IsServer;
        }

        /// <summary>CrabLand's internal <c>CrabLandMod.Active</c> flag, read by reflection; false when CrabLand isn't loaded.</summary>
        internal static bool CrabLandActive()
        {
            if (!_crabLandLooked)
            {
                _crabLandLooked = true;
                var mod = MelonMod.RegisteredMelons.FirstOrDefault(m => m.Info.Name == "CrabLand");
                _crabLandActive = mod?.GetType().GetProperty("Active", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                // BossFight._bossHealth: the boss's HealthContainer pointer, non-zero from spawn to kill.
                _crabLandBoss = mod?.GetType().Assembly.GetType("SpellBrigadeMods.CrabLand.BossFight")
                    ?.GetField("_bossHealth", BindingFlags.Static | BindingFlags.NonPublic);
                Log.Msg($"crabland: mod={(mod != null)} flag={(_crabLandActive != null)} bossField={(_crabLandBoss != null)}");
                if (mod != null && (_crabLandActive == null || _crabLandBoss == null))
                    Log.Error("crabland: loaded but its Active / BossFight._bossHealth members are missing; boss back-off is off");
            }
            try { return _crabLandActive != null && (bool)_crabLandActive.GetValue(null); }
            catch { return false; }
        }

        /// <summary>CrabLand's boss fight is running on this machine; false when CrabLand isn't loaded.</summary>
        internal static bool CrabLandBossFight()
        {
            if (!_crabLandLooked) CrabLandActive();
            try { return _crabLandBoss != null && (IntPtr)_crabLandBoss.GetValue(null) != IntPtr.Zero; }
            catch { return false; }
        }

        internal static void Decision(string msg)
        {
            if (LogDecisions.Value) Log.Msg(msg);
        }

        /// <summary>Log an exception once per site, so a per-frame failure doesn't flood the log.</summary>
        internal static void Once(string site, Exception e)
        {
            if (Reported.Add(site + e.GetType().Name)) Log.Error($"{site}: {e}");
        }

        /// <summary>Spike helper for sbx: one group near player 0, host only, ignores Enabled and the caps.</summary>
        public static string DebugSpawn(int type, int count)
        {
            if (!IsHostOrSolo()) return "not host";
            var pm = GameplayPlayerManager.Instance;
            if (pm == null || pm.AlivePlayers == null || pm.AlivePlayers.Count == 0) return "no player";
            if (JsonLog.Path == null) JsonLog.Open(Dir);
            var p = pm.AlivePlayers[0];
            Inputs.Remember(p);
            var ok = Director.DebugSpawn((Crab)type, count, p.OwnerClientId);
            return $"spawn={ok} census={Census.Total} log={JsonLog.Path}";
        }
    }
}
