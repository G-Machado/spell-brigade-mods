using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2Cpp;
using MelonLoader.Utils;
using UnityEngine;

namespace SpellBrigadeMods.CrabDirector
{
    /// <summary>
    /// Wave director link (docs/director.md "Wave director"): at each RunManager.StartWave, writes the
    /// last wave's summary to <c>UserData/CrabDirector/wave/</c>; once a second, applies
    /// <c>intent.json</c> if it is newer than the last one read. The game never waits on it.
    /// </summary>
    internal static class WaveLink
    {
        internal static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "CrabDirector", "wave");
        private static string IntentPath => Path.Combine(Dir, "intent.json");

        private static readonly WaveStats Stats = new WaveStats();
        private static string _run, _map;
        private static bool _crabLand;
        private static int _pendingOf = -1, _endedWave = -1;
        private static float _nextPoll, _summaryAt = -1f;
        private static DateTime _lastMtime;

        /// <summary>Time.time when the current intent was applied (-1: defaults).</summary>
        internal static float AppliedAt { get; private set; } = -1f;

        public static void BeginRun(string run, string map, bool crabLand)
        {
            _run = run; _map = map; _crabLand = crabLand;
            Stats.Reset();
            _pendingOf = -1;
            _endedWave = -1;
            _summaryAt = AppliedAt = -1f;
            _nextPoll = 0f;
            try { Directory.CreateDirectory(Dir); _lastMtime = File.Exists(IntentPath) ? File.GetLastWriteTimeUtc(IntentPath) : DateTime.MinValue; }
            catch (Exception e) { CrabDirectorMod.Once("wave dir", e); }
        }

        /// <summary>Every jsonl line the director writes (JsonLog.OnLine).</summary>
        public static void OnLine(string json)
        {
            try { Stats.Feed(json); }
            catch (Exception e) { CrabDirectorMod.Once("wave stats", e); }
        }

        public static void StartWavePrefix(int numberOfWaves)
        {
            if (CrabDirectorMod.Running) _pendingOf = numberOfWaves;
        }

        public static void Tick(float now)
        {
            if (_pendingOf >= 0)
            {
                var of = _pendingOf;
                _pendingOf = -1;
                try { WriteSummary(now, of); }
                catch (Exception e) { CrabDirectorMod.Once("wave summary", e); }
            }
            if (now < _nextPoll) return;
            _nextPoll = now + 1f;
            try { Poll(now); }
            catch (Exception e) { CrabDirectorMod.Once("wave intent", e); }
        }

        private static void WriteSummary(float now, int of)
        {
            // Read a frame after the prefix, so CurrentWave has moved on (assumed; the raw value is logged).
            var current = RunManager.Instance != null ? RunManager.Instance.GetCurrentWave() : -1;
            var core = Director.Core;
            var summary = Stats.Build(new Dictionary<string, object>
            {
                ["run"] = _run, ["ended"] = _endedWave, ["next"] = current, ["of"] = of, ["map"] = _map, ["crabLand"] = _crabLand,
                ["written"] = DateTime.UtcNow.ToString("o"),
                ["intent"] = JsonSerializer.Deserialize<JsonElement>(core.Intent.ToJson()),
                ["now"] = new Dictionary<string, object> { ["phase"] = core.Phase.ToString(), ["I"] = core.IParty, ["bank"] = core.Bank, ["held"] = core.Held },
            });
            var seconds = Stats.Seconds;
            Stats.Reset();
            _endedWave = current;

            var path = Path.Combine(Dir, $"summary-{_run}-{current:D2}.json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(summary));
            File.Move(tmp, path, overwrite: true);
            _summaryAt = now;
            JsonLog.Write(new Dictionary<string, object>
            {
                ["ev"] = "wave", ["t"] = now, ["currentWave"] = current, ["of"] = of, ["seconds"] = seconds, ["summary"] = Path.GetFileName(path),
            });
            CrabDirectorMod.Decision($"wave {current}/{of}: summary {Path.GetFileName(path)} ({seconds} s)");
        }

        private static void Poll(float now)
        {
            if (!File.Exists(IntentPath)) return;
            var mtime = File.GetLastWriteTimeUtc(IntentPath);
            if (mtime <= _lastMtime) return;
            _lastMtime = mtime;

            var line = new Dictionary<string, object> { ["ev"] = "intent", ["t"] = now, ["sinceSummary"] = _summaryAt >= 0 ? now - _summaryAt : -1f };
            Intent raw;
            try { raw = Intent.Parse(File.ReadAllText(IntentPath)); }
            catch (Exception e) { raw = null; line["err"] = $"unreadable: {e.Message}"; }
            if (raw != null && raw.Run != _run) line["err"] = $"stale: run {raw.Run ?? "null"} != {_run}";
            else if (raw == null && !line.ContainsKey("err")) line["err"] = "empty";

            if (line.ContainsKey("err"))
            {
                line["applied"] = false;
                JsonLog.Write(line);
                CrabDirectorMod.Decision($"intent ignored ({line["err"]}); keeping {Director.Core.Intent.Source}/{Director.Core.Intent.Theme}");
                return;
            }

            var notes = new List<string>();
            var c = raw.Clamped(notes);
            Director.Core.Intent = c;
            AppliedAt = now;
            line["applied"] = true;
            line["wave"] = c.Wave;
            line["source"] = c.Source;
            line["theme"] = c.Theme;
            line["reason"] = c.Reason;
            line["budget"] = c.BudgetMul;
            line["relax"] = c.RelaxMul;
            line["mix"] = c.Mix;
            line["mixUsed"] = _crabLand && c.Mix != null;
            line["rules"] = c.Rules;
            line["clamped"] = notes;
            JsonLog.Write(line);
            CrabDirectorMod.Decision($"intent {c.Source} wave {c.Wave}: {c.Theme} budget={c.BudgetMul:F2} relax={c.RelaxMul:F2}{(notes.Count > 0 ? " clamped: " + string.Join("; ", notes) : "")}");
        }
    }
}
