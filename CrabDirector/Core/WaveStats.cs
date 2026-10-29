using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SpellBrigadeMods.CrabDirector
{
    /// <summary>
    /// Raw aggregates of one wave, built from the director's own jsonl lines (sec / spawn / phase), so
    /// the game and tools/wave-director (replaying a run log) produce the same summary.
    /// </summary>
    public sealed class WaveStats
    {
        private sealed class P
        {
            public float HpStart = -1, HpEnd, HpMin = float.MaxValue, MaxHp, DmgTaken, Dealt;
            public int Kills, Downs, Samples, ChasingSum, ChasingMax;
            public bool Alive;
            public readonly Dictionary<string, float> Spell = new Dictionary<string, float>();
        }

        private readonly Dictionary<string, P> _players = new Dictionary<string, P>();
        private readonly Dictionary<string, float> _phaseSec = new Dictionary<string, float>();
        private readonly Dictionary<string, int> _phaseEntries = new Dictionary<string, int>();
        private readonly Dictionary<string, Dictionary<string, int>> _spawns = new Dictionary<string, Dictionary<string, int>>();
        private float _t0 = -1, _t1, _iSum, _iMax, _heldSec;
        private int _secs, _liveMax, _directorLiveMax, _liveSum;
        private JsonElement _census, _directorCensus;
        private bool _hasCensus;

        public int Seconds => _secs;

        public void Feed(string jsonLine)
        {
            using var doc = JsonDocument.Parse(jsonLine);
            Feed(doc.RootElement);
        }

        public void Feed(JsonElement e)
        {
            switch (Str(e, "ev"))
            {
                case "sec": Sec(e); break;
                case "phase":
                    var to = Str(e, "to");
                    if (to != null) _phaseEntries[to] = _phaseEntries.GetValueOrDefault(to) + 1;
                    break;
                case "spawn":
                    var rule = Str(e, "rule") ?? "?";
                    if (!_spawns.TryGetValue(rule, out var s)) _spawns[rule] = s = new Dictionary<string, int>();
                    s["calls"] = s.GetValueOrDefault("calls") + 1;
                    if (Bool(e, "ok"))
                    {
                        s["ok"] = s.GetValueOrDefault("ok") + 1;
                        s["enemies"] = s.GetValueOrDefault("enemies") + (int)Num(e, "count");
                        var type = Str(e, "type") ?? "?";
                        s[type] = s.GetValueOrDefault(type) + (int)Num(e, "count");
                    }
                    else s["failed"] = s.GetValueOrDefault("failed") + 1;
                    break;
            }
        }

        private void Sec(JsonElement e)
        {
            var t = Num(e, "t");
            if (_t0 < 0) _t0 = t;
            _t1 = t;
            _secs++;
            var phase = Str(e, "phase") ?? "?";
            _phaseSec[phase] = _phaseSec.GetValueOrDefault(phase) + 1f;
            if (Bool(e, "held")) _heldSec += 1f;
            var i = Num(e, "I");
            _iSum += i;
            _iMax = MathF.Max(_iMax, i);
            var live = (int)Num(e, "live");
            _liveSum += live;
            _liveMax = Math.Max(_liveMax, live);
            _directorLiveMax = Math.Max(_directorLiveMax, (int)Num(e, "directorLive"));
            if (e.TryGetProperty("census", out var c)) { _census = c.Clone(); _hasCensus = true; }
            if (e.TryGetProperty("directorCensus", out var dc)) _directorCensus = dc.Clone();

            if (!e.TryGetProperty("players", out var ps) || ps.ValueKind != JsonValueKind.Array) return;
            foreach (var row in ps.EnumerateArray())
            {
                var id = row.TryGetProperty("id", out var idv) ? idv.ToString() : "?";
                if (!_players.TryGetValue(id, out var p)) _players[id] = p = new P();
                var hp = Num(row, "hp");
                if (p.HpStart < 0) p.HpStart = hp;
                p.HpEnd = hp;
                p.HpMin = MathF.Min(p.HpMin, hp);
                p.MaxHp = Num(row, "maxHp");
                p.Alive = Bool(row, "alive");
                p.DmgTaken += Num(row, "dmgTaken");
                p.Dealt += Num(row, "dealt");
                p.Kills += (int)Num(row, "kills");
                p.Downs += (int)Num(row, "downs");
                var ch = (int)Num(row, "chasing");
                p.ChasingSum += ch;
                p.ChasingMax = Math.Max(p.ChasingMax, ch);
                p.Samples++;
                if (row.TryGetProperty("spell", out var sp) && sp.ValueKind == JsonValueKind.Object)
                    foreach (var kv in sp.EnumerateObject())
                        if (kv.Value.ValueKind == JsonValueKind.Number)
                            p.Spell[kv.Name] = p.Spell.GetValueOrDefault(kv.Name) + kv.Value.GetSingle();
            }
        }

        /// <summary>The summary of the wave so far plus <paramref name="meta"/> (run, wave, next, of, map, crabLand, intent).</summary>
        public Dictionary<string, object> Build(Dictionary<string, object> meta)
        {
            var d = new Dictionary<string, object>(meta)
            {
                ["seconds"] = _secs, ["t0"] = _t0, ["t1"] = _t1,
                ["party"] = new Dictionary<string, object>
                {
                    ["Imean"] = _secs > 0 ? _iSum / _secs : 0f, ["Imax"] = _iMax,
                    ["phaseSeconds"] = new Dictionary<string, float>(_phaseSec), ["phaseEntries"] = new Dictionary<string, int>(_phaseEntries),
                    ["heldSeconds"] = _heldSec, ["liveMean"] = _secs > 0 ? (float)_liveSum / _secs : 0f, ["liveMax"] = _liveMax,
                    ["directorLiveMax"] = _directorLiveMax,
                    ["censusEnd"] = _hasCensus ? (object)_census : null, ["directorCensusEnd"] = _hasCensus ? (object)_directorCensus : null,
                },
                ["players"] = _players.Select(kv => new Dictionary<string, object>
                {
                    ["id"] = kv.Key, ["alive"] = kv.Value.Alive, ["maxHp"] = kv.Value.MaxHp,
                    ["hpStart"] = kv.Value.HpStart, ["hpEnd"] = kv.Value.HpEnd, ["hpMin"] = kv.Value.HpMin == float.MaxValue ? 0f : kv.Value.HpMin,
                    ["dmgTaken"] = kv.Value.DmgTaken, ["dealt"] = kv.Value.Dealt, ["kills"] = kv.Value.Kills, ["downs"] = kv.Value.Downs,
                    ["chasingMean"] = kv.Value.Samples > 0 ? (float)kv.Value.ChasingSum / kv.Value.Samples : 0f, ["chasingMax"] = kv.Value.ChasingMax,
                    ["spell"] = new Dictionary<string, float>(kv.Value.Spell),
                }).ToList(),
            };
            var spawns = new Dictionary<string, Dictionary<string, int>>();
            foreach (var kv in _spawns) spawns[kv.Key] = new Dictionary<string, int>(kv.Value);
            d["spawns"] = spawns;
            return d;
        }

        public void Reset()
        {
            _players.Clear(); _phaseSec.Clear(); _phaseEntries.Clear(); _spawns.Clear();
            _t0 = -1; _t1 = _iSum = _iMax = _heldSec = 0f;
            _secs = _liveMax = _directorLiveMax = _liveSum = 0;
            _hasCensus = false;
        }

        private static string Str(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static float Num(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0f;

        private static bool Bool(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
