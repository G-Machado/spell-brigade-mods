using System;
using Il2Cpp;
using UnityEngine;

namespace SpellBrigadeMods.CrabDirector
{
    /// <summary>
    /// A native boss is up: the run's <c>EndBossObjective</c> holds a boss <c>HealthContainer</c> that isn't
    /// at zero. Polled once a second (FindObjectOfType), for the boss back-off on every map.
    /// </summary>
    internal static class NativeBoss
    {
        private static float _next;
        private static bool _active, _failed;

        public static bool Active
        {
            get
            {
                if (_failed || Time.time < _next) return _active;
                _next = Time.time + 1f;
                try
                {
                    var obj = UnityEngine.Object.FindObjectOfType<EndBossObjective>();
                    var hc = obj != null ? obj.bossHealthContainer : null;
                    var now = hc != null && !hc.IsHealthZero();
                    if (now != _active)
                        CrabDirectorMod.Log.Msg($"native boss: active={now} objective={(obj != null)} health={(hc != null ? hc.CurrentHealth : -1f)}");
                    _active = now;
                }
                catch (Exception e)
                {
                    _failed = true;
                    _active = false;
                    CrabDirectorMod.Log.Error($"native boss: check failed, back-off covers CrabLand's boss only: {e.GetType().Name}: {e.Message}");
                }
                return _active;
            }
        }
    }
}
