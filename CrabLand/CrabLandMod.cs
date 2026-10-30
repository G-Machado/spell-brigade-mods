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

        internal static MelonPreferences_Entry<bool> AutoStartCrabLand, DebugGodMode, DebugSkipToBoss, KeepEnemyEvents, GrantInfusionReward, BossMechanics, ShortRun, HideAchievementPopups, EggCrabs, SeaTheme, SeaTint, SeaCaustics, SeaParticles, SeaGrass, SeaGodRays, SeaDepth, SeaElements;
        internal static MelonPreferences_Entry<float> RayfishShare, ExploderShare, BossScale, BossHealthMultiplier, DebugBossHealthMultiplier, ShellThreshold, RingRadius, ShellDamageMultiplier, ShellMaxSeconds, ObjectiveInterval, CrabDifficulty, EggLayInterval, EggHatchSeconds, SeaThemeABSeconds, SeaFreezeWindow, SeaFreezeSeconds;
        internal static MelonPreferences_Entry<int> ExploderBabies, BroodCrablings, BroodFleshCrabs, ShellShieldCrabs, EggsPerLayer, EggMaxTotal, EggHatchCrablings, SeaPropClusters, SeaFreezeStacks;
        internal static MelonPreferences_Entry<string> BroodThresholds, ObjectivePool, SeaScreenshotDir;

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
            DebugSkipToBoss = cat.CreateEntry("DebugSkipToBoss", false, description: "Debug: force the first objective at 30 s so the boss comes early");
            KeepEnemyEvents = cat.CreateEntry("KeepEnemyEvents", false, description: "Keep the native enemy events (they spawn non-crab enemies)");
            GrantInfusionReward = cat.CreateEntry("GrantInfusionReward", true, description: "After the first objective, every player picks an infusion for a spell (turn off if the store hangs)");
            BossScale = cat.CreateEntry("BossScale", 3f, description: "Crablodon boss scale");
            BossHealthMultiplier = cat.CreateEntry("BossHealthMultiplier", 1f, description: "Boss HP = native boss HP x this");
            DebugBossHealthMultiplier = cat.CreateEntry("DebugBossHealthMultiplier", 1f, description: "Debug: extra boss HP factor (e.g. 0.02 for a fast smoke run)");
            HideAchievementPopups = cat.CreateEntry("HideAchievementPopups", true, description: "Hide the in-game challenge/achievement unlock popups (unlocks still count)");
            ObjectivePool = cat.CreateEntry("ObjectivePool", "", description: "Fallback for profiles without unlocked objectives: types Crab Land starts itself (host), e.g. Hunt,Capture,Collect; empty = native only");
            ObjectiveInterval = cat.CreateEntry("ObjectiveInterval", 150f, description: "Seconds between Crab Land objectives (min 30)");
            CrabDifficulty = cat.CreateEntry("CrabDifficulty", 1.5f, description: "Crab Land: native wave difficulty multiplier x this (host)");
            ShortRun = cat.CreateEntry("ShortRun", false, description: "Boss starts right after the first objective (short run); off = full native run");
            BossMechanics = cat.CreateEntry("BossMechanics", true, description: "Brood rings and shell phase on the crab boss (host)");
            ShellMaxSeconds = cat.CreateEntry("ShellMaxSeconds", 60f, description: "Shell cracks by itself after this many seconds");
            BroodThresholds = cat.CreateEntry("BroodThresholds", "0.75,0.5,0.25", description: "Boss HP fractions that each spawn one brood ring");
            BroodCrablings = cat.CreateEntry("BroodCrablings", 8, description: "Crablings per brood ring");
            BroodFleshCrabs = cat.CreateEntry("BroodFleshCrabs", 4, description: "FleshCrabs per brood ring");
            ShellThreshold = cat.CreateEntry("ShellThreshold", 0.5f, description: "Boss HP fraction that starts the shell phase");
            ShellShieldCrabs = cat.CreateEntry("ShellShieldCrabs", 4, description: "ShieldCrabs in the shell ring");
            RingRadius = cat.CreateEntry("RingRadius", 6f, description: "Spawn ring radius around the boss");
            ShellDamageMultiplier = cat.CreateEntry("ShellDamageMultiplier", 0.1f, description: "Damage the boss takes while the shell is up");
            EggCrabs = cat.CreateEntry("EggCrabs", true, description: "Crablodons lay petrified eggs that hatch into Crablings unless broken (host)");
            EggLayInterval = cat.CreateEntry("EggLayInterval", 10f, description: "Seconds between eggs per Crablodon");
            EggsPerLayer = cat.CreateEntry("EggsPerLayer", 3, description: "Most unhatched eggs per Crablodon");
            EggMaxTotal = cat.CreateEntry("EggMaxTotal", 12, description: "Most unhatched eggs overall");
            EggHatchSeconds = cat.CreateEntry("EggHatchSeconds", 8f, description: "Seconds until an unbroken egg hatches");
            EggHatchCrablings = cat.CreateEntry("EggHatchCrablings", 4, description: "Crablings per hatched egg");
            RayfishShare = cat.CreateEntry("RayfishShare", 0.12f, description: "Share of wave spawns that are rayfish (native FlyingManta), 0-1 (host)");
            ExploderShare = cat.CreateEntry("ExploderShare", 0.15f, description: "Share of FleshCrabs that glow red and explode into Crablings on death, 0-1");
            ExploderBabies = cat.CreateEntry("ExploderBabies", 3, description: "Crablings per exploding crab (host)");
            SeaTheme = cat.CreateEntry("SeaTheme", true, description: "Under-the-sea look in Crab Land (local, every machine); master switch for the Sea* effects");
            SeaTint = cat.CreateEntry("SeaTint", true, description: "Sea: teal fog, sea-tinted light, colour grade, vignette, bloom");
            SeaCaustics = cat.CreateEntry("SeaCaustics", true, description: "Sea: animated caustics on the sun (light cookie)");
            SeaParticles = cat.CreateEntry("SeaParticles", true, description: "Sea: rising bubbles and drifting sea snow");
            SeaGrass = cat.CreateEntry("SeaGrass", true, description: "Sea: sea-floor props (swaying kelp, coral, rocks), kelp-tinted plants");
            SeaPropClusters = cat.CreateEntry("SeaPropClusters", 60, description: "Sea: kelp, coral and rock clusters on the sea floor");
            SeaGodRays = cat.CreateEntry("SeaGodRays", false, description: "Sea: light shafts from the surface (off: they read as yellow spikes from the top-down camera)");
            SeaDepth = cat.CreateEntry("SeaDepth", true, description: "Sea: the water darkens as the waves go on, darkest at the boss");
            SeaElements = cat.CreateEntry("SeaElements", true, description: "Sea: fire hits only make bubbles (no damage, no burn), lightning always crits, enemies chilled too often freeze");
            SeaFreezeStacks = cat.CreateEntry("SeaFreezeStacks", 6, description: "Sea: ice chills within SeaFreezeWindow that freeze an enemy");
            SeaFreezeWindow = cat.CreateEntry("SeaFreezeWindow", 4f, description: "Sea: seconds the chills are counted over");
            SeaFreezeSeconds = cat.CreateEntry("SeaFreezeSeconds", 3f, description: "Sea: seconds a frozen enemy stays frozen");
            SeaThemeABSeconds = cat.CreateEntry("SeaThemeABSeconds", 0f, description: "Debug: toggle the sea look off/on every N s to compare frame times (0 = off)");
            SeaScreenshotDir = cat.CreateEntry("SeaScreenshotDir", "", description: "Debug: folder for automatic sea screenshots (empty = none)");

            Log.Msg($"prefs AutoStartCrabLand={AutoStartCrabLand.Value} DebugGodMode={DebugGodMode.Value} " +
                    $"DebugSkipToBoss={DebugSkipToBoss.Value} KeepEnemyEvents={KeepEnemyEvents.Value} GrantInfusionReward={GrantInfusionReward.Value} " +
                    $"BossScale={BossScale.Value} BossHealthMultiplier={BossHealthMultiplier.Value} DebugBossHealthMultiplier={DebugBossHealthMultiplier.Value} " +
                    $"HideAchievementPopups={HideAchievementPopups.Value} ObjectivePool={ObjectivePool.Value} ObjectiveInterval={ObjectiveInterval.Value} CrabDifficulty={CrabDifficulty.Value} ShortRun={ShortRun.Value} BossMechanics={BossMechanics.Value} ShellMaxSeconds={ShellMaxSeconds.Value} BroodThresholds={BroodThresholds.Value} BroodCrablings={BroodCrablings.Value} BroodFleshCrabs={BroodFleshCrabs.Value} " +
                    $"ShellThreshold={ShellThreshold.Value} ShellShieldCrabs={ShellShieldCrabs.Value} RingRadius={RingRadius.Value} ShellDamageMultiplier={ShellDamageMultiplier.Value} " +
                    $"EggCrabs={EggCrabs.Value} EggLayInterval={EggLayInterval.Value} EggsPerLayer={EggsPerLayer.Value} EggMaxTotal={EggMaxTotal.Value} EggHatchSeconds={EggHatchSeconds.Value} EggHatchCrablings={EggHatchCrablings.Value} " +
                    $"RayfishShare={RayfishShare.Value} ExploderShare={ExploderShare.Value} ExploderBabies={ExploderBabies.Value} " +
                    $"SeaTheme={SeaTheme.Value} SeaTint={SeaTint.Value} SeaCaustics={SeaCaustics.Value} SeaParticles={SeaParticles.Value} SeaGrass={SeaGrass.Value} SeaGodRays={SeaGodRays.Value} SeaDepth={SeaDepth.Value} SeaElements={SeaElements.Value} SeaFreezeStacks={SeaFreezeStacks.Value} SeaFreezeWindow={SeaFreezeWindow.Value} SeaFreezeSeconds={SeaFreezeSeconds.Value} SeaThemeABSeconds={SeaThemeABSeconds.Value}");

            var hooks = new Hooks(HarmonyInstance, LoggerInstance)
                .Add<WorldsPanel>("InitializeButtons", postfix: P(typeof(Menu), nameof(Menu.InitializeButtonsPostfix)))
                .Add<WorldsPanel>("OnWorldButtonClicked", prefix: P(typeof(Menu), nameof(Menu.OnWorldButtonClickedPrefix)))
                .Add<MainMenuManager>("OnNetworkSpawn", postfix: P(typeof(CrabLandMod), nameof(MainMenuSpawnedPostfix)))
                .Add<EnemySpawner>("CreateEnemyTypeToPrefabDictionary", postfix: P(typeof(Spawns), nameof(Spawns.CreateDictionaryPostfix)))
                .Add<EnemySpawner>("GetRandomEnemyForWave", postfix: P(typeof(Spawns), nameof(Spawns.RandomEnemyPostfix)))
                .Add<EnemyHealthContainer>("HealthDepleted", postfix: P(typeof(Exploders), nameof(Exploders.HealthDepletedPostfix)))
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
                .Add<RunManager>("StartWave", prefix: P(typeof(Run), nameof(Run.StartWavePrefix)))
                .Add<RunManager>("GetWaveDifficultyMultiplier", postfix: P(typeof(CrabLandMod), nameof(WaveDifficultyPostfix)))
                .Add<StoreManager>("CheckForSpellInfusion", prefix: P(typeof(Run), nameof(Run.CheckForSpellInfusionPrefix)))
                .Add<StoreManager>("OfferSpellInfusion", prefix: P(typeof(Run), nameof(Run.OfferSpellInfusionPrefix)))
                .Add<StoreManager>("ChooseSpellImprovement", prefix: P(typeof(Run), nameof(Run.ChooseSpellImprovementPrefix)))
                .Add<ImprovementsManager>("AddSpellInfusion", prefix: P(typeof(Run), nameof(Run.AddSpellInfusionPrefix)))
                .Add<ImprovementsManager>("SendSpellInfusionRpc", prefix: P(typeof(Run), nameof(Run.SendSpellInfusionRpcPrefix)))
                .Add<SpellImprovementLevelDeterminer>("DetermineSpellLevel", postfix: P(typeof(Run), nameof(Run.DetermineSpellLevelPostfix)))
                .Add<InfusionStoreAvailabilityChecker>("InfusionStoreShouldBeOffered", postfix: P(typeof(Run), nameof(Run.InfusionShouldBeOfferedPostfix)))
                .Add<ChallengeNotification>("QueueChallenge", prefix: P(typeof(CrabLandMod), nameof(QueueChallengePrefix)))
                .Add<PetrifiedEnemy>("OnHealthDepleted", prefix: P(typeof(Eggs), nameof(Eggs.OnHealthDepletedPrefix)))
                .Add<PetrifiedEnemy>("ReplaceWithRegularEnemy", prefix: P(typeof(Eggs), nameof(Eggs.ReplaceWithRegularEnemyPrefix)));
            ThemeElements.AddHooks(hooks);
            hooks.Install();
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
                BossFight.Reset();
                Eggs.Reset();
                Exploders.Reset();
                ThemeElements.Reset();
                if (DebugSkipToBoss.Value) MelonCoroutines.Start(Run.DebugForceFirstObjective());
                if (Active && IsHostOrSolo() && !string.IsNullOrWhiteSpace(ObjectivePool.Value))
                    MelonCoroutines.Start(Run.ObjectiveTimer(UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle));
                MelonCoroutines.Start(Spawns.Census(UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle));
                MelonCoroutines.Start(Eggs.Loop(UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle));
                MelonCoroutines.Start(Exploders.Loop(UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle));
                if (Active) Reskin.Apply();
                if (Active) Theme.Apply();
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

        // UI only: the challenge is already unlocked when its popup is queued.
        public static bool QueueChallengePrefix(ChallengeId challengeId)
        {
            if (!HideAchievementPopups.Value) return true;
            Log.Msg($"popup: hid challenge notification id={challengeId}");
            return false;
        }

        private static float _lastDifficulty = -1f;

        public static void WaveDifficultyPostfix(ref float __result)
        {
            if (!Active) return;
            var native = __result;
            __result = native * CrabDifficulty.Value;
            if (Mathf.Abs(native - _lastDifficulty) < 0.01f) return;   // log when the native value moves
            _lastDifficulty = native;
            Log.Msg($"difficulty: native={native} x{CrabDifficulty.Value} -> {__result}");
        }

        public static bool TryToDamagePrefix(HealthContainer __instance, ref float amount, DamageInfo damageInfo, ref bool hitIsCritical)
        {
            if (!ThemeElements.OnTryToDamage(__instance, ref amount, damageInfo, ref hitIsCritical)) return false;
            BossFight.ScaleDamage(__instance, ref amount);
            if (!DebugGodMode.Value) return true;
            return __instance.TryCast<PlayerHealthContainer>() == null;
        }
    }
}
