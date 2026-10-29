using System;
using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>
    /// Run shape: crab waves -> first objective -> (reward) -> crab boss. The host starts the native EndBoss
    /// objective as soon as the first objective succeeds. The infusion hooks are log-only probes: method bodies
    /// are stubbed in the game map, so spell level, max level and the infusion RPC direction come from a live run.
    /// </summary>
    internal static class Run
    {
        private const float BossDelay = 5f, DebugObjectiveDelay = 30f;

        private static bool _bossStarted, _objectiveSeen, _rewardGranted;
        private static readonly Dictionary<string, int> LastLevel = new Dictionary<string, int>();
        private static bool _levelsLogged;

        internal static void Reset()
        {
            _bossStarted = false; _objectiveSeen = false; _rewardGranted = false; _levelsLogged = false;
            LastLevel.Clear();
        }

        private static string Name(IAmObjective o)
        {
            if (o == null) return "null";
            var obj = o.TryCast<Il2CppSystem.Object>();
            return obj != null ? obj.GetIl2CppType().Name : "?";
        }

        // --- objectives ---

        public static void ObjectiveStartedPostfix(IAmObjective objective)
        {
            _objectiveSeen = true;
            CrabLandMod.Log.Msg($"objective: started {Name(objective)} active={CrabLandMod.Active} host={CrabLandMod.IsHostOrSolo()}");
        }

        public static void ObjectiveEndedPostfix(ObjectiveDirector __instance, IAmObjective objective, bool isSuccessful, float timeLeft)
        {
            CrabLandMod.Log.Msg($"objective: ended {Name(objective)} success={isSuccessful} timeLeft={timeLeft} " +
                                $"completed={__instance.SuccessfullyCompletedObjectives} active={CrabLandMod.Active}");
            if (!CrabLandMod.Active || !isSuccessful || _bossStarted || !CrabLandMod.IsHostOrSolo()) return;
            if (objective != null && objective.TryCast<EndBossObjective>() != null) return;
            _bossStarted = true;
            if (CrabLandMod.GrantInfusionReward.Value)
            {
                Net.BroadcastReward();
                GrantLocalInfusion();
            }
            MelonCoroutines.Start(StartBossAfterDelay(__instance));
        }

        private static IEnumerator StartBossAfterDelay(ObjectiveDirector director)
        {
            yield return new WaitForSeconds(BossDelay);
            try
            {
                var boss = director.StartObjective(Objective.EndBoss);
                CrabLandMod.Log.Msg($"objective: early EndBoss started -> {Name(boss)}");
            }
            catch (Exception e) { CrabLandMod.Log.Error($"objective: StartObjective(EndBoss) failed: {e}"); }
        }

        /// <summary>
        /// Reward, on every machine for its own player: the native infusion store for the first equipped spell
        /// that has no element yet. The player picks; the native pick path grants and syncs it.
        /// Not done: the spell's remaining levels (level is computed, no setter found; see docs/hooks.md).
        /// </summary>
        internal static void GrantLocalInfusion()
        {
            if (_rewardGranted || !CrabLandMod.Active) return;
            _rewardGranted = true;
            try
            {
                PlayerStats mine = null;
                foreach (var ps in UnityEngine.Object.FindObjectsOfType<PlayerStats>())
                    if (ps.IsOwner) { mine = ps; break; }
                var store = NetworkSingleton<StoreManager>.Instance;
                if (mine == null || store == null)
                {
                    CrabLandMod.Log.Error($"reward: local PlayerStats={mine != null} StoreManager={store != null}; no reward");
                    return;
                }
                var spells = mine.GetEquippedSpells();
                if (spells == null || spells.Count == 0) { CrabLandMod.Log.Error("reward: no equipped spells"); return; }
                var spell = spells[0];
                for (int i = 0; i < spells.Count; i++)
                    if (mine.GetElement(spells[i]) == ElementType.None) { spell = spells[i]; break; }
                CrabLandMod.Log.Msg($"reward: OfferSpellInfusion spell={spell} level={mine.GetLevel(spell)} " +
                                    $"element={mine.GetElement(spell)} equipped={spells.Count}");
                store.OfferSpellInfusion(spell);
            }
            catch (Exception e) { CrabLandMod.Log.Error($"reward: failed: {e}"); }
        }

        public static void DetermineObjectivePostfix(int waveNumber, int numberOfWaves) =>
            CrabLandMod.Log.Msg($"objective: determine wave={waveNumber}/{numberOfWaves}");

        /// <summary>Debug: start the first objective at 30 s so a smoke run does not wait for its wave.</summary>
        internal static IEnumerator DebugForceFirstObjective()
        {
            yield return new WaitForSeconds(DebugObjectiveDelay);
            if (!CrabLandMod.Active || _objectiveSeen || !CrabLandMod.IsHostOrSolo()) yield break;
            var director = NetworkSingleton<ObjectiveDirector>.Instance;
            if (director == null) { CrabLandMod.Log.Error("debug: no ObjectiveDirector"); yield break; }
            try
            {
                var o = director.StartObjective(Objective.Hunt);
                CrabLandMod.Log.Msg($"debug: forced first objective Hunt -> {Name(o)}");
            }
            catch (Exception e) { CrabLandMod.Log.Error($"debug: StartObjective(Hunt) failed: {e}"); }
        }

        // --- infusion probes (log only) ---

        public static void CheckForSpellInfusionPrefix(SpellType spell, int level) =>
            CrabLandMod.Log.Msg($"probe: CheckForSpellInfusion spell={spell} level={level}");

        public static void OfferSpellInfusionPrefix(SpellType spell) =>
            CrabLandMod.Log.Msg($"probe: OfferSpellInfusion spell={spell}");

        public static void ChooseSpellImprovementPrefix(SpellImprovementItem spellImprovement) =>
            CrabLandMod.Log.Msg($"probe: ChooseSpellImprovement item={(spellImprovement != null ? spellImprovement.GetIl2CppType().Name : "null")}");

        public static void AddSpellInfusionPrefix(ImprovementsManager __instance, SpellType spell, ElementType element) =>
            CrabLandMod.Log.Msg($"probe: AddSpellInfusion spell={spell} element={element} server={__instance.IsServer} " +
                                $"client={__instance.IsClient} localId={__instance.NetworkManager?.LocalClientId}");

        public static void SendSpellInfusionRpcPrefix(ImprovementsManager __instance, ulong clientId, SpellInfusionMessage message) =>
            CrabLandMod.Log.Msg($"probe: SendSpellInfusionRpc clientId={clientId} spell={message?.Spell} element={message?.Element} " +
                                $"server={__instance.IsServer}");

        public static void DetermineSpellLevelPostfix(ulong clientID, SpellType spell, int __result)
        {
            var key = $"{clientID}/{spell}";
            if (LastLevel.TryGetValue(key, out var last) && last == __result) return;
            LastLevel[key] = __result;
            CrabLandMod.Log.Msg($"probe: DetermineSpellLevel client={clientID} spell={spell} level={__result}");
        }

        public static void InfusionShouldBeOfferedPostfix(InfusionStoreAvailabilityChecker __instance, SpellType spell, int spellLevel, bool __result)
        {
            CrabLandMod.Log.Msg($"probe: InfusionStoreShouldBeOffered spell={spell} level={spellLevel} -> {__result}");
            if (_levelsLogged) return;
            _levelsLogged = true;
            CrabLandMod.Log.Msg($"probe: infusion baseLevels=[{Join(__instance.baseLevels)}] adjustedLevels=[{Join(__instance.adjustedLevels)}]");
        }

        private static string Join(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> a)
        {
            if (a == null) return "null";
            var parts = new string[a.Length];
            for (int i = 0; i < a.Length; i++) parts[i] = a[i].ToString();
            return string.Join(",", parts);
        }
    }
}
