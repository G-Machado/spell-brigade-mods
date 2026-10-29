using System;
using System.Collections;
using Il2Cpp;
using MelonLoader;
using SpellBrigadeMods.Shared;
using Unity.Netcode;
using UnityEngine;

[assembly: MelonInfo(typeof(SpellBrigadeMods.CrabLand.CrabLandMod), "CrabLand", SpellBrigadeMods.CrabLand.CrabLandMod.Version, "spell-brigade-mods")]
[assembly: MelonGame(null, "The Spell Brigade")]

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>
    /// Crab Land: the base world (VerdantMeadows) plus a host-owned flag. While the flag is on, every
    /// non-crab enemy type maps to a crab prefab, the boss is a scaled Crablodon, and the scene is reskinned.
    /// The flag reaches mod clients by an NGO named message (<see cref="Net"/>).
    /// </summary>
    public sealed class CrabLandMod : MelonMod
    {
        public const string Version = "0.1.0";
        public const World BaseWorld = World.VerdantMeadows;

        internal static MelonLogger.Instance Log;

        internal static MelonPreferences_Entry<bool> AutoStartCrabLand, DebugGodMode, DebugSkipToBoss, KeepEnemyEvents, GrantInfusionReward;
        internal static MelonPreferences_Entry<float> BossScale, BossHealthMultiplier;

        private static bool _active;

        /// <summary>Boss scale from the host's message on a client; the local pref otherwise.</summary>
        internal static float HostBossScale = -1f;
        internal static float CurrentBossScale => HostBossScale > 0 ? HostBossScale : BossScale.Value;

        /// <summary>Crab Land is the selected map. Host: set by the menu button; client: set by the host's message.</summary>
        internal static bool Active
        {
            get => _active;
            set
            {
                if (_active == value) return;
                _active = value;
                Log.Msg($"active={value}");
                Menu.RefreshLobbyIndicator();
                if (IsHostOrSolo()) Net.BroadcastState();
            }
        }

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            var cat = MelonPreferences.CreateCategory("CrabLand");
            AutoStartCrabLand = cat.CreateEntry("AutoStartCrabLand", false, description: "Debug: pick Crab Land and start the run from the main menu");
            DebugGodMode = cat.CreateEntry("DebugGodMode", false, description: "Debug: players take no damage");
            DebugSkipToBoss = cat.CreateEntry("DebugSkipToBoss", false, description: "Debug: first objective forced at 30 s and boss HP x0.02, so the boss comes early and dies fast");
            KeepEnemyEvents = cat.CreateEntry("KeepEnemyEvents", false, description: "Keep the native enemy events (they spawn non-crab enemies)");
            GrantInfusionReward = cat.CreateEntry("GrantInfusionReward", true, description: "After the first objective, every player picks an infusion for a spell (turn off if the store hangs)");
            BossScale = cat.CreateEntry("BossScale", 3f, description: "Crablodon boss scale");
            BossHealthMultiplier = cat.CreateEntry("BossHealthMultiplier", 1f, description: "Boss HP = native boss HP x this");

            Log.Msg($"prefs AutoStartCrabLand={AutoStartCrabLand.Value} DebugGodMode={DebugGodMode.Value} " +
                    $"DebugSkipToBoss={DebugSkipToBoss.Value} KeepEnemyEvents={KeepEnemyEvents.Value} GrantInfusionReward={GrantInfusionReward.Value} " +
                    $"BossScale={BossScale.Value} BossHealthMultiplier={BossHealthMultiplier.Value}");

            new Hooks(HarmonyInstance, LoggerInstance)
                .Add<WorldsPanel>("InitializeButtons", postfix: P(typeof(Menu), nameof(Menu.InitializeButtonsPostfix)))
                .Add<WorldsPanel>("OnWorldButtonClicked", prefix: P(typeof(Menu), nameof(Menu.OnWorldButtonClickedPrefix)))
                .Add<MainMenuManager>("OnNetworkSpawn", postfix: P(typeof(CrabLandMod), nameof(MainMenuSpawnedPostfix)))
                .Add<EnemySpawner>("CreateEnemyTypeToPrefabDictionary", postfix: P(typeof(Spawns), nameof(Spawns.CreateDictionaryPostfix)))
                .Add<EnemySpawner>("SpawnEnemy", postfix: P(typeof(Spawns), nameof(Spawns.SpawnEnemyPostfix)))
                .Add<EnemyEventStarterController>("StartEvent", prefix: P(typeof(Spawns), nameof(Spawns.StartEventPrefix)))
                .Add<RunManager>("GetRunConfigurationForWorld", postfix: P(typeof(Spawns), nameof(Spawns.RunConfigurationPostfix)))
                .Add<RunManager>("HandleBossKilled", postfix: P(typeof(Boss), nameof(Boss.BossKilledPostfix)))
                .Add<EndBossObjective>("SpawnBoss", prefix: P(typeof(Boss), nameof(Boss.SpawnBossPrefix)), postfix: P(typeof(Boss), nameof(Boss.SpawnBossPostfix)))
                .Add<EndBossObjective>("InvokeEnemySpawnedRpc", postfix: P(typeof(Boss), nameof(Boss.EnemySpawnedRpcPostfix)))
                .Add<EndBossObjective>("FlagRunAsSuccessful", postfix: P(typeof(Boss), nameof(Boss.RunSuccessfulPostfix)))
                .Add<HealthContainer>("TryToDamage", prefix: P(typeof(CrabLandMod), nameof(TryToDamagePrefix)))
                .Add<ObjectiveDirector>("NotifyObjectiveStarted", postfix: P(typeof(Run), nameof(Run.ObjectiveStartedPostfix)))
                .Add<ObjectiveDirector>("NotifyObjectiveEnded", postfix: P(typeof(Run), nameof(Run.ObjectiveEndedPostfix)))
                .Add<ObjectiveDeterminer>("Determine", postfix: P(typeof(Run), nameof(Run.DetermineObjectivePostfix)))
                .Add<StoreManager>("CheckForSpellInfusion", prefix: P(typeof(Run), nameof(Run.CheckForSpellInfusionPrefix)))
                .Add<StoreManager>("OfferSpellInfusion", prefix: P(typeof(Run), nameof(Run.OfferSpellInfusionPrefix)))
                .Add<StoreManager>("ChooseSpellImprovement", prefix: P(typeof(Run), nameof(Run.ChooseSpellImprovementPrefix)))
                .Add<ImprovementsManager>("AddSpellInfusion", prefix: P(typeof(Run), nameof(Run.AddSpellInfusionPrefix)))
                .Add<ImprovementsManager>("SendSpellInfusionRpc", prefix: P(typeof(Run), nameof(Run.SendSpellInfusionRpcPrefix)))
                .Add<SpellImprovementLevelDeterminer>("DetermineSpellLevel", postfix: P(typeof(Run), nameof(Run.DetermineSpellLevelPostfix)))
                .Add<InfusionStoreAvailabilityChecker>("InfusionStoreShouldBeOffered", postfix: P(typeof(Run), nameof(Run.InfusionShouldBeOfferedPostfix)))
                .Install();
        }

        private static System.Reflection.MethodInfo P(Type t, string name) => t.GetMethod(name);

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Log.Msg($"scene loaded name={sceneName} index={buildIndex} active={Active}");
            Boss.RestoreIfSwapped();
            if (sceneName == BaseWorld.ToString())
            {
                Spawns.ResetCounts();
                Run.Reset();
                if (DebugSkipToBoss.Value) MelonCoroutines.Start(Run.DebugForceFirstObjective());
                MelonCoroutines.Start(Spawns.Census(UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle));
                if (Active) Reskin.Apply();
                if (IsHostOrSolo()) Net.BroadcastState();
            }
            else
            {
                Spawns.LogCounts("scene change");
            }
        }

        internal static bool IsHostOrSolo()
        {
            var nm = NetworkManager.Singleton;
            return nm == null || !nm.IsListening || nm.IsServer;
        }

        internal static bool InBaseWorldScene() =>
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == BaseWorld.ToString();

        // MainMenuManager is a NetworkBehaviour: the menu is a live NGO session (host or client).
        public static void MainMenuSpawnedPostfix(MainMenuManager __instance)
        {
            Net.EnsureRegistered();
            if (AutoStartCrabLand.Value && __instance.IsServer && !_autoStarted)
            {
                _autoStarted = true;   // once per launch: returning to the menu must not start another run
                MelonCoroutines.Start(AutoStart(__instance));
            }
        }

        private static bool _autoStarted;

        private static IEnumerator AutoStart(MainMenuManager mm)
        {
            yield return new WaitForSeconds(3f);
            if (Menu.ClickCrabLandButton()) Log.Msg("autostart: picked Crab Land through the menu button");
            else
            {
                Log.Error("autostart: Crab Land button not built; falling back to SetWorld + flag");
                mm.SetWorld(BaseWorld);
                Active = true;
            }
            yield return new WaitForSeconds(2f);
            Log.Msg($"autostart: world={mm.CurrentWorld.Value} active={Active}; StartLevelOnServerRpc");
            try { mm.StartLevelOnServerRpc(); }
            catch (Exception e) { Log.Error($"autostart: StartLevelOnServerRpc failed: {e.Message}"); }
        }

        public static bool TryToDamagePrefix(HealthContainer __instance)
        {
            if (!DebugGodMode.Value) return true;
            return __instance.TryCast<PlayerHealthContainer>() == null;
        }
    }
}
