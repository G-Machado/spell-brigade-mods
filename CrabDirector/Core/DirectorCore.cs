using System;
using System.Collections.Generic;
using System.Linq;

namespace SpellBrigadeMods.CrabDirector
{
    /// <summary>
    /// Intensity phases (build-up / peak / relax), the six Crab Land rules and the caps. Emits raw log
    /// lines and hands each spawn to <see cref="Execute"/> (the game's SpawnGroupAsync in the mod).
    /// </summary>
    public sealed class DirectorCore
    {
        public readonly Meter Meter = new Meter();
        public Tuning T = new Tuning();

        /// <summary>Runs one spawn request; may add fields to the log line; false = failed.</summary>
        public Func<SpawnRequest, Dictionary<string, object>, bool> Execute = (r, line) => true;
        public Action<Dictionary<string, object>> Emit = _ => { };
        public Action<string> Decide = _ => { };

        public Phase Phase { get; private set; }
        public float IParty { get; private set; }
        public float Bank { get; private set; }
        public float Rate { get; private set; }
        public bool SpawnDisabled { get; set; }
        public float Now { get; private set; }

        private Random _rng = new Random();
        private float _phaseStart, _peakLen;
        private int _spawnSeq;
        private readonly Dictionary<string, float> _nextAllowed = new Dictionary<string, float>();
        private readonly Queue<(float t, float kps)> _killHistory = new Queue<(float, float)>();
        public readonly Dictionary<string, int> RuleFires = new Dictionary<string, int>();

        public void Reset(Tuning t, float now, int seed)
        {
            T = t;
            Meter.Reset(t);
            _rng = new Random(seed);
            _nextAllowed.Clear();
            _killHistory.Clear();
            RuleFires.Clear();
            SpawnDisabled = false;
            Bank = Rate = IParty = 0f;
            _spawnSeq = 0;
            Now = now;
            SetPhase(Phase.BuildUp, "run start");
        }

        /// <summary>One 4 Hz step, after <see cref="Meter.Sample"/>.</summary>
        public void Step(float now, float dt, IWorld w)
        {
            Now = now;
            var alive = Meter.Alive;
            IParty = alive.Count == 0 ? 0f : alive.Max(p => p.I);

            var kps = Meter.Players.Values.Sum(p => p.KillRate);
            _killHistory.Enqueue((now, kps));
            while (_killHistory.Count > 0 && now - _killHistory.Peek().t > T.HeavyWindow) _killHistory.Dequeue();

            var inPhase = now - _phaseStart;

            // Rule 1, any phase: breather.
            if (Phase != Phase.Relax && alive.Count > 0)
            {
                var teamHp = alive.Average(p => p.HpFrac);
                var downs = Meter.Players.Values.Sum(p => p.Downs.Count(t => now - t <= T.BreatherWindow));
                if (teamHp < T.BreatherTeamHp || downs >= T.BreatherDowns)
                {
                    Fire("breather");
                    SetPhase(Phase.Relax, $"breather teamHp={teamHp:F2} downs={downs}");
                    return;
                }
            }

            switch (Phase)
            {
                case Phase.BuildUp:
                    var ramp = M.Lerp(T.RampFrom, 1f, inPhase / T.RampSeconds);
                    var players = 1f + T.PerExtraPlayer * Math.Max(0, alive.Count - 1);
                    Rate = T.PointsPerSecond * ramp * MathF.Max(0.1f, w.WaveMultiplier) * players;
                    Bank = MathF.Min(T.BankMax, Bank + Rate * dt);
                    if (IParty >= T.PeakEnter) { SetPhase(Phase.Peak, $"I={IParty:F2}"); return; }
                    EvaluateRules(now, w, relax: false);
                    break;
                case Phase.Peak:
                    Rate = 0f;
                    if (inPhase >= _peakLen) SetPhase(Phase.Relax, "peak held");
                    break;
                case Phase.Relax:
                    Rate = 0f;
                    if ((inPhase >= T.RelaxMin && IParty < T.RelaxExit) || inPhase >= T.RelaxMax)
                        SetPhase(Phase.BuildUp, $"relax over I={IParty:F2} after={inPhase:F0}s");
                    else
                        EvaluateRules(now, w, relax: true);
                    break;
            }
        }

        private void SetPhase(Phase to, string reason)
        {
            var from = Phase;
            Phase = to;
            _phaseStart = Now;
            if (to == Phase.Peak) _peakLen = M.Lerp(T.PeakMin, T.PeakMax, (float)_rng.NextDouble());
            Emit(new Dictionary<string, object>
            {
                ["ev"] = "phase", ["t"] = Now, ["from"] = from.ToString(), ["to"] = to.ToString(), ["I"] = IParty, ["reason"] = reason,
            });
            Decide($"phase {from} -> {to} ({reason})");
        }

        // ---- rules (first match spends the budget; per-rule cooldown) ----

        private void EvaluateRules(float now, IWorld w, bool relax)
        {
            var alive = Meter.Alive;
            if (SpawnDisabled || alive.Count == 0) return;

            // Rule 2: campers get swarmed (the only rule allowed in relax; free there).
            var camper = alive.FirstOrDefault(p => p.Stationary);
            if (camper != null && Ready("camper", now))
            {
                var n = _rng.Next(T.CamperMin, T.CamperMax + 1);
                if (!relax) n = Math.Min(n, Affordable(Crab.Crabling));
                if (n >= T.CamperMin || (relax && n > 0))
                {
                    if (Spawn(new SpawnRequest
                        {
                            Rule = "camper", Type = Crab.Crabling, Count = n, Target = camper.Id,
                            MinR = T.CamperMinR, MaxR = T.CamperMaxR, MinD = 2f, MaxD = 3f, Charge = !relax,
                        }, w)) return;
                }
            }
            if (relax) return;

            // Rule 3: projectile builds meet shields.
            if (Ready("shields", now))
            {
                var liveCrabs = CrabTotal(w);
                var shields = w.Count(Crab.ShieldCrab);
                foreach (var p in alive.Where(p => p.ProjectileBuild))
                {
                    if (shields + 2 > MathF.Max(2, T.ShieldShareCap * (liveCrabs + 2))) break;
                    if (w.ShieldsNear(p.Pos, T.ShieldRange) >= T.ShieldKeep) continue;
                    if (Affordable(Crab.ShieldCrab) < 2) break;
                    var dirOk = p.Vel.SqrMagnitude > 0.25f;
                    if (Spawn(new SpawnRequest
                        {
                            Rule = "shields", Type = Crab.ShieldCrab, Count = 2, Target = p.Id,
                            AtPoint = dirOk, Point = p.Pos + p.Vel.Normalized * T.ShieldAhead,
                            MinR = dirOk ? 0.5f : 10f, MaxR = dirOk ? 2f : 14f, MinD = 0f, MaxD = 1f,
                        }, w)) return;
                }
            }

            // Rule 4: flank the line.
            if (Ready("flank", now) && (Spread() > T.FlankSpread || alive.Any(p => p.Speed >= T.FlankLeadSpeed)))
            {
                var dir = PartyDir();
                if (dir.SqrMagnitude > 0.25f)
                {
                    var rear = alive.OrderBy(p => V3.Dot(p.Pos, dir)).First();
                    var n = Math.Min(_rng.Next(T.FlankMin, T.FlankMax + 1), Affordable(Crab.FleshCrab));
                    if (n >= T.FlankMin)
                    {
                        var back = M.Lerp(T.FlankBehindMin, T.FlankBehindMax, (float)_rng.NextDouble());
                        if (Spawn(new SpawnRequest
                            {
                                Rule = "flank", Type = Crab.FleshCrab, Count = n, Target = rear.Id,
                                AtPoint = true, Point = rear.Pos - dir.Normalized * back,
                                MinR = 0.5f, MaxR = 3f, MinD = 1f, MaxD = 2f,
                            }, w)) return;
                    }
                }
            }

            // Rule 5: fast killers get a heavy.
            if (Ready("heavy", now) && _killHistory.Count > 0 && now - _killHistory.Peek().t >= T.HeavyMinHistory)
            {
                var kps = Meter.Players.Values.Sum(p => p.KillRate);
                var sorted = _killHistory.Select(k => k.kps).OrderBy(x => x).ToList();
                var q = sorted[Math.Clamp((int)(T.HeavyQuantile * sorted.Count), 0, sorted.Count - 1)];
                if (kps > 0f && kps >= q && w.Count(Crab.Crablodon) == 0 && Affordable(Crab.Crablodon) >= 1)
                {
                    var top = alive.OrderByDescending(p => p.KillRate).First();
                    if (Spawn(new SpawnRequest
                        {
                            Rule = "heavy", Type = Crab.Crablodon, Count = 1, Target = top.Id,
                            MinR = T.HeavyMinR, MaxR = T.HeavyMaxR,
                        }, w)) return;
                }
            }

            // Rule 6: default mix from the remaining budget.
            if (Ready("default", now) && Bank >= T.DefaultMinBank)
            {
                var target = alive[_rng.Next(alive.Count)];
                var pts = Bank;
                var nC = (int)(pts * T.MixCrabling / Cost(Crab.Crabling));
                var nF = (int)(pts * T.MixFleshCrab / Cost(Crab.FleshCrab));
                var nS = (int)(pts * T.MixShieldCrab / Cost(Crab.ShieldCrab));
                if (w.Count(Crab.ShieldCrab) + nS > T.ShieldShareCap * (CrabTotal(w) + nC + nF + nS)) nS = 0;
                var any = false;
                foreach (var (type, n, minD, maxD) in new[] { (Crab.Crabling, nC, 2f, 4f), (Crab.FleshCrab, nF, 2f, 4f), (Crab.ShieldCrab, nS, 1f, 2f) })
                    if (n > 0)
                        any |= Spawn(new SpawnRequest
                        {
                            Rule = "default", Type = type, Count = n, Target = target.Id,
                            MinR = T.DefaultMinR, MaxR = T.DefaultMaxR, MinD = minD, MaxD = maxD,
                        }, w);
                if (any) Fire("default");
            }
        }

        private bool Ready(string rule, float now) => !_nextAllowed.TryGetValue(rule, out var t) || now >= t;

        private void Fire(string rule)
        {
            _nextAllowed[rule] = Now + T.RuleCooldown;
            RuleFires[rule] = RuleFires.TryGetValue(rule, out var n) ? n + 1 : 1;
        }

        // ---- output ----

        /// <summary>Caps, then <see cref="Execute"/>; charges the bank; logs the call either way.</summary>
        public bool Spawn(SpawnRequest r, IWorld w)
        {
            var line = new Dictionary<string, object>
            {
                ["ev"] = "spawn", ["t"] = Now, ["rule"] = r.Rule, ["type"] = r.Type.ToString(), ["asked"] = r.Count,
                ["target"] = r.Target, ["atPoint"] = r.AtPoint, ["minR"] = r.MinR, ["maxR"] = r.MaxR,
                ["minD"] = r.MinD, ["maxD"] = r.MaxD, ["live"] = w.Total, ["director"] = w.DirectorTotal, ["bank"] = Bank,
            };
            if (!r.IgnoreCaps)
            {
                var max = w.MaxConcurrent;
                var room = Math.Max(T.DirectorMinAllowance, (int)(T.DirectorShareCap * w.Total)) - w.DirectorTotal;
                if (max > 0) room = Math.Min(room, max - w.Total);
                line["maxConcurrent"] = max;
                line["room"] = room;
                r.Count = Math.Min(r.Count, room);
                if (r.Count <= 0) { line["ok"] = false; line["err"] = "cap"; Emit(line); return false; }
            }
            line["count"] = r.Count;
            line["id"] = ++_spawnSeq;

            var ok = Execute(r, line);
            line["ok"] = ok;
            if (ok)
            {
                if (r.Charge) Bank = MathF.Max(0f, Bank - r.Count * Cost(r.Type));
                if (r.Rule != "default") Fire(r.Rule);
                Decide($"spawn #{_spawnSeq} {r.Rule}: {r.Count} {r.Type} -> client {r.Target} r={r.MinR:F0}-{r.MaxR:F0} bank={Bank:F1}");
            }
            Emit(line);
            return ok;
        }

        /// <summary>Director fields of the per-second line; clears the per-second player totals.</summary>
        public Dictionary<string, object> SecondFields() => new Dictionary<string, object>
        {
            ["phase"] = Phase.ToString(), ["I"] = IParty, ["rate"] = Rate, ["bank"] = Bank,
            ["events"] = Meter.Events, ["rules"] = new Dictionary<string, int>(RuleFires), ["players"] = Meter.SecondLines(),
        };

        // ---- helpers ----

        private float Cost(Crab t) => t switch
        {
            Crab.Crabling => T.CostCrabling,
            Crab.FleshCrab => T.CostFleshCrab,
            Crab.ShieldCrab => T.CostShieldCrab,
            Crab.Crablodon => T.CostCrablodon,
            _ => 1f,
        };

        private int Affordable(Crab t) => (int)(Bank / Cost(t));

        private static int CrabTotal(IWorld w) =>
            w.Count(Crab.Crabling) + w.Count(Crab.FleshCrab) + w.Count(Crab.ShieldCrab) + w.Count(Crab.Crablodon);

        private float Spread()
        {
            var a = Meter.Alive;
            var max = 0f;
            for (var i = 0; i < a.Count; i++)
                for (var j = i + 1; j < a.Count; j++)
                    max = MathF.Max(max, V3.Distance(a[i].Pos, a[j].Pos));
            return max;
        }

        private V3 PartyDir()
        {
            var v = V3.Zero;
            foreach (var p in Meter.Alive) v += p.Vel;
            return (v / Math.Max(1, Meter.Alive.Count)).Flat;
        }
    }
}
