using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SpellBrigadeMods.CrabDirector
{
    /// <summary>Game side of the inputs: event bus subscriptions and the 4 Hz player sample, fed into the core <see cref="Meter"/>.</summary>
    internal static class Inputs
    {
        public static int BusCaptures { get; private set; }
        public static ServerGameplayEventBus Bus { get; private set; }

        private static Meter Meter => Director.Core.Meter;
        private static readonly Dictionary<ulong, GameplayPlayer> PlayerObjects = new Dictionary<ulong, GameplayPlayer>();
        private static readonly List<RawSample> Samples = new List<RawSample>();

        // Kept alive for the native side.
        private static Il2CppSystem.Action<OnDamageTakenEventData> _onDamageTaken;
        private static Il2CppSystem.Action<OnDeathEventData> _onDeath;
        private static Il2CppSystem.Action<OnReviveUsedEventData> _onReviveUsed;
        private static Il2CppSystem.Action<OnTeammateRevivedEventData> _onTeammateRevived;
        private static Il2CppSystem.Action<OnEnemyKilledEventData> _onEnemyKilled;
        private static Il2CppSystem.Action<OnEnemyDamageDealtEventData> _onEnemyDamageDealt;
        private static Il2CppSystem.Action<OnSpellDamageDoneEventData> _onSpellDamageDone;

        public static void Reset() => PlayerObjects.Clear();

        public static void Remember(GameplayPlayer p) => PlayerObjects[p.OwnerClientId] = p;

        public static Transform TransformOf(ulong id) =>
            PlayerObjects.TryGetValue(id, out var p) && p != null ? p.GetTransform() : null;

        // ---- event bus ----

        public static void BusCtorPostfix(ServerGameplayEventBus __instance)
        {
            try
            {
                Bus = __instance;
                BusCaptures++;
                _onDamageTaken ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<OnDamageTakenEventData>>(new Action<OnDamageTakenEventData>(OnDamageTaken));
                _onDeath ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<OnDeathEventData>>(new Action<OnDeathEventData>(OnDeath));
                _onReviveUsed ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<OnReviveUsedEventData>>(new Action<OnReviveUsedEventData>(OnReviveUsed));
                _onTeammateRevived ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<OnTeammateRevivedEventData>>(new Action<OnTeammateRevivedEventData>(OnTeammateRevived));
                _onEnemyKilled ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<OnEnemyKilledEventData>>(new Action<OnEnemyKilledEventData>(OnEnemyKilled));
                _onEnemyDamageDealt ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<OnEnemyDamageDealtEventData>>(new Action<OnEnemyDamageDealtEventData>(OnEnemyDamageDealt));
                _onSpellDamageDone ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<OnSpellDamageDoneEventData>>(new Action<OnSpellDamageDoneEventData>(OnSpellDamageDone));

                __instance.OnDamageTaken.Subscribe(_onDamageTaken);
                __instance.OnDeath.Subscribe(_onDeath);
                __instance.OnReviveUsed.Subscribe(_onReviveUsed);
                __instance.OnTeammateRevived.Subscribe(_onTeammateRevived);
                __instance.OnEnemyKilled.Subscribe(_onEnemyKilled);
                __instance.OnEnemyDamageDealt.Subscribe(_onEnemyDamageDealt);
                __instance.OnSpellDamageDone.Subscribe(_onSpellDamageDone);

                CrabDirectorMod.Log.Msg($"bus captured n={BusCaptures} t={Time.time:F1} host={CrabDirectorMod.IsHostOrSolo()}");
                JsonLog.Write(new Dictionary<string, object> { ["ev"] = "bus", ["t"] = Time.time, ["n"] = BusCaptures });
            }
            catch (Exception e) { CrabDirectorMod.Log.Error($"bus capture failed: {e}"); }
        }

        private static bool Listening => CrabDirectorMod.Running;

        private static void OnDamageTaken(OnDamageTakenEventData e)
        {
            if (!Listening || e == null) return;
            Meter.DamageTaken(e.VictimClientId, e.Damage, Time.time);
            // Raw for probe 7 (before or after armor): damage vs HP read at event time.
            float hp = -1, max = -1;
            try
            {
                var hc = PlayerObjects.TryGetValue(e.VictimClientId, out var p) && p != null ? p.healthContainer : null;
                if (hc != null) { hp = hc.CurrentHealth; max = hc.MaxHealth; }
            }
            catch { }
            JsonLog.Write(new Dictionary<string, object>
            {
                ["ev"] = "hit", ["t"] = Time.time, ["id"] = e.VictimClientId, ["damage"] = e.Damage, ["hpNow"] = hp, ["maxHp"] = max,
            });
        }

        private static void OnDeath(OnDeathEventData e)
        {
            if (!Listening || e == null) return;
            Meter.Down(e.VictimClientId, Time.time);
            JsonLog.Write(new Dictionary<string, object> { ["ev"] = "down", ["t"] = Time.time, ["id"] = e.VictimClientId });
        }

        private static void OnReviveUsed(OnReviveUsedEventData e)
        {
            if (!Listening) return;
            Meter.Events++;
            JsonLog.Write(new Dictionary<string, object> { ["ev"] = "reviveUsed", ["t"] = Time.time });
        }

        private static void OnTeammateRevived(OnTeammateRevivedEventData e)
        {
            if (!Listening) return;
            Meter.Events++;
            JsonLog.Write(new Dictionary<string, object> { ["ev"] = "teammateRevived", ["t"] = Time.time });
        }

        private static void OnEnemyKilled(OnEnemyKilledEventData e)
        {
            if (Listening && e != null) Meter.Kill(e.DamageSourceClientId);
        }

        private static void OnEnemyDamageDealt(OnEnemyDamageDealtEventData e)
        {
            if (Listening && e != null) Meter.Dealt(e.DamageSourceClientId, e.Damage);
        }

        private static void OnSpellDamageDone(OnSpellDamageDoneEventData e)
        {
            if (Listening && e != null) Meter.Spell(e.ClientId, e.SpellType.ToString(), e.Amount);
        }

        // ---- 4 Hz sample ----

        public static void Sample(float now, float dt)
        {
            var pm = GameplayPlayerManager.Instance;
            if (pm == null) return;
            Samples.Clear();
            var list = pm.AlivePlayers;
            for (var i = 0; list != null && i < list.Count; i++)
            {
                var p = list[i];
                if (p == null) continue;
                var id = p.OwnerClientId;
                PlayerObjects[id] = p;
                var r = new RawSample { Id = id, Chasing = p.ChasingEnemyCount };
                var hc = p.healthContainer;
                if (hc != null) { r.Hp = hc.CurrentHealth; r.MaxHp = hc.MaxHealth; }
                var pos = p.GetTransform().position;
                r.Pos = new V3(pos.x, pos.y, pos.z);
                Samples.Add(r);
            }
            Meter.Sample(now, dt, Samples);
        }
    }
}
