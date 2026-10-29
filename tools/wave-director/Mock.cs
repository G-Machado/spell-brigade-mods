using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace SpellBrigadeMods.CrabDirector.WaveDirector
{
    /// <summary>Rule-based intents (no key): ease off after a hard wave, push after an easy one.</summary>
    internal static class Mock
    {
        public static Intent Decide(JsonObject s)
        {
            var players = s["players"]?.AsArray().Select(p => p.AsObject()).ToList() ?? new List<JsonObject>();
            var party = s["party"]?.AsObject();
            var iMean = F(party?["Imean"]);
            var downs = players.Sum(p => F(p["downs"]));
            var minHp = players.Count == 0 ? 1f : players.Min(p => F(p["maxHp"]) > 0 ? F(p["hpMin"]) / F(p["maxHp"]) : 1f);
            var crabLand = s["crabLand"]?.GetValue<bool>() ?? false;
            var why = $"Imean={iMean:F2} minHp={minHp:F2} downs={downs:F0}";

            if (downs > 0 || minHp < 0.35f || iMean > 0.6f)
                return new Intent
                {
                    Source = "mock", Theme = "ease off", Reason = "hard wave: " + why, Budget = 0.75f, Relax = 1.4f,
                    Rules = new Dictionary<string, bool> { ["heavy"] = false },
                    Mix = crabLand ? new Dictionary<string, float> { ["Crabling"] = 0.8f, ["FleshCrab"] = 0.15f, ["ShieldCrab"] = 0.05f } : null,
                };
            if (iMean < 0.25f && minHp > 0.7f)
                return new Intent
                {
                    Source = "mock", Theme = "push", Reason = "easy wave: " + why, Budget = 1.3f, Relax = 0.7f,
                    Mix = crabLand ? new Dictionary<string, float> { ["Crabling"] = 0.5f, ["FleshCrab"] = 0.3f, ["ShieldCrab"] = 0.2f } : null,
                };
            return new Intent { Source = "mock", Theme = "steady", Reason = "even wave: " + why, Budget = 1f, Relax = 1f };
        }

        private static float F(JsonNode n)
        {
            try { return n == null ? 0f : (float)n.GetValue<double>(); }
            catch (InvalidOperationException) { return 0f; }
        }
    }
}
