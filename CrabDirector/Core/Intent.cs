using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpellBrigadeMods.CrabDirector
{
    /// <summary>
    /// Wave-level intent from the wave director (docs/director.md "Wave director"): set once per wave,
    /// every field optional and clamped here; the rules stay the per-frame executor. Shared with
    /// tools/wave-director, which writes it as <c>UserData/CrabDirector/wave/intent.json</c>.
    /// </summary>
    public sealed class Intent
    {
        public const float BudgetMin = 0.5f, BudgetMax = 1.5f, RelaxMinScale = 0.5f, RelaxMaxScale = 2f;
        public static readonly string[] RuleNames = { "camper", "shields", "flank", "heavy" };

        /// <summary>The summary's run id this intent answers; an intent for another run is stale.</summary>
        [JsonPropertyName("run")] public string Run { get; set; }
        /// <summary>The wave the intent is for (the summary's <c>next</c>).</summary>
        [JsonPropertyName("wave")] public int Wave { get; set; }
        /// <summary>claude / mock; "defaults" when nothing has been applied.</summary>
        [JsonPropertyName("source")] public string Source { get; set; }
        [JsonPropertyName("theme")] public string Theme { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; }
        /// <summary>Multiplies the build-up point rate, 0.5–1.5.</summary>
        [JsonPropertyName("budget")] public float? Budget { get; set; }
        /// <summary>Multiplies RelaxMin and RelaxMax, 0.5–2.</summary>
        [JsonPropertyName("relax")] public float? Relax { get; set; }
        /// <summary>Default-rule crab mix weights (Crab Land only); normalised to sum 1.</summary>
        [JsonPropertyName("mix")] public Dictionary<string, float> Mix { get; set; }
        /// <summary>camper / shields / flank / heavy on or off; missing = on. Breather and default always run.</summary>
        [JsonPropertyName("rules")] public Dictionary<string, bool> Rules { get; set; }

        public static Intent Defaults() => new Intent { Source = "defaults", Theme = "defaults" };

        public float BudgetMul => Budget ?? 1f;
        public float RelaxMul => Relax ?? 1f;
        public bool RuleOn(string rule) => Rules == null || !Rules.TryGetValue(rule, out var on) || on;

        /// <summary>The intent's weight for <paramref name="crab"/> (0 if the mix leaves it out), or the rules.json one with no mix.</summary>
        public float MixOr(string crab, float fallback) =>
            Mix == null ? fallback : Mix.TryGetValue(crab, out var w) ? w : 0f;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static Intent Parse(string json) => JsonSerializer.Deserialize<Intent>(json, Json);
        public string ToJson() => JsonSerializer.Serialize(this, Json);

        /// <summary>A clamped copy; <paramref name="notes"/> lists every value that was changed or dropped.</summary>
        public Intent Clamped(List<string> notes)
        {
            var c = new Intent
            {
                Run = Run, Wave = Wave,
                Source = Cut(Source, 16, "source", notes) ?? "unknown",
                Theme = Cut(Theme, 40, "theme", notes),
                Reason = Cut(Reason, 200, "reason", notes),
                Budget = Range(Budget, BudgetMin, BudgetMax, "budget", notes),
                Relax = Range(Relax, RelaxMinScale, RelaxMaxScale, "relax", notes),
            };
            if (Mix != null)
            {
                var m = new Dictionary<string, float>();
                foreach (var kv in Mix)
                {
                    if (kv.Key != nameof(Crab.Crabling) && kv.Key != nameof(Crab.FleshCrab) && kv.Key != nameof(Crab.ShieldCrab))
                    { notes.Add($"mix.{kv.Key} dropped"); continue; }
                    var w = float.IsFinite(kv.Value) ? Math.Clamp(kv.Value, 0f, 1f) : 0f;
                    if (w != kv.Value) notes.Add($"mix.{kv.Key} {kv.Value} -> {w}");
                    m[kv.Key] = w;
                }
                var sum = 0f;
                foreach (var w in m.Values) sum += w;
                if (sum <= 0f) notes.Add("mix all zero, dropped");
                else
                {
                    c.Mix = new Dictionary<string, float>();
                    foreach (var kv in m) c.Mix[kv.Key] = kv.Value / sum;
                }
            }
            if (Rules != null)
            {
                c.Rules = new Dictionary<string, bool>();
                foreach (var kv in Rules)
                    if (Array.IndexOf(RuleNames, kv.Key) >= 0) c.Rules[kv.Key] = kv.Value;
                    else notes.Add($"rules.{kv.Key} dropped");
            }
            return c;
        }

        private static string Cut(string s, int max, string name, List<string> notes)
        {
            if (s == null || s.Length <= max) return s;
            notes.Add($"{name} cut to {max}");
            return s.Substring(0, max);
        }

        private static float? Range(float? v, float lo, float hi, string name, List<string> notes)
        {
            if (v == null) return null;
            if (!float.IsFinite(v.Value)) { notes.Add($"{name} not finite, dropped"); return null; }
            var c = Math.Clamp(v.Value, lo, hi);
            if (c != v.Value) notes.Add($"{name} {v.Value} -> {c}");
            return c;
        }
    }
}
