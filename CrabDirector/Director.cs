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
        public static int SpawnRandomCalls, RandomEnemyCalls, SkippedTicks;

        public static void BeginRun(Tuning t)
        {
            Inputs.Reset();
            Census.Reset();
            Awaiting.Clear();
            SpawnRandomCalls = RandomEnemyCalls = SkippedTicks = 0;
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
                var aw = es.SpawnGroupAsync(new EnemyGroupSpawnConfig((EnemyType)(int)r.Type, r.Count, origin,
                    r.MinR, r.MaxR, r.MinD, r.MaxD, EnemySpawnRotation.TowardsClosestPlayer));
                Census.ExpectDirector((EnemyType)(int)r.Type, r.Count, Time.time + r.MaxD + 3f);
                line["awaitable"] = aw != null;
                if (aw != null) Awaiting.Add(((int)line["id"], aw, Time.time, Time.time + r.MaxD + 10f));
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
                ["hookCalls"] = new Dictionary<string, int> { ["spawnRandomEnemy"] = SpawnRandomCalls, ["getRandomEnemyForWave"] = RandomEnemyCalls, ["skipped"] = SkippedTicks },
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

        public static bool SpawnRandomEnemyPrefix()
        {
            SpawnRandomCalls++;
            if (!CrabDirectorMod.Running || Core.Held || Core.Phase != Phase.Relax) return true;
            if (Rng.NextDouble() >= Core.T.RelaxSkipChance) return true;
            SkippedTicks++;
            return false;
        }

        public static void GetRandomEnemyForWavePostfix() => RandomEnemyCalls++;

        private sealed class GameWorld : IWorld
        {
            public int Total => Census.Total;
            public int DirectorTotal => Census.DirectorTotal;
            public bool BossFight => CrabDirectorMod.CrabLandBossFight();
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
