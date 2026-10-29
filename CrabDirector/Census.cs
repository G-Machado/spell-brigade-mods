using System;
using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;

namespace SpellBrigadeMods.CrabDirector
{
    /// <summary>
    /// Live enemies by type (postfix Enemy.Start, prefix Enemy.OnDestroy), host only. An enemy of a
    /// type the director just asked for is counted as the director's while that request is pending.
    /// </summary>
    internal static class Census
    {
        internal sealed class Entry
        {
            public Enemy Enemy;
            public EnemyType Type;
            public bool Director;
        }

        private const int TypeSlots = 64;
        private static readonly Dictionary<IntPtr, Entry> Live = new Dictionary<IntPtr, Entry>();
        private static readonly int[] Pending = new int[TypeSlots];
        private static readonly float[] PendingUntil = new float[TypeSlots];

        public static int Total => Live.Count;
        public static int DirectorTotal { get; private set; }
        public static int Started, Destroyed, DirectorStarted, DirectorDestroyed;

        public static IEnumerable<Entry> Entries => Live.Values;

        public static void Reset()
        {
            Live.Clear();
            Array.Clear(Pending, 0, TypeSlots);
            DirectorTotal = Started = Destroyed = DirectorStarted = DirectorDestroyed = 0;
        }

        public static void ExpectDirector(EnemyType type, int count, float until)
        {
            var i = (int)type;
            if (i < 0 || i >= TypeSlots) return;
            Pending[i] += count;
            PendingUntil[i] = Mathf.Max(PendingUntil[i], until);
        }

        public static int Count(EnemyType type, bool directorOnly = false)
        {
            var n = 0;
            foreach (var e in Live.Values)
                if (e.Type == type && (!directorOnly || e.Director)) n++;
            return n;
        }

        public static Dictionary<string, int> ByType(bool directorOnly)
        {
            var d = new Dictionary<string, int>();
            foreach (var e in Live.Values)
            {
                if (directorOnly && !e.Director) continue;
                var k = e.Type.ToString();
                d[k] = d.TryGetValue(k, out var n) ? n + 1 : 1;
            }
            return d;
        }

        public static void StartPostfix(Enemy __instance)
        {
            try
            {
                if (!CrabDirectorMod.IsHostOrSolo() || __instance == null) return;
                var type = __instance.EnemyType;
                var e = new Entry { Enemy = __instance, Type = type };
                var i = (int)type;
                if (i >= 0 && i < TypeSlots && Pending[i] > 0)
                {
                    if (Time.time <= PendingUntil[i])
                    {
                        Pending[i]--;
                        e.Director = true;
                    }
                    else Pending[i] = 0;
                }
                Live[__instance.Pointer] = e;
                Started++;
                if (e.Director)
                {
                    DirectorTotal++;
                    DirectorStarted++;
                    Director.OnDirectorEnemyStarted(e);
                }
            }
            catch (Exception ex) { CrabDirectorMod.Once("census start", ex); }
        }

        public static void OnDestroyPrefix(Enemy __instance)
        {
            try
            {
                if (__instance == null || !Live.TryGetValue(__instance.Pointer, out var e)) return;
                Live.Remove(__instance.Pointer);
                Destroyed++;
                if (e.Director)
                {
                    DirectorTotal--;
                    DirectorDestroyed++;
                }
            }
            catch (Exception ex) { CrabDirectorMod.Once("census destroy", ex); }
        }
    }
}
