using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SpellBrigadeMods.CrabDirector.Replay
{
    /// <summary>
    /// Offline replay: synthetic players feed the director core at 4 Hz; prints JSONL to stdout:
    /// phase changes, decisions, spawn calls (what SpawnGroupAsync would get), raw per-second state,
    /// and one summary line per scenario.
    /// Usage: dotnet run --project tools/probes/replay -- [idle|kiting|killburst|downed|boss|all] [--rules rules.json] [--seed N] [--every S]
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var name = "all";
            string rules = null;
            var seed = 1;
            var every = 1f;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--rules": rules = args[++i]; break;
                    case "--seed": seed = int.Parse(args[++i]); break;
                    case "--every": every = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                    default: name = args[i]; break;
                }
            }

            var tuning = rules == null ? new Tuning() : Tuning.Load(rules, m => Console.Error.WriteLine(m));
            var picked = name == "all" ? Scenarios.All : Scenarios.All.Where(s => s.Name == name).ToList();
            if (picked.Count == 0)
            {
                Console.Error.WriteLine($"replay: unknown scenario {name}; have: {string.Join(", ", Scenarios.All.Select(s => s.Name))}, all");
                return 2;
            }
            foreach (var s in picked) new Sim(s, tuning, seed, every).Run();
            return 0;
        }
    }

    /// <summary>One synthetic player: where it is, how fast it kills, what it casts, how much it gets chased.</summary>
    internal sealed class Actor
    {
        public ulong Id;
        public Func<float, V3> Pos = _ => V3.Zero;
        public Func<float, float> KillRate = _ => 0.5f;
        public Func<float, float> BaseChasing = _ => 2f;
        public Func<float, float> DamagePerChaser = _ => 0.3f;
        public string Spell = "EnergyZone";
        public float MaxHp = 100f, Regen = 1f, ReviveAfter = 5f;
    }

    internal sealed class Scenario
    {
        public string Name;
        public float Duration = 180f;
        /// <summary>CrabLand boss fight window (BossFight._bossHealth set) and the brood it adds to the map.</summary>
        public float BossFrom = -1f, BossTo = -1f;
        public int Brood = 15;
        public List<Actor> Actors = new List<Actor>();
    }

    internal static class Scenarios
    {
        private static V3 Circle(float t, float r, float speed) =>
            new V3(r * MathF.Cos(t * speed / r), 0, r * MathF.Sin(t * speed / r));

        public static readonly List<Scenario> All = new List<Scenario>
        {
            // Stands still the whole run, area spell: camper swarms expected.
            new Scenario { Name = "idle", Actors = { new Actor { Id = 0 } } },

            // Circles at 5 m/s with a Rifle: flank and shields expected.
            new Scenario
            {
                Name = "kiting",
                Actors = { new Actor { Id = 0, Pos = t => Circle(t, 20f, 5f), KillRate = _ => 1f, BaseChasing = _ => 4f, Spell = "Rifle" } },
            },

            // Walks at 1.5 m/s, kills 0.5/s, then 5/s from 60 to 90 s: a heavy expected in the burst.
            new Scenario
            {
                Name = "killburst",
                Actors =
                {
                    new Actor
                    {
                        Id = 0, Pos = t => new V3(1.5f * t, 0, 0), Spell = "Laser",
                        KillRate = t => t >= 60f && t < 90f ? 5f : 0.5f,
                    },
                },
            },

            // Two players 25 m apart; swarmed hard from 30 to 60 s until they go down: breather / relax expected.
            new Scenario
            {
                Name = "downed",
                Actors =
                {
                    new Actor
                    {
                        Id = 0, Pos = t => new V3(2f * t, 0, 0),
                        BaseChasing = t => t >= 30f && t < 60f ? 12f : 3f, DamagePerChaser = t => t >= 30f && t < 60f ? 1.2f : 0.3f,
                    },
                    new Actor
                    {
                        Id = 1, Pos = t => new V3(2f * t, 0, 25f), Spell = "Orb",
                        BaseChasing = t => t >= 30f && t < 60f ? 10f : 3f, DamagePerChaser = t => t >= 30f && t < 60f ? 1.2f : 0.3f,
                    },
                },
            },

            // Crab Land boss from 60 to 120 s (CrabLand spawns its brood): no director spawns expected in the window.
            new Scenario
            {
                Name = "boss", BossFrom = 60f, BossTo = 120f,
                Actors =
                {
                    new Actor
                    {
                        Id = 0, Pos = t => Circle(t, 12f, 3f), KillRate = _ => 1.5f, Spell = "Rifle",
                        BaseChasing = t => t >= 60f && t < 120f ? 8f : 3f,
                    },
                },
            },
        };
    }

    /// <summary>Synthetic map: a steady pool of game enemies plus the crabs the director spawned.</summary>
    internal sealed class Sim : IWorld
    {
        private sealed class Spawned { public Crab Type; public V3 Pos; public ulong Target; }

        private const float Dt = 0.25f;
        private const int GameEnemies = 25, Max = 80;

        private readonly Scenario _s;
        private readonly Tuning _t;
        private readonly float _every;
        private readonly Random _rng;
        private readonly DirectorCore _core = new DirectorCore();
        private readonly List<Spawned> _crabs = new List<Spawned>();
        private readonly Dictionary<ulong, float> _hp = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> _downUntil = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> _killDebt = new Dictionary<ulong, float>();
        private readonly Dictionary<string, int> _spawnsByRule = new Dictionary<string, int>();
        private readonly Dictionary<string, float> _phaseTime = new Dictionary<string, float>();
        private float _now, _maxI;
        private int _downs, _spawnsInBoss;

        public Sim(Scenario s, Tuning t, int seed, float every)
        {
            _s = s;
            _t = t;
            _every = every;
            _rng = new Random(seed);
            _core.Emit = Print;
            _core.Decide = msg => Print(new Dictionary<string, object> { ["ev"] = "decision", ["t"] = _now, ["msg"] = msg });
            _core.Execute = Execute;
        }

        // IWorld
        public bool BossFight => _now >= _s.BossFrom && _now < _s.BossTo;
        public int Total => GameEnemies + (BossFight ? _s.Brood : 0) + _crabs.Count;
        public int DirectorTotal => _crabs.Count;
        public int MaxConcurrent => Max;
        public float WaveMultiplier => 1f + _now / 300f;
        public int Count(Crab type) => _crabs.Count(c => c.Type == type);
        public int ShieldsNear(V3 pos, float range) => _crabs.Count(c => c.Type == Crab.ShieldCrab && V3.Distance(c.Pos, pos) <= range);

        private void Print(Dictionary<string, object> line)
        {
            line["scenario"] = _s.Name;
            Console.WriteLine(JsonSerializer.Serialize(line));
        }

        private bool Execute(SpawnRequest r, Dictionary<string, object> line)
        {
            var center = r.AtPoint ? r.Point : PosOf(r.Target);
            for (var i = 0; i < r.Count; i++)
            {
                var a = (float)(_rng.NextDouble() * Math.PI * 2);
                var d = r.MinR + (float)_rng.NextDouble() * (r.MaxR - r.MinR);
                _crabs.Add(new Spawned { Type = r.Type, Pos = center + new V3(MathF.Cos(a) * d, 0, MathF.Sin(a) * d), Target = r.Target });
            }
            line["center"] = new[] { center.X, center.Z };
            _spawnsByRule[r.Rule] = (_spawnsByRule.TryGetValue(r.Rule, out var n) ? n : 0) + r.Count;
            if (BossFight) _spawnsInBoss += r.Count;
            return true;
        }

        private V3 PosOf(ulong id) => _s.Actors.First(a => a.Id == id).Pos(_now);

        public void Run()
        {
            _core.Reset(_t, 0f, _rng.Next());
            foreach (var a in _s.Actors) _hp[a.Id] = a.MaxHp;
            var nextSec = 0f;
            var samples = new List<RawSample>();

            for (_now = 0f; _now <= _s.Duration; _now += Dt)
            {
                samples.Clear();
                foreach (var a in _s.Actors)
                {
                    if (_downUntil.TryGetValue(a.Id, out var until))
                    {
                        if (_now < until) continue;
                        _downUntil.Remove(a.Id);
                        _hp[a.Id] = a.MaxHp;
                    }

                    var pos = a.Pos(_now);
                    // Director crabs close in on their target: count those as chasing.
                    var chasing = (int)a.BaseChasing(_now) + _crabs.Count(c => c.Target == a.Id);
                    var dmg = chasing * a.DamagePerChaser(_now) * Dt * (float)(0.5 + _rng.NextDouble());
                    if (dmg > 0.01f)
                    {
                        _core.Meter.DamageTaken(a.Id, dmg, _now);
                        _hp[a.Id] -= dmg;
                    }
                    _hp[a.Id] = MathF.Min(a.MaxHp, _hp[a.Id] + a.Regen * Dt);
                    if (_hp[a.Id] <= 0f)
                    {
                        _core.Meter.Down(a.Id, _now);
                        _downs++;
                        _downUntil[a.Id] = _now + a.ReviveAfter;
                        continue;
                    }

                    // Kills: whole kills from a running debt; each kill removes one of that player's director crabs half the time.
                    _killDebt[a.Id] = (_killDebt.TryGetValue(a.Id, out var k) ? k : 0f) + a.KillRate(_now) * Dt;
                    while (_killDebt[a.Id] >= 1f)
                    {
                        _killDebt[a.Id] -= 1f;
                        _core.Meter.Kill(a.Id);
                        _core.Meter.Dealt(a.Id, 20f);
                        _core.Meter.Spell(a.Id, a.Spell, 20f);
                        var mine = _crabs.FindIndex(c => c.Target == a.Id);
                        if (mine >= 0 && _rng.NextDouble() < 0.5) _crabs.RemoveAt(mine);
                    }

                    samples.Add(new RawSample { Id = a.Id, Hp = _hp[a.Id], MaxHp = a.MaxHp, Chasing = chasing, Pos = pos });
                }

                _core.Meter.Sample(_now, Dt, samples);
                _core.Step(_now, Dt, this);
                _maxI = MathF.Max(_maxI, _core.IParty);
                var ph = _core.Held ? "Held" : _core.Phase.ToString();
                _phaseTime[ph] = (_phaseTime.TryGetValue(ph, out var pt) ? pt : 0f) + Dt;

                if (_now >= nextSec)
                {
                    nextSec += _every;
                    var line = new Dictionary<string, object>
                    {
                        ["ev"] = "sec", ["t"] = _now, ["waveMult"] = WaveMultiplier, ["live"] = Total, ["directorLive"] = DirectorTotal,
                        ["census"] = _crabs.GroupBy(c => c.Type.ToString()).ToDictionary(g => g.Key, g => g.Count()),
                    };
                    foreach (var kv in _core.SecondFields()) line[kv.Key] = kv.Value;
                    Print(line);
                }
            }

            Print(new Dictionary<string, object>
            {
                ["ev"] = "summary", ["duration"] = _s.Duration, ["maxI"] = _maxI, ["downs"] = _downs,
                ["spawnedByRule"] = _spawnsByRule, ["ruleFires"] = new Dictionary<string, int>(_core.RuleFires),
                ["phaseSeconds"] = _phaseTime, ["directorLiveAtEnd"] = DirectorTotal, ["spawnsDuringBoss"] = _spawnsInBoss,
            });
        }
    }
}
