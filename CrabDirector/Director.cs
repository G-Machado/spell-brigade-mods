using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using UnityEngine;

namespace SpellBrigadeMods.CrabDirector
{
    internal enum Phase { BuildUp, Peak, Relax }

    /// <summary>Intensity phases, the six Crab Land rules, and spawning through EnemySpawner.SpawnGroupAsync.</summary>
    internal static class Director
    {
        private static Tuning T => CrabDirectorMod.Tuning;
        private static readonly System.Random Rng = new System.Random();

        public static Phase Phase { get; private set; }
        public static float IParty { get; private set; }
        public static float Bank { get; private set; }
        public static bool SpawnDisabled { get; private set; }

        private static float _phaseStart, _peakLen, _rate, _nextSample, _nextSecond, _runStart;
        private static int _spawnFailures, _spawnSeq;
        private static readonly Dictionary<string, float> NextAllowed = new Dictionary<string, float>();
        private static readonly Queue<(float t, float kps)> KillHistory = new Queue<(float, float)>();
        private static readonly List<(int id, Awaitable aw, float t0, float until)> Awaiting = new List<(int, Awaitable, float, float)>();
        private static readonly Dictionary<string, int> RuleFires = new Dictionary<string, int>();
        private static bool _warnedNoEvents;

        // Hook counters (probe 5) and relax throttle
        public static int SpawnRandomCalls, RandomEnemyCalls, SkippedTicks;

        public static void BeginRun()
        {
            Inputs.Reset();
            Census.Reset();
            NextAllowed.Clear();
            KillHistory.Clear();
            Awaiting.Clear();
            RuleFires.Clear();
            SpawnRandomCalls = RandomEnemyCalls = SkippedTicks = 0;
            _spawnFailures = 0;
            SpawnDisabled = false;
            _warnedNoEvents = false;
            Bank = 0f;
            _runStart = _nextSample = _nextSecond = Time.time;
            SetPhase(Phase.BuildUp, "run start");
        }

        // ---- per frame (host, enabled, in a run) ----

        public static void Tick()
        {
            var now = Time.time;
            if (now < _nextSample) return;
            var dt = 1f / T.SampleHz;
            _nextSample = now + dt;

            Inputs.Sample(now, dt);
            IParty = Inputs.Alive.Count == 0 ? 0f : Inputs.Alive.Max(p => p.I);
            TrackKills(now);
            CheckAwaiting(now);
            Step(now, dt);

            if (now >= _nextSecond)
            {
                _nextSecond = now + 1f;
                WriteSecond(now);
            }
        }

        private static void Step(float now, float dt)
        {
            var inPhase = now - _phaseStart;

            // Rule 1, any phase: breather.
            if (Phase != Phase.Relax && Inputs.Alive.Count > 0)
            {
                var teamHp = Inputs.Alive.Average(p => p.HpFrac);
                var downs = Inputs.Players.Values.Sum(p => p.Downs.Count(t => now - t <= T.BreatherWindow));
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
                    var ramp = Mathf.Lerp(T.RampFrom, 1f, Mathf.Clamp01(inPhase / T.RampSeconds));
                    var waveMult = SafeWaveMultiplier();
                    var players = 1f + T.PerExtraPlayer * Mathf.Max(0, Inputs.Alive.Count - 1);
                    _rate = T.PointsPerSecond * ramp * waveMult * players;
                    Bank = Mathf.Min(T.BankMax, Bank + _rate * dt);
                    if (IParty >= T.PeakEnter) { SetPhase(Phase.Peak, $"I={IParty:F2}"); return; }
                    EvaluateRules(now, relax: false);
                    break;
                case Phase.Peak:
                    _rate = 0f;
                    if (inPhase >= _peakLen) SetPhase(Phase.Relax, "peak held");
                    break;
                case Phase.Relax:
                    _rate = 0f;
                    if ((inPhase >= T.RelaxMin && IParty < T.RelaxExit) || inPhase >= T.RelaxMax)
                        SetPhase(Phase.BuildUp, $"relax over I={IParty:F2} after={inPhase:F0}s");
                    else
                        EvaluateRules(now, relax: true);
                    break;
            }
        }

        private static void SetPhase(Phase to, string reason)
        {
            var from = Phase;
            Phase = to;
            _phaseStart = Time.time;
            if (to == Phase.Peak) _peakLen = Mathf.Lerp(T.PeakMin, T.PeakMax, (float)Rng.NextDouble());
            JsonLog.Write(new Dictionary<string, object>
            {
                ["ev"] = "phase", ["t"] = Time.time, ["from"] = from.ToString(), ["to"] = to.ToString(), ["I"] = IParty, ["reason"] = reason,
            });
            CrabDirectorMod.Decision($"phase {from} -> {to} ({reason})");
        }

        // ---- rules (first match spends the budget; per-rule cooldown) ----

        private static void EvaluateRules(float now, bool relax)
        {
            if (SpawnDisabled || Inputs.Alive.Count == 0) return;

            // Rule 2: campers get swarmed (the only rule allowed in relax; free there).
            var camper = Inputs.Alive.FirstOrDefault(p => p.Stationary);
            if (camper != null && Ready("camper", now))
            {
                var n = Rng.Next(T.CamperMin, T.CamperMax + 1);
                if (!relax) n = Mathf.Min(n, Affordable(EnemyType.Crabling));
                if (n >= T.CamperMin || (relax && n > 0))
                {
                    if (Spawn("camper", EnemyType.Crabling, n, camper.Player.GetTransform(), T.CamperMinR, T.CamperMaxR, 2f, 3f,
                            EnemySpawnRotation.TowardsClosestPlayer, camper.Id, charge: !relax)) return;
                }
            }
            if (relax) return;

            // Rule 3: projectile builds meet shields.
            if (Ready("shields", now))
            {
                var liveCrabs = CrabTotal();
                var shields = Census.Count(EnemyType.ShieldCrab);
                foreach (var p in Inputs.Alive.Where(p => p.ProjectileBuild))
                {
                    if (shields + 2 > Mathf.Max(2, T.ShieldShareCap * (liveCrabs + 2))) break;
                    if (ShieldsNear(p.Pos) >= T.ShieldKeep) continue;
                    if (Affordable(EnemyType.ShieldCrab) < 2) break;
                    var origin = Ahead(p, T.ShieldAhead, out var dirOk);
                    if (Spawn("shields", EnemyType.ShieldCrab, 2, origin, dirOk ? 0.5f : 10f, dirOk ? 2f : 14f, 0f, 1f,
                            EnemySpawnRotation.TowardsClosestPlayer, p.Id)) return;
                }
            }

            // Rule 4: flank the line.
            if (Ready("flank", now))
            {
                var spread = Spread();
                var lead = Inputs.Alive.Any(p => p.Speed >= T.FlankLeadSpeed);
                if (spread > T.FlankSpread || lead)
                {
                    var dir = PartyDir();
                    if (dir.sqrMagnitude > 0.25f)
                    {
                        var rear = Inputs.Alive.OrderBy(p => Vector3.Dot(p.Pos, dir)).First();
                        var n = Mathf.Min(Rng.Next(T.FlankMin, T.FlankMax + 1), Affordable(EnemyType.FleshCrab));
                        if (n >= T.FlankMin)
                        {
                            var back = Mathf.Lerp(T.FlankBehindMin, T.FlankBehindMax, (float)Rng.NextDouble());
                            var origin = Anchor(rear.Pos - dir.normalized * back, 5f);
                            if (Spawn("flank", EnemyType.FleshCrab, n, origin, 0.5f, 3f, 1f, 2f,
                                    EnemySpawnRotation.TowardsClosestPlayer, rear.Id)) return;
                        }
                    }
                }
            }

            // Rule 5: fast killers get a heavy.
            if (Ready("heavy", now) && KillHistory.Count > 0 && now - KillHistory.Peek().t >= T.HeavyMinHistory)
            {
                var kps = Inputs.Players.Values.Sum(p => p.KillRate);
                var sorted = KillHistory.Select(k => k.kps).OrderBy(x => x).ToList();
                var q = sorted[Mathf.Clamp((int)(T.HeavyQuantile * sorted.Count), 0, sorted.Count - 1)];
                if (kps > 0f && kps >= q && Census.Count(EnemyType.Crablodon) == 0 && Affordable(EnemyType.Crablodon) >= 1)
                {
                    var top = Inputs.Alive.OrderByDescending(p => p.KillRate).First();
                    if (Spawn("heavy", EnemyType.Crablodon, 1, top.Player.GetTransform(), T.HeavyMinR, T.HeavyMaxR, 0f, 0f,
                            EnemySpawnRotation.TowardsClosestPlayer, top.Id)) return;
                }
            }

            // Rule 6: default mix from the remaining budget.
            if (Ready("default", now) && Bank >= T.DefaultMinBank)
            {
                var target = Inputs.Alive[Rng.Next(Inputs.Alive.Count)];
                var origin = target.Player.GetTransform();
                var pts = Bank;
                var nC = (int)(pts * T.MixCrabling / Cost(EnemyType.Crabling));
                var nF = (int)(pts * T.MixFleshCrab / Cost(EnemyType.FleshCrab));
                var nS = (int)(pts * T.MixShieldCrab / Cost(EnemyType.ShieldCrab));
                if (Census.Count(EnemyType.ShieldCrab) + nS > T.ShieldShareCap * (CrabTotal() + nC + nF + nS)) nS = 0;
                var any = false;
                if (nC > 0) any |= Spawn("default", EnemyType.Crabling, nC, origin, T.DefaultMinR, T.DefaultMaxR, 2f, 4f, EnemySpawnRotation.TowardsClosestPlayer, target.Id);
                if (nF > 0) any |= Spawn("default", EnemyType.FleshCrab, nF, origin, T.DefaultMinR, T.DefaultMaxR, 2f, 4f, EnemySpawnRotation.TowardsClosestPlayer, target.Id);
                if (nS > 0) any |= Spawn("default", EnemyType.ShieldCrab, nS, origin, T.DefaultMinR, T.DefaultMaxR, 1f, 2f, EnemySpawnRotation.TowardsClosestPlayer, target.Id);
                if (any) Fire("default");
            }
        }

        private static bool Ready(string rule, float now) => !NextAllowed.TryGetValue(rule, out var t) || now >= t;

        private static void Fire(string rule)
        {
            NextAllowed[rule] = Time.time + T.RuleCooldown;
            RuleFires[rule] = RuleFires.TryGetValue(rule, out var n) ? n + 1 : 1;
        }

        // ---- output ----

        /// <summary>One group through the game's spawner. Caps first; charges the bank; logs the call either way.</summary>
        internal static bool Spawn(string rule, EnemyType type, int count, Transform origin, float minR, float maxR,
            float minD, float maxD, EnemySpawnRotation rot, ulong target, bool charge = true, bool ignoreCaps = false)
        {
            var es = EnemySpawner.Instance;
            var line = new Dictionary<string, object>
            {
                ["ev"] = "spawn", ["t"] = Time.time, ["rule"] = rule, ["type"] = type.ToString(), ["asked"] = count,
                ["target"] = target, ["minR"] = minR, ["maxR"] = maxR, ["minD"] = minD, ["maxD"] = maxD,
                ["live"] = Census.Total, ["director"] = Census.DirectorTotal, ["bank"] = Bank,
            };
            if (es == null || origin == null) { line["ok"] = false; line["err"] = es == null ? "no spawner" : "no origin"; JsonLog.Write(line); return false; }

            if (!ignoreCaps)
            {
                var max = SafeMaxConcurrent();
                var dirCap = Mathf.Max(T.DirectorMinAllowance, (int)(T.DirectorShareCap * Census.Total));
                var room = dirCap - Census.DirectorTotal;
                if (max > 0) room = Mathf.Min(room, max - Census.Total);
                line["maxConcurrent"] = max;
                line["room"] = room;
                count = Mathf.Min(count, room);
                if (count <= 0) { line["ok"] = false; line["err"] = "cap"; JsonLog.Write(line); return false; }
            }
            line["count"] = count;

            try
            {
                var aw = es.SpawnGroupAsync(new EnemyGroupSpawnConfig(type, count, origin, minR, maxR, minD, maxD, rot));
                Census.ExpectDirector(type, count, Time.time + maxD + 3f);
                if (charge) Bank = Mathf.Max(0f, Bank - count * Cost(type));
                var id = ++_spawnSeq;
                line["id"] = id;
                line["ok"] = true;
                line["awaitable"] = aw != null;
                if (aw != null) Awaiting.Add((id, aw, Time.time, Time.time + maxD + 10f));
                _spawnFailures = 0;
                if (rule != "default") Fire(rule);
                JsonLog.Write(line);
                CrabDirectorMod.Decision($"spawn #{id} {rule}: {count} {type} -> client {target} r={minR:F0}-{maxR:F0} bank={Bank:F1}");
                return true;
            }
            catch (Exception e)
            {
                line["ok"] = false;
                line["err"] = $"{e.GetType().Name}: {e.Message}";
                JsonLog.Write(line);
                CrabDirectorMod.Log.Warning($"spawn {rule} {type} x{count} failed: {e.Message}");
                if (++_spawnFailures >= 3)
                {
                    SpawnDisabled = true;
                    CrabDirectorMod.Log.Error("spawn: 3 failures in a row, director spawns disabled for this run; game waves continue");
                }
                return false;
            }
        }

        /// <summary>Director-owned enemy appeared (census). Crablodons are kept from the unseen-despawn.</summary>
        public static void OnDirectorEnemyStarted(Census.Entry e)
        {
            if (e.Type == EnemyType.Crablodon) e.Enemy.SetOverrideImmuneToDespawning(true);
        }

        private static void CheckAwaiting(float now)
        {
            for (var i = Awaiting.Count - 1; i >= 0; i--)
            {
                var (id, aw, t0, until) = Awaiting[i];
                bool done;
                try { done = aw.IsCompleted; }
                catch (Exception e) { done = false; CrabDirectorMod.Once("awaitable", e); }
                if (!done && now < until) continue;
                JsonLog.Write(new Dictionary<string, object>
                {
                    ["ev"] = "awaited", ["t"] = now, ["id"] = id, ["completed"] = done, ["after"] = now - t0,
                });
                Awaiting.RemoveAt(i);
            }
        }

        // ---- helpers ----

        private static float Cost(EnemyType t) => t switch
        {
            EnemyType.Crabling => T.CostCrabling,
            EnemyType.FleshCrab => T.CostFleshCrab,
            EnemyType.ShieldCrab => T.CostShieldCrab,
            EnemyType.Crablodon => T.CostCrablodon,
            _ => 1f,
        };

        private static int Affordable(EnemyType t) => (int)(Bank / Cost(t));

        private static int CrabTotal() =>
            Census.Count(EnemyType.Crabling) + Census.Count(EnemyType.FleshCrab) +
            Census.Count(EnemyType.ShieldCrab) + Census.Count(EnemyType.Crablodon);

        private static int ShieldsNear(Vector3 pos)
        {
            var n = 0;
            var r2 = T.ShieldRange * T.ShieldRange;
            foreach (var e in Census.Entries)
                if (e.Type == EnemyType.ShieldCrab && e.Enemy != null && (e.Enemy.transform.position - pos).sqrMagnitude <= r2) n++;
            return n;
        }

        private static float Spread()
        {
            var max = 0f;
            for (var i = 0; i < Inputs.Alive.Count; i++)
                for (var j = i + 1; j < Inputs.Alive.Count; j++)
                    max = Mathf.Max(max, Vector3.Distance(Inputs.Alive[i].Pos, Inputs.Alive[j].Pos));
            return max;
        }

        private static Vector3 PartyDir()
        {
            var v = Vector3.zero;
            foreach (var p in Inputs.Alive) v += p.Vel;
            v /= Mathf.Max(1, Inputs.Alive.Count);
            v.y = 0f;
            return v;
        }

        /// <summary>Origin ahead of a moving player (a helper anchor), or the player itself when standing still.</summary>
        private static Transform Ahead(PlayerState p, float dist, out bool dirOk)
        {
            var v = p.Vel;
            v.y = 0f;
            dirOk = v.sqrMagnitude > 0.25f;
            return dirOk ? Anchor(p.Pos + v.normalized * dist, 3f) : p.Player.GetTransform();
        }

        /// <summary>A throwaway Transform for directional spawns; destroyed once the group's duration is over.</summary>
        private static Transform Anchor(Vector3 pos, float life)
        {
            var go = new GameObject("CrabDirectorAnchor");
            go.transform.position = pos;
            UnityEngine.Object.Destroy(go, life);
            return go.transform;
        }

        private static void TrackKills(float now)
        {
            var kps = Inputs.Players.Values.Sum(p => p.KillRate);
            KillHistory.Enqueue((now, kps));
            while (KillHistory.Count > 0 && now - KillHistory.Peek().t > T.HeavyWindow) KillHistory.Dequeue();
        }

        private static float SafeWaveMultiplier()
        {
            try { var rm = RunManager.Instance; return rm != null ? Mathf.Max(0.1f, rm.GetWaveDifficultyMultiplier()) : 1f; }
            catch { return 1f; }
        }

        private static int SafeMaxConcurrent()
        {
            try { var es = EnemySpawner.Instance; return es != null ? es.maxNumberOfConcurrentEnemies : 0; }
            catch { return 0; }
        }

        private static void WriteSecond(float now)
        {
            int wave = -1, cycle = -1, level = -1;
            float progress = -1;
            try
            {
                var rm = RunManager.Instance;
                if (rm != null) { wave = rm.GetCurrentWave(); cycle = rm.GetCurrentCycle(); progress = rm.GetCurrentProgress(); }
                var plm = PartyLevelManager.Instance;
                if (plm != null) level = plm.GetLevel();
            }
            catch (Exception e) { CrabDirectorMod.Once("run context", e); }

            JsonLog.Write(new Dictionary<string, object>
            {
                ["ev"] = "sec", ["t"] = now, ["runT"] = now - _runStart, ["wave"] = wave, ["cycle"] = cycle, ["progress"] = progress,
                ["level"] = level, ["waveMult"] = SafeWaveMultiplier(), ["phase"] = Phase.ToString(), ["I"] = IParty,
                ["rate"] = _rate, ["bank"] = Bank, ["live"] = Census.Total, ["directorLive"] = Census.DirectorTotal,
                ["maxConcurrent"] = SafeMaxConcurrent(), ["census"] = Census.ByType(false), ["directorCensus"] = Census.ByType(true),
                ["started"] = Census.Started, ["destroyed"] = Census.Destroyed,
                ["directorStarted"] = Census.DirectorStarted, ["directorDestroyed"] = Census.DirectorDestroyed,
                ["events"] = Inputs.Events, ["bus"] = Inputs.BusCaptures, ["rules"] = new Dictionary<string, int>(RuleFires),
                ["hookCalls"] = new Dictionary<string, int> { ["spawnRandomEnemy"] = SpawnRandomCalls, ["getRandomEnemyForWave"] = RandomEnemyCalls, ["skipped"] = SkippedTicks },
                ["players"] = Inputs.SecondLines(),
            });

            // Fail loudly when an input collects nothing.
            if (!_warnedNoEvents && now - _runStart > 60f && (Inputs.BusCaptures == 0 || Inputs.Events == 0 || Census.Started == 0))
            {
                _warnedNoEvents = true;
                CrabDirectorMod.Log.Error($"inputs empty after 60 s: bus={Inputs.BusCaptures} events={Inputs.Events} census={Census.Started}");
            }
        }

        // ---- spawner modulation (hooks) ----

        public static bool SpawnRandomEnemyPrefix()
        {
            SpawnRandomCalls++;
            if (!CrabDirectorMod.Running || Phase != Phase.Relax) return true;
            if (Rng.NextDouble() >= T.RelaxSkipChance) return true;
            SkippedTicks++;
            return false;
        }

        public static void GetRandomEnemyForWavePostfix() => RandomEnemyCalls++;
    }
}
