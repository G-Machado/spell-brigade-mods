using System;
using System.Collections.Generic;

// Core/: the director's decisions in plain C# (no Unity, no interop), so tools/probes/replay can
// drive them offline. The mod feeds it samples and events and executes its spawn requests.
namespace SpellBrigadeMods.CrabDirector
{
    public struct V3
    {
        public float X, Y, Z;
        public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static readonly V3 Zero = new V3(0, 0, 0);
        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator *(V3 a, float k) => new V3(a.X * k, a.Y * k, a.Z * k);
        public static V3 operator /(V3 a, float k) => new V3(a.X / k, a.Y / k, a.Z / k);
        public static float Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static float Distance(V3 a, V3 b) => (a - b).Magnitude;
        public static V3 Lerp(V3 a, V3 b, float t) => a + (b - a) * t;
        public float SqrMagnitude => X * X + Y * Y + Z * Z;
        public float Magnitude => MathF.Sqrt(SqrMagnitude);
        public V3 Normalized => Magnitude > 1e-5f ? this / Magnitude : Zero;
        public V3 Flat => new V3(X, 0, Z);
    }

    /// <summary>The crabs the director spawns; values are the game's EnemyType values.</summary>
    public enum Crab { Crabling = 0, FleshCrab = 1, ShieldCrab = 5, Crablodon = 7 }

    public enum Phase { BuildUp, Peak, Relax }

    /// <summary>What the director needs to know about the enemies on the map.</summary>
    public interface IWorld
    {
        int Total { get; }
        int DirectorTotal { get; }
        int MaxConcurrent { get; }
        float WaveMultiplier { get; }
        int Count(Crab type);
        int ShieldsNear(V3 pos, float range);
        /// <summary>CrabLand's boss fight is running (it spawns its own brood).</summary>
        bool BossFight { get; }
    }

    /// <summary>One SpawnGroupAsync call: ring around the target player, or around a point (a helper anchor in game).</summary>
    public sealed class SpawnRequest
    {
        public string Rule;
        public Crab Type;
        public int Count;
        public ulong Target;
        public bool AtPoint;
        public V3 Point;
        public float MinR, MaxR, MinD, MaxD;
        public bool Charge = true, IgnoreCaps;
    }

    /// <summary>One player's raw sample (4 Hz).</summary>
    public struct RawSample
    {
        public ulong Id;
        public float Hp, MaxHp;
        public int Chasing;
        public V3 Pos;
    }

    public sealed class PlayerState
    {
        public ulong Id;
        public bool Alive;

        // Sampled
        public float Hp, MaxHp;
        public int Chasing;
        public V3 Pos, Vel;
        public bool HasPos;
        public float Speed, StillFor;

        // Smoothed rates (EMA, per second)
        public float DmgTakenRate, KillRate, DealtRate;

        // Event accumulators, cleared every sample
        public float AccDmgTaken, AccDealt, AccSpellProj, AccSpellTotal;
        public int AccKills, AccDowns;

        // Per-second raw totals for the log, cleared every second
        public float SecDmgTaken, SecDealt;
        public int SecKills, SecDowns;
        public readonly Dictionary<string, float> SecSpell = new Dictionary<string, float>();

        // Derived
        public float I;
        public float LastHit = -999f, LastPressure = -999f;
        public bool AccHit;
        public float ProjShare;
        public bool Stationary, ProjectileBuild;
        public readonly Queue<(float t, float proj, float total)> SpellWindow = new Queue<(float, float, float)>();
        public readonly List<float> Downs = new List<float>();

        public float HpFrac => MaxHp > 0 ? Hp / MaxHp : 1f;
    }

    internal static class M
    {
        public static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
    }
}
