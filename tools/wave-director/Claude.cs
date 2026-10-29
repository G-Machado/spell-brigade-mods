using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace SpellBrigadeMods.CrabDirector.WaveDirector
{
    /// <summary>
    /// One Messages API call per summary: the intent comes back as a forced set_intent tool call. The
    /// tool and the system prompt are the cached prefix; the summary is the only per-call content.
    /// </summary>
    internal sealed class Claude
    {
        public const string DryModel = "claude-sonnet-5-5";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly string _key, _model;

        public Claude(string key, string model) { _key = key; _model = model; }

        private const string Schema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""theme"": { ""type"": ""string"", ""maxLength"": 40 },
    ""reason"": { ""type"": ""string"", ""maxLength"": 200 },
    ""budget"": { ""type"": ""number"", ""minimum"": 0.5, ""maximum"": 1.5 },
    ""relax"": { ""type"": ""number"", ""minimum"": 0.5, ""maximum"": 2 },
    ""mix"": { ""type"": ""object"", ""additionalProperties"": false, ""properties"": {
      ""Crabling"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 1 },
      ""FleshCrab"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 1 },
      ""ShieldCrab"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 1 } } },
    ""rules"": { ""type"": ""object"", ""additionalProperties"": false, ""properties"": {
      ""camper"": { ""type"": ""boolean"" }, ""shields"": { ""type"": ""boolean"" },
      ""flank"": { ""type"": ""boolean"" }, ""heavy"": { ""type"": ""boolean"" } } }
  },
  ""required"": [""theme"", ""reason""]
}";

        public static string Body(string model, JsonObject summary) => new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 400,
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = "set_intent",
                ["description"] = "Set the Crab Director's intent for the next wave. Every field except theme and reason is optional; leave a field out to keep the default.",
                ["input_schema"] = JsonNode.Parse(Schema),
            }),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "set_intent" },
            // One breakpoint at the end of the system prompt caches tools + system.
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text", ["text"] = Prompt.System, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            }),
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "Summary of the wave that just ended:\n" + summary.ToJsonString(),
            }),
        }.ToJsonString();

        /// <summary>The intent, or null on any failure (the game keeps its last intent). Usage and latency go into <paramref name="log"/>.</summary>
        public Intent Decide(JsonObject summary, JsonObject log)
        {
            log["model"] = _model;
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
                {
                    Content = new StringContent(Body(_model, summary), Encoding.UTF8, "application/json"),
                };
                req.Headers.Add("x-api-key", _key);
                req.Headers.Add("anthropic-version", "2023-06-01");
                using var res = Http.Send(req);
                var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                log["apiMs"] = sw.ElapsedMilliseconds;
                log["status"] = (int)res.StatusCode;
                var root = JsonNode.Parse(text).AsObject();
                if (root["usage"] is JsonObject u) log["usage"] = JsonNode.Parse(u.ToJsonString());
                if (!res.IsSuccessStatusCode)
                {
                    log["err"] = root["error"]?["message"]?.GetValue<string>() ?? text;
                    return null;
                }
                log["stop"] = root["stop_reason"]?.GetValue<string>();
                foreach (var block in root["content"].AsArray())
                    if (block["type"]?.GetValue<string>() == "tool_use" && block["name"]?.GetValue<string>() == "set_intent")
                    {
                        var intent = Intent.Parse(block["input"].ToJsonString());
                        intent.Source = "claude";
                        return intent;
                    }
                log["err"] = "no set_intent tool_use block";
                return null;
            }
            catch (Exception e)
            {
                log["apiMs"] = sw.ElapsedMilliseconds;
                log["err"] = $"{e.GetType().Name}: {e.Message}";
                return null;
            }
        }
    }
}
