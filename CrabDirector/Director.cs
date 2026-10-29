using System;
using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;

namespace SpellBrigadeMods.CrabDirector
{
    /// <summary>Game side of the director: drives <see cref="DirectorCore"/> at 4 Hz and executes its spawns through EnemySpawner.SpawnGroupAsync.</summary>
    internal static class Director
    {
        public static readonly DirectorCore Core = new DirectorCore
        {
            Execute = Execute,
            Emit = JsonLog.Write,
            Decide = CrabDirectorMod.Decision,
        };

        private static readonly GameWorld World = new GameWorld();
        private static readonly System.Random Rng = new System.Random();
        private static float _nextSample, _nextSecond, _runStart;
        private static int _spawnFailures;
        private static bool _warnedNoEvents;
        private static readonly List<(int id, Awaitable aw, float t0, float until)> Awaiting = new List<(int, Awaitable, float, float)>();

        // Hook counters (probe 5) and relax throttle
        public static int RandomTicks, RandomSpawns, RandomEnemyCalls, SkippedTicks;

        public static void BeginRun(Tuning t)
        {
            Inputs.Reset();
            Census.Reset();
            Awaiting.Clear();
            RandomTicks = RandomSpawns = RandomEnemyCalls = SkippedTicks = 0;
            _spawnFailures = 0;
            _warnedNoEvents = false;
            _runStart = _nextSample = _nextSecond = Time.time;
            Core.Reset(t, Time.time, Environment.TickCount);
        }

        public static void Tick()
        {
            var now = Time.time;
            if (now < _nextSample) return;
            var dt = 1f / Core.T.SampleHz;
            _nextSample = now + dt;

            Inputs.Sample(now, dt);
            CheckAwaiting(now);
            Core.Step(now, dt, World);
            WaveLink.Tick(now);

            if (now >= _nextSecond)
            {
                _nextSecond = now + 1f;
                WriteSecond(now);
            }
        }

        // ---- output ----

        private static bool Execute(SpawnRequest r, Dictionary<string, object> line)
        {
            var es = EnemySpawner.Instance;
            var origin = r.AtPoint ? Anchor(r.Point, r.MaxD + 3f) : Inputs.TransformOf(r.Target);
            if (es == null || origin == null) { line["err"] = es == null ? "no spawner" : "no origin"; return false; }
            try
            {
                var groups = new Dictionary<EnemyType, int>();
                if (r.Type == Crab.Native)
                {
                    if (!PickNative(es, r.Count, groups)) { line["err"] = "no wave mix"; return false; }
                    var mix = new Dictionary<string, int>();
                    foreach (var kv in groups) mix[kv.Key.ToString()] = kv.Value;
                    line["mix"] = mix;
                }
                else groups[(EnemyType)(int)r.Type] = r.Count;
                var any = false;
                foreach (var kv in groups)
                {
                    var aw = es.SpawnGroupAsync(new EnemyGroupSpawnConfig(kv.Key, kv.Value, origin,
                        r.MinR, r.MaxR, r.MinD, r.MaxD, EnemySpawnRotation.TowardsClosestPlayer));
                    Census.ExpectDirector(kv.Key, kv.Value, Time.time + r.MaxD + 3f);
                    any |= aw != null;
                    if (aw != null) Awaiting.Add(((int)line["id"], aw, Time.time, Time.time + r.MaxD + 10f));
                }
                line["awaitable"] = any;
                _spawnFailures = 0;
                return true;
            }
            catch (Exception e)
            {
                line["err"] = $"{e.GetType().Name}: {e.Message}";
                CrabDirectorMod.Log.Warning($"spawn {r.Rule} {r.Type} x{r.Count} failed: {e.Message}");
                if (++_spawnFailures >= 3)
                {
                    Core.SpawnDisabled = true;
                    CrabDirectorMod.Log.Error("spawn: 3 failures in a row, director spawns disabled for this run; game waves continue");
                }
                return false;
            }
        }

        /// <summary>Spike helper: one group around a player, no caps, no budget.</summary>
        public static bool DebugSpawn(Crab type, int count, ulong target) =>
            Core.Spawn(new SpawnRequest
            {
                Rule = "debug", Type = type, Count = count, Target = target,
                MinR = 6f, MaxR = 10f, MinD = 0f, MaxD = 1f, Charge = false, IgnoreCaps = true,
            }, World);

        /// <summary>
        /// Weighted pick of <paramref name="count"/> enemies from the current wave's
        /// <c>EnemyProbabilitiesForSelectedWorld</c> (world variants applied by the game). Reads the
        /// list only; never calls GetRandomEnemyForWave, so it can't re-enter the throttle hooks.
        /// </summary>
        private static bool PickNative(EnemySpawner es, int count, Dictionary<EnemyType, int> groups)
        {
            var wave = es.currentWave;
            var list = wave?.EnemyProbabilitiesForSelectedWorld;
            if (list == null || list.Count == 0) return false;
            var types = new List<(EnemyType t, float p)>();
            float sum = 0f;
            for (var i = 0; i < list.Count; i++)
            {
                var ep = list[i];
                if (ep == null || ep.Probability <= 0f) continue;
                types.Add((ep.Type, ep.Probability));
                sum += ep.Probability;
            }
            if (sum <= 0f) return false;
            for (var n = 0; n < count; n++)
            {
                var x = (float)Rng.NextDouble() * sum;
                var pick = types[types.Count - 1].t;
                foreach (var (t, p) in types) { if ((x -= p) < 0f) { pick = t; break; } }
                groups[pick] = groups.TryGetValue(pick, out var c) ? c + 1 : 1;
            }
            return true;
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

        /// <summary>A throwaway Transform for directional spawns; destroyed once the group's duration is over.</summary>
        private static Transform Anchor(V3 pos, float life)
        {
            var go = new GameObject("CrabDirectorAnchor");
            go.transform.position = new Vector3(pos.X, pos.Y, pos.Z);
            UnityEngine.Object.Destroy(go, life);
            return go.transform;
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

            var line = new Dictionary<string, object>
            {
                ["ev"] = "sec", ["t"] = now, ["runT"] = now - _runStart, ["wave"] = wave, ["cycle"] = cycle, ["progress"] = progress,
                ["level"] = level, ["waveMult"] = World.WaveMultiplier, ["live"] = Census.Total, ["directorLive"] = Census.DirectorTotal,
                ["maxConcurrent"] = World.MaxConcurrent, ["census"] = Census.ByType(false), ["directorCensus"] = Census.ByType(true),
                ["started"] = Census.Started, ["destroyed"] = Census.Destroyed,
                ["directorStarted"] = Census.DirectorStarted, ["directorDestroyed"] = Census.DirectorDestroyed, ["bus"] = Inputs.BusCaptures,
                ["hookCalls"] = new Dictionary<string, int> { ["randomTick"] = RandomTicks, ["randomSpawn"] = RandomSpawns, ["getRandomEnemyForWave"] = RandomEnemyCalls, ["skipped"] = SkippedTicks },
            };
            foreach (var kv in Core.SecondFields()) line[kv.Key] = kv.Value;
            JsonLog.Write(line);

            // Fail loudly when an input collects nothing.
            if (!_warnedNoEvents && now - _runStart > 60f && (Inputs.BusCaptures == 0 || Core.Meter.Events == 0 || Census.Started == 0))
            {
                _warnedNoEvents = true;
                CrabDirectorMod.Log.Error($"inputs empty after 60 s: bus={Inputs.BusCaptures} events={Core.Meter.Events} census={Census.Started}");
            }
        }

        // ---- spawner modulation (hooks) ----

        // SpawnRandomEnemy is inlined into the random-spawn coroutine (run 1: 0 calls), and the coroutine
        // passes GetRandomEnemyForWave's result to SpawnEnemy with no null check. So the throttle skips
        // SpawnEnemy itself, but only inside the coroutine's MoveNext: SpawnEnemy is also called by
        // SpawnEliteEnemy and by SpawnGroupAsync (the director's own output). The coroutine drops
        // SpawnEnemy's return value, so returning null there is safe (disassembly, 2026-10-29).
        private static int _randomTickFrame = -1;

        public static void RandomTickPrefix()
        {
            RandomTicks++;
            _randomTickFrame = Time.frameCount;
        }

        public static void RandomTickPostfix() => _randomTickFrame = -1;

        public static bool SpawnEnemyPrefix(ref Unity.Netcode.NetworkObject __result)
        {
            if (_randomTickFrame != Time.frameCount) return true;
            RandomSpawns++;
            if (!CrabDirectorMod.Running || Core.Held || Core.Phase != Phase.Relax) return true;
            if (Rng.NextDouble() >= Core.T.RelaxSkipChance) return true;
            SkippedTicks++;
            __result = null;
            return false;
        }

        public static void GetRandomEnemyForWavePostfix() => RandomEnemyCalls++;

        private sealed class GameWorld : IWorld
        {
            public int Total => Census.Total;
            public int DirectorTotal => Census.DirectorTotal;
            public bool BossFight => CrabDirectorMod.CrabLandBossFight() || NativeBoss.Active;
            public bool CrabRules => CrabDirectorMod.CrabLandActive();
            public int Count(Crab type) => Census.Count((EnemyType)(int)type);

            public int MaxConcurrent
            {
                get
                {
                    try { var es = EnemySpawner.Instance; return es != null ? es.maxNumberOfConcurrentEnemies : 0; }
                    catch { return 0; }
                }
            }

            public float WaveMultiplier
            {
                get
                {
                    try { var rm = RunManager.Instance; return rm != null ? rm.GetWaveDifficultyMultiplier() : 1f; }
                    catch { return 1f; }
                }
            }

            public int ShieldsNear(V3 pos, float range)
            {
                var n = 0;
                var p = new Vector3(pos.X, pos.Y, pos.Z);
                foreach (var e in Census.Entries)
                    if (e.Type == EnemyType.ShieldCrab && e.Enemy != null && (e.Enemy.transform.position - p).sqrMagnitude <= range * range) n++;
                return n;
            }
        }
    }
}
