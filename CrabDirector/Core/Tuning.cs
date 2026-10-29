using System;
using System.IO;
using System.Text.Json;

namespace SpellBrigadeMods.CrabDirector
{
    /// <summary>
    /// Every threshold and cost the director uses: placeholders until playtests. Read from
    /// <c>UserData/CrabDirector/rules.json</c> at each run start (written with these defaults when
    /// missing), so tuning is a config edit, not a rebuild.
    /// </summary>
    public sealed class Tuning
    {
        // Inputs
        public float SampleHz = 4f;
        public float EmaTau = 3f;
        public float StationarySpeed = 1f;
        public float StationarySeconds = 4f;
        public float ProjectileShare = 0.6f;
        public float ProjectileWindow = 20f;

        // Intensity meter
        public float DamageGain = 1f;
        public float ChasingGain = 0.01f;
        public float DownGain = 0.5f;
        public float LowHp = 0.3f;
        public float LowHpGain = 0.05f;
        public float Decay = 0.04f;
        public float DecayDelay = 2f;
        // Chip-damage tolerance (0 = off): damage/s below the deadband adds nothing and doesn't stop the
        // decay; single hits under DecayIgnoreHitsUnder don't stop the decay.
        public float DamageDeadbandDps = 0f;
        public float DecayIgnoreHitsUnder = 0f;

        // Phases
        public float PeakEnter = 0.85f;
        public float PeakMin = 3f, PeakMax = 5f;
        public float RelaxMin = 20f, RelaxMax = 45f, RelaxExit = 0.25f;
        public float RelaxSkipChance = 0.7f;
        public float RampFrom = 0.3f, RampSeconds = 40f;

        // Budget (crab points)
        public float PointsPerSecond = 2f;
        public float BankMax = 30f;
        public float PerExtraPlayer = 0.5f;
        public float CostCrabling = 1f, CostFleshCrab = 3f, CostShieldCrab = 4f, CostCrablodon = 10f;

        // Rules
        public float RuleCooldown = 6f;
        public float BreatherTeamHp = 0.35f, BreatherWindow = 20f;
        public int BreatherDowns = 2;
        public int CamperMin = 8, CamperMax = 14;
        public float CamperMinR = 6f, CamperMaxR = 10f;
        public float ShieldRange = 15f, ShieldShareCap = 0.25f, ShieldAhead = 12f;
        public int ShieldKeep = 2;
        public float FlankSpread = 20f, FlankLeadSpeed = 3f, FlankBehindMin = 12f, FlankBehindMax = 16f;
        public int FlankMin = 3, FlankMax = 5;
        public float HeavyQuantile = 0.75f, HeavyWindow = 60f, HeavyMinHistory = 30f, HeavyMinR = 14f, HeavyMaxR = 18f;
        public float DefaultMinBank = 6f, DefaultMinR = 10f, DefaultMaxR = 16f;
        public float MixCrabling = 0.7f, MixFleshCrab = 0.2f, MixShieldCrab = 0.1f;

        // Caps
        public float DirectorShareCap = 0.4f;
        public int DirectorMinAllowance = 12;

        // Crab Land coexistence: no director spawns or throttling while CrabLand's boss fight runs.
        public bool BossBackOff = true;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            IncludeFields = true, WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        };

        public static Tuning Load(string path, Action<string> log)
        {
            try
            {
                if (!File.Exists(path))
                {
                    var d = new Tuning();
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, JsonSerializer.Serialize(d, Json));
                    log($"rules: wrote defaults to {path}");
                    return d;
                }
                var t = JsonSerializer.Deserialize<Tuning>(File.ReadAllText(path), Json) ?? new Tuning();
                log($"rules: loaded {path}");
                return t;
            }
            catch (Exception e)
            {
                log($"rules: {path} unreadable ({e.Message}); using defaults");
                return new Tuning();
            }
        }
    }
}
