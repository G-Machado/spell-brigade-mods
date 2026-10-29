using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SpellBrigadeMods.CrabDirector
{
    internal sealed class PlayerState
    {
        public ulong Id;
        public GameplayPlayer Player;
        public bool Alive;

        // Sampled
        public float Hp, MaxHp;
        public int Chasing;
        public Vector3 Pos, Vel;
        public bool HasPos;
        public float Speed, StillFor;

        // Smoothed rates (EMA, per second)
        public float DmgTakenRate, KillRate, DealtRate;

        // Event accumulators, cleared every sample
        public float AccDmgTaken, AccDealt, AccSpellProj, AccSpellTotal;
        public int AccKills, AccDowns;

        // Per-second raw totals for the log, cleared every second
        public float SecDmgTaken, SecDealt;
        public int SecKills, SecDowns, SecRevives;
        public readonly Dictionary<string, float> SecSpell = new Dictionary<string, float>();

        // Derived
        public float I;
        public float LastHit = -999f;
        public float ProjShare;
        public readonly Queue<(float t, float proj, float total)> SpellWindow = new Queue<(float, float, float)>();
        public readonly List<float> Downs = new List<float>();

        public float HpFrac => MaxHp > 0 ? Hp / MaxHp : 1f;
        public bool Stationary => StillFor >= CrabDirectorMod.Tuning.StationarySeconds;
        public bool ProjectileBuild => ProjShare >= CrabDirectorMod.Tuning.ProjectileShare;
    }

    /// <summary>The three inputs (pressure, output, positioning), per player, from the event bus and 4 Hz samples.</summary>
    internal static class Inputs
    {
        public static readonly Dictionary<ulong, PlayerState> Players = new Dictionary<ulong, PlayerState>();
        public static readonly List<PlayerState> Alive = new List<PlayerState>();
        public static int BusCaptures { get; private set; }
        public static ServerGameplayEventBus Bus { get; private set; }
        public static int Events;

        // SpellType has no category in code: classified by name (assumed, docs/director.md).
        private static readonly HashSet<string> Projectile = new HashSet<string>
        {
            "Orb", "Boomerang", "Shotgun", "Rifle", "Smg", "BombLauncher", "Arrow", "Splinter", "Sawblade", "Book",
        };

        // Kept alive for the native side.
        private static Il2CppSystem.Action<OnDamageTakenEventData> _onDamageTaken;
        private static Il2CppSystem.Action<OnDeathEventData> _onDeath;
        private static Il2CppSystem.Action<OnReviveUsedEventData> _onReviveUsed;
        private static Il2CppSystem.Action<OnTeammateRevivedEventData> _onTeammateRevived;
        private static Il2CppSystem.Action<OnEnemyKilledEventData> _onEnemyKilled;
        private static Il2CppSystem.Action<OnEnemyDamageDealtEventData> _onEnemyDamageDealt;
        private static Il2CppSystem.Action<OnSpellDamageDoneEventData> _onSpellDamageDone;

        private static Tuning T => CrabDirectorMod.Tuning;

        public static void Reset()
        {
            Players.Clear();
            Alive.Clear();
            Events = 0;
        }

        private static PlayerState Get(ulong id)
        {
            if (!Players.TryGetValue(id, out var s)) Players[id] = s = new PlayerState { Id = id };
            return s;
        }

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

        private static bool Listening => CrabDirectorMod.Enabled.Value && CrabDirectorMod.IsHostOrSolo();

        private static void OnDamageTaken(OnDamageTakenEventData e)
        {
            if (!Listening || e == null) return;
            Events++;
            var s = Get(e.VictimClientId);
            s.AccDmgTaken += e.Damage;
            s.SecDmgTaken += e.Damage;
            s.LastHit = Time.time;
            // Raw for probe 7 (before or after armor): damage vs HP read at event time.
            float hp = -1, max = -1;
            try
            {
                var hc = s.Player != null ? s.Player.healthContainer : null;
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
            Events++;
            var s = Get(e.VictimClientId);
            s.AccDowns++;
            s.SecDowns++;
            s.Downs.Add(Time.time);
            JsonLog.Write(new Dictionary<string, object> { ["ev"] = "down", ["t"] = Time.time, ["id"] = e.VictimClientId });
        }

        private static void OnReviveUsed(OnReviveUsedEventData e)
        {
            if (!Listening) return;
            Events++;
            JsonLog.Write(new Dictionary<string, object> { ["ev"] = "reviveUsed", ["t"] = Time.time });
        }

        private static void OnTeammateRevived(OnTeammateRevivedEventData e)
        {
            if (!Listening) return;
            Events++;
            JsonLog.Write(new Dictionary<string, object> { ["ev"] = "teammateRevived", ["t"] = Time.time });
        }

        private static void OnEnemyKilled(OnEnemyKilledEventData e)
        {
            if (!Listening || e == null) return;
            Events++;
            var s = Get(e.DamageSourceClientId);
            s.AccKills++;
            s.SecKills++;
        }

        private static void OnEnemyDamageDealt(OnEnemyDamageDealtEventData e)
        {
            if (!Listening || e == null) return;
            Events++;
            var s = Get(e.DamageSourceClientId);
            s.AccDealt += e.Damage;
            s.SecDealt += e.Damage;
        }

        private static void OnSpellDamageDone(OnSpellDamageDoneEventData e)
        {
            if (!Listening || e == null) return;
            Events++;
            var s = Get(e.ClientId);
            var name = e.SpellType.ToString();
            s.SecSpell[name] = s.SecSpell.TryGetValue(name, out var v) ? v + e.Amount : e.Amount;
            s.AccSpellTotal += e.Amount;
            if (Projectile.Contains(name)) s.AccSpellProj += e.Amount;
        }

        // ---- 4 Hz sample ----

        public static void Sample(float now, float dt)
        {
            var pm = GameplayPlayerManager.Instance;
            if (pm == null) return;
            var a = 1f - Mathf.Exp(-dt / T.EmaTau);

            foreach (var s in Players.Values) s.Alive = false;
            Alive.Clear();
            var list = pm.AlivePlayers;
            for (var i = 0; list != null && i < list.Count; i++)
            {
                var p = list[i];
                if (p == null) continue;
                var s = Get(p.OwnerClientId);
                s.Player = p;
                s.Alive = true;
                Alive.Add(s);

                var hc = p.healthContainer;
                if (hc != null) { s.Hp = hc.CurrentHealth; s.MaxHp = hc.MaxHealth; }
                s.Chasing = p.ChasingEnemyCount;

                var pos = p.GetTransform().position;
                if (s.HasPos)
                {
                    var v = (pos - s.Pos) / dt;
                    v.y = 0f;
                    s.Vel = Vector3.Lerp(s.Vel, v, a);
                    s.Speed += a * (v.magnitude - s.Speed);
                }
                s.Pos = pos;
                s.HasPos = true;
                s.StillFor = s.Speed < T.StationarySpeed ? s.StillFor + dt : 0f;
            }

            foreach (var s in Players.Values)
            {
                s.DmgTakenRate += a * (s.AccDmgTaken / dt - s.DmgTakenRate);
                s.KillRate += a * (s.AccKills / dt - s.KillRate);
                s.DealtRate += a * (s.AccDealt / dt - s.DealtRate);

                // Intensity meter (docs/director.md)
                var maxHp = Mathf.Max(1f, s.MaxHp);
                var dI = s.AccDmgTaken / maxHp * T.DamageGain
                         + T.ChasingGain * s.Chasing * dt
                         + s.AccDowns * T.DownGain
                         + (s.Alive && s.MaxHp > 0 && s.HpFrac < T.LowHp ? T.LowHpGain * dt : 0f);
                if (now - s.LastHit > T.DecayDelay) dI -= T.Decay * dt;
                s.I = Mathf.Clamp01(s.I + dI);

                s.SpellWindow.Enqueue((now, s.AccSpellProj, s.AccSpellTotal));
                while (s.SpellWindow.Count > 0 && now - s.SpellWindow.Peek().t > T.ProjectileWindow) s.SpellWindow.Dequeue();
                float proj = 0, total = 0;
                foreach (var w in s.SpellWindow) { proj += w.proj; total += w.total; }
                s.ProjShare = total > 0 ? proj / total : 0f;

                s.Downs.RemoveAll(t => now - t > 60f);
                s.AccDmgTaken = s.AccDealt = s.AccSpellProj = s.AccSpellTotal = 0f;
                s.AccKills = s.AccDowns = 0;
            }
        }

        public static List<Dictionary<string, object>> SecondLines()
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var s in Players.Values)
            {
                rows.Add(new Dictionary<string, object>
                {
                    ["id"] = s.Id, ["alive"] = s.Alive, ["hp"] = s.Hp, ["maxHp"] = s.MaxHp,
                    ["dmgTaken"] = s.SecDmgTaken, ["kills"] = s.SecKills, ["dealt"] = s.SecDealt,
                    ["downs"] = s.SecDowns, ["speed"] = s.Speed, ["chasing"] = s.Chasing, ["stillFor"] = s.StillFor,
                    ["x"] = s.Pos.x, ["z"] = s.Pos.z, ["I"] = s.I, ["projShare"] = s.ProjShare,
                    ["spell"] = new Dictionary<string, float>(s.SecSpell),
                });
                s.SecDmgTaken = s.SecDealt = 0f;
                s.SecKills = s.SecDowns = s.SecRevives = 0;
                s.SecSpell.Clear();
            }
            return rows;
        }
    }
}
