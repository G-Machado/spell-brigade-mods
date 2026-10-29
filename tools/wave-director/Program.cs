using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace SpellBrigadeMods.CrabDirector.WaveDirector
{
    /// <summary>
    /// Watches <c>UserData/CrabDirector/wave/</c> for the mod's wave summaries and answers each with
    /// <c>intent.json</c> (written atomically). Claude when ANTHROPIC_API_KEY is set, rule-based
    /// otherwise. The key is read from the environment only and never written anywhere.
    ///
    ///   wave-director [--dir D] [--model M] [--mock] [--dry-run] [--once]
    ///   wave-director --replay run.jsonl [--mock|--dry-run]   offline: one summary per wave of a run log
    /// </summary>
    internal static class Program
    {
        private const string DefaultModel = "claude-sonnet-5-5";

        private static int Main(string[] args)
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            string dir = null, model = DefaultModel, replay = null;
            bool mock = false, dry = false, once = false;
            for (var i = 0; i < args.Length; i++)
                switch (args[i])
                {
                    case "--dir": dir = args[++i]; break;
                    case "--model": model = args[++i]; break;
                    case "--replay": replay = args[++i]; break;
                    case "--mock": mock = true; break;
                    case "--dry-run": dry = true; break;
                    case "--once": once = true; break;
                    default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
                }

            var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!mock && !dry && string.IsNullOrEmpty(key))
            {
                Console.WriteLine("no ANTHROPIC_API_KEY: mock mode");
                mock = true;
            }
            var claude = mock ? null : new Claude(key, model);
            Console.WriteLine($"mode={(dry ? "dry-run" : mock ? "mock" : "claude")} model={(mock ? "-" : model)}");

            if (replay != null) return Replay(replay, claude, dry);

            dir ??= Path.Combine(Environment.GetEnvironmentVariable("SPELL_BRIGADE_DIR")
                ?? @"C:\Program Files (x86)\Steam\steamapps\common\The Spell Brigade", "UserData", "CrabDirector", "wave");
            Directory.CreateDirectory(dir);
            Console.WriteLine($"watching {dir}");
            return Watch(dir, claude, dry, once);
        }

        // ---- live: newest summary wins ----

        private static int Watch(string dir, Claude claude, bool dry, bool once)
        {
            var calls = Path.Combine(dir, "director-calls.jsonl");
            var seen = new HashSet<string>(Directory.GetFiles(dir, "summary-*.json").Select(Path.GetFileName));
            if (once && seen.Count > 0) seen.Remove(Directory.GetFiles(dir, "summary-*.json").OrderBy(File.GetLastWriteTimeUtc).Select(Path.GetFileName).Last());
            while (true)
            {
                var fresh = Directory.GetFiles(dir, "summary-*.json").Where(f => !seen.Contains(Path.GetFileName(f)))
                    .OrderBy(File.GetLastWriteTimeUtc).ToList();
                foreach (var f in fresh) seen.Add(Path.GetFileName(f));
                if (fresh.Count > 1) Console.WriteLine($"skipping {fresh.Count - 1} older summaries");
                if (fresh.Count > 0)
                {
                    var path = fresh.Last();
                    try
                    {
                        var summary = JsonNode.Parse(File.ReadAllText(path)).AsObject();
                        var (intent, log) = Decide(summary, claude, dry);
                        log["summary"] = Path.GetFileName(path);
                        File.AppendAllText(calls, log.ToJsonString() + "\n");
                        if (intent != null && !dry) WriteAtomic(Path.Combine(dir, "intent.json"), intent.ToJson());
                        Console.WriteLine($"{Path.GetFileName(path)} -> {Describe(intent)} {log["ms"]}ms");
                    }
                    catch (Exception e) { Console.Error.WriteLine($"{Path.GetFileName(path)}: {e.Message}"); }
                    if (once) return 0;
                }
                Thread.Sleep(500);
            }
        }

        // ---- offline: rebuild per-wave summaries from a run log ----

        private static int Replay(string log, Claude claude, bool dry)
        {
            var run = Path.GetFileNameWithoutExtension(log);
            var stats = new WaveStats();
            int wave = -1, waves = 0;
            string map = "?";
            var crabLand = false;
            var last = Intent.Defaults();
            foreach (var line in File.ReadLines(log))
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                var ev = e.TryGetProperty("ev", out var v) ? v.GetString() : null;
                if (ev == "run")
                {
                    map = e.TryGetProperty("scene", out var s) ? s.GetString() : "?";
                    crabLand = e.TryGetProperty("crabLand", out var c) && c.ValueKind == JsonValueKind.True;
                }
                // Old logs have no "wave" line: a wave starts where the sec line's wave changes.
                if (ev == "sec" && e.TryGetProperty("wave", out var w) && w.GetInt32() != wave)
                {
                    var next = w.GetInt32();
                    if (wave >= 0 && stats.Seconds > 0)
                    {
                        var summary = JsonSerializer.SerializeToNode(stats.Build(new Dictionary<string, object>
                        {
                            ["run"] = run, ["ended"] = wave, ["next"] = next, ["of"] = -1, ["map"] = map, ["crabLand"] = crabLand,
                            ["intent"] = JsonSerializer.Deserialize<JsonElement>(last.ToJson()),
                        })).AsObject();
                        var (intent, call) = Decide(summary, claude, dry);
                        if (intent != null) last = intent.Clamped(new List<string>());
                        Console.WriteLine($"wave {wave}->{next} ({stats.Seconds}s) -> {Describe(intent)} {call.ToJsonString()}");
                        waves++;
                    }
                    stats.Reset();
                    wave = next;
                }
                stats.Feed(e);
            }
            Console.WriteLine($"replayed {waves} wave boundaries from {log}");
            if (waves == 0) { Console.Error.WriteLine("no wave boundary in the log"); return 1; }
            return 0;
        }

        private static (Intent intent, JsonObject log) Decide(JsonObject summary, Claude claude, bool dry)
        {
            var sw = Stopwatch.StartNew();
            var log = new JsonObject { ["at"] = DateTime.UtcNow.ToString("o") };
            Intent intent;
            if (dry)
            {
                var body = Claude.Body(Claude.DryModel, summary);
                log["source"] = "dry-run";
                log["requestBytes"] = body.Length;
                log["userBytes"] = summary.ToJsonString().Length;
                log["systemChars"] = Prompt.System.Length;
                intent = null;
            }
            else if (claude == null)
            {
                intent = Mock.Decide(summary);
                log["source"] = "mock";
            }
            else
            {
                intent = claude.Decide(summary, log);
                log["source"] = "claude";
            }
            if (intent != null)
            {
                intent.Run = summary["run"]?.GetValue<string>();
                intent.Wave = summary["next"]?.GetValue<int>() ?? -1;
                var notes = new List<string>();
                intent = intent.Clamped(notes);
                if (notes.Count > 0) log["clamped"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray());
                log["intent"] = JsonNode.Parse(intent.ToJson());
            }
            log["ms"] = sw.ElapsedMilliseconds;
            return (intent, log);
        }

        private static string Describe(Intent i) =>
            i == null ? "(no intent)" : $"{i.Source}:{i.Theme} budget={i.BudgetMul:F2} relax={i.RelaxMul:F2}" +
                (i.Mix != null ? " mix=" + string.Join(",", i.Mix.Select(kv => $"{kv.Key}:{kv.Value:F2}")) : "") +
                (i.Rules != null && i.Rules.Count > 0 ? " rules=" + string.Join(",", i.Rules.Select(kv => $"{kv.Key}:{(kv.Value ? "on" : "off")}")) : "");

        private static void WriteAtomic(string path, string text)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
