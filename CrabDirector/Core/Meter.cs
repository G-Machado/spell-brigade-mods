using System;
using System.Collections.Generic;

namespace SpellBrigadeMods.CrabDirector
{
    /// <summary>The three inputs (pressure, output, positioning) per player, and the intensity meter.</summary>
    public sealed class Meter
    {
        public readonly Dictionary<ulong, PlayerState> Players = new Dictionary<ulong, PlayerState>();
        public readonly List<PlayerState> Alive = new List<PlayerState>();
        public int Events;
        public Tuning T = new Tuning();

        // SpellType has no category in code: classified by name (assumed, docs/director.md).
        private static readonly HashSet<string> Projectile = new HashSet<string>
        {
            "Orb", "Boomerang", "Shotgun", "Rifle", "Smg", "BombLauncher", "Arrow", "Splinter", "Sawblade", "Book",
        };

        public void Reset(Tuning t)
        {
            T = t;
            Players.Clear();
            Alive.Clear();
            Events = 0;
        }

        public PlayerState Get(ulong id)
        {
            if (!Players.TryGetValue(id, out var s)) Players[id] = s = new PlayerState { Id = id };
            return s;
        }

        // ---- events ----

        public void DamageTaken(ulong id, float damage, float now)
        {
            Events++;
            var s = Get(id);
            s.AccDmgTaken += damage;
            s.SecDmgTaken += damage;
            s.LastHit = now;
            if (damage >= T.DecayIgnoreHitsUnder) s.AccHit = true;
        }

        public void Down(ulong id, float now)
        {
            Events++;
            var s = Get(id);
            s.AccDowns++;
            s.SecDowns++;
            s.Downs.Add(now);
        }

        public void Kill(ulong id)
        {
            Events++;
            var s = Get(id);
            s.AccKills++;
            s.SecKills++;
        }

        public void Dealt(ulong id, float damage)
        {
            Events++;
            var s = Get(id);
            s.AccDealt += damage;
            s.SecDealt += damage;
        }

        public void Spell(ulong id, string spellType, float amount)
        {
            Events++;
            var s = Get(id);
            s.SecSpell[spellType] = s.SecSpell.TryGetValue(spellType, out var v) ? v + amount : amount;
            s.AccSpellTotal += amount;
            if (Projectile.Contains(spellType)) s.AccSpellProj += amount;
        }

        // ---- 4 Hz sample ----

        public void Sample(float now, float dt, IReadOnlyList<RawSample> alive)
        {
            var a = 1f - MathF.Exp(-dt / T.EmaTau);

            foreach (var s in Players.Values) s.Alive = false;
            Alive.Clear();
            foreach (var r in alive)
            {
                var s = Get(r.Id);
                s.Alive = true;
                Alive.Add(s);
                s.Hp = r.Hp;
                s.MaxHp = r.MaxHp;
                s.Chasing = r.Chasing;
                if (s.HasPos)
                {
                    var v = ((r.Pos - s.Pos) / dt).Flat;
                    s.Vel = V3.Lerp(s.Vel, v, a);
                    s.Speed += a * (v.Magnitude - s.Speed);
                }
                s.Pos = r.Pos;
                s.HasPos = true;
                s.StillFor = s.Speed < T.StationarySpeed ? s.StillFor + dt : 0f;
                s.Stationary = s.StillFor >= T.StationarySeconds;
            }

            foreach (var s in Players.Values)
            {
                s.DmgTakenRate += a * (s.AccDmgTaken / dt - s.DmgTakenRate);
                s.KillRate += a * (s.AccKills / dt - s.KillRate);
                s.DealtRate += a * (s.AccDealt / dt - s.DealtRate);

                // Intensity meter (docs/director.md)
                var maxHp = MathF.Max(1f, s.MaxHp);
                var dmg = MathF.Max(0f, s.AccDmgTaken - T.DamageDeadbandDps * dt);
                if (dmg > 0f && s.AccHit) s.LastPressure = now;
                var dI = dmg / maxHp * T.DamageGain
                         + T.ChasingGain * s.Chasing * dt
                         + s.AccDowns * T.DownGain
                         + (s.Alive && s.MaxHp > 0 && s.HpFrac < T.LowHp ? T.LowHpGain * dt : 0f);
                if (now - s.LastPressure > T.DecayDelay) dI -= T.Decay * dt;
                s.I = M.Clamp01(s.I + dI);

                s.SpellWindow.Enqueue((now, s.AccSpellProj, s.AccSpellTotal));
                while (s.SpellWindow.Count > 0 && now - s.SpellWindow.Peek().t > T.ProjectileWindow) s.SpellWindow.Dequeue();
                float proj = 0, total = 0;
                foreach (var w in s.SpellWindow) { proj += w.proj; total += w.total; }
                s.ProjShare = total > 0 ? proj / total : 0f;
                s.ProjectileBuild = s.ProjShare >= T.ProjectileShare;

                s.Downs.RemoveAll(t => now - t > 60f);
                s.AccDmgTaken = s.AccDealt = s.AccSpellProj = s.AccSpellTotal = 0f;
                s.AccKills = s.AccDowns = 0;
                s.AccHit = false;
            }
        }

        public List<Dictionary<string, object>> SecondLines()
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var s in Players.Values)
            {
                rows.Add(new Dictionary<string, object>
                {
                    ["id"] = s.Id, ["alive"] = s.Alive, ["hp"] = s.Hp, ["maxHp"] = s.MaxHp,
                    ["dmgTaken"] = s.SecDmgTaken, ["kills"] = s.SecKills, ["dealt"] = s.SecDealt,
                    ["downs"] = s.SecDowns, ["speed"] = s.Speed, ["chasing"] = s.Chasing, ["stillFor"] = s.StillFor,
                    ["x"] = s.Pos.X, ["z"] = s.Pos.Z, ["I"] = s.I, ["projShare"] = s.ProjShare,
                    ["spell"] = new Dictionary<string, float>(s.SecSpell),
                });
                s.SecDmgTaken = s.SecDealt = 0f;
                s.SecKills = s.SecDowns = 0;
                s.SecSpell.Clear();
            }
            return rows;
        }
    }
}
