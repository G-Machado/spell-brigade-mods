using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MelonLoader;
using Il2Cpp;
using SpellBrigadeMods.Shared;
using UnityEngine;

namespace SpellBrigadeMods.CrabLand
{
    // TEMPORARY live probe for sea elements: raw hit values to a fresh file per run. Delete this file to remove it.
    internal static partial class ThemeElements
    {
        private static StreamWriter _probe;
        private static int _probeHits, _probePopups;

        static partial void ProbeHooks(Hooks h) =>
            h.Add<EnemyHealthContainer>("ShowDamagePopup", postfix: P(nameof(ProbePopupPostfix)));

        static partial void ProbeReset()
        {
            if (_probe != null)
            {
                var msg = $"probe: closed hits={_probeHits} popups={_probePopups}";
                if (_probeHits == 0) CrabLandMod.Log.Error(msg + " (collected nothing)"); else CrabLandMod.Log.Msg(msg);
                _probe.Dispose();
            }
            _probeHits = _probePopups = 0;
            var path = Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "UserData", $"sea-elements-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _probe = new StreamWriter(path, false) { AutoFlush = true };
            _probe.WriteLine("t\tkind\ttarget\telement\tamount\thitIsCritical\tcritChance\tcritDamage\tisServer\tfrozen");
            CrabLandMod.Log.Msg($"probe: writing {path}");
            MelonCoroutines.Start(SelfTestWatch(++_probeRun));
        }

        private static int _probeBurns, _probeRun;

        static partial void ProbeBurn() => _probeBurns++;

        private static string TriggerPath => Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "UserData", "sea-elements-selftest.txt");

        private static void P2(string line) { _probe?.WriteLine("selftest\t" + line); CrabLandMod.Log.Msg("selftest: " + line); }

        // Waits for UserData/sea-elements-selftest.txt, then drives hits through the game's own TryToDamage/Chill.
        private static IEnumerator SelfTestWatch(int run)
        {
            while (run == _probeRun)
            {
                yield return new WaitForSecondsRealtime(1f);
                if (!File.Exists(TriggerPath)) continue;
                File.Delete(TriggerPath);
                yield return SelfTest();
            }
        }

        private static PlayerDamageInfo Info(ElementType e, float dmg) =>
            new PlayerDamageInfo(0UL) { Element = e, Damage = dmg, CriticalChance = 0f, CriticalDamage = 200f };

        private static List<EnemyHealthContainer> Targets()
        {
            var list = new List<EnemyHealthContainer>();
            foreach (var h in UnityEngine.Object.FindObjectsOfType<EnemyHealthContainer>())
                if (h != null && h.gameObject.activeInHierarchy && !h.IsHealthZero() && h.GetComponentInParent<PetrifiedEnemy>() == null) list.Add(h);
            return list;
        }

        private static IEnumerator SelfTest()
        {
            P2($"start timeScale={Time.timeScale} on={On}");
            Time.timeScale = 1f;
            var t = Targets();
            P2($"targets={t.Count}");
            if (t.Count < 4) { CrabLandMod.Log.Error("selftest: fewer than 4 enemies, nothing tested"); yield break; }

            // a: fire
            var a = t[0]; var h0 = a.CurrentHealth; var burns0 = _probeBurns;
            a.TryToDamage(100f, Info(ElementType.Fire, 100f), false);
            a.TryToDamage(100f, Info(ElementType.FireDouble, 100f), false);
            var feh = a.GetComponentInParent<FireEffectHandler>() ?? a.GetComponentInChildren<FireEffectHandler>();
            P2($"fire target={a.gameObject.name} hp {h0} -> {a.CurrentHealth} burnsBlocked={_probeBurns - burns0} fireHandler={(feh != null)}");

            // b: lightning (popup row follows with DisplayDamage and IsCritical)
            var b = t[1]; var h1 = b.CurrentHealth;
            b.TryToDamage(100f, Info(ElementType.Lightning, 100f), false);
            P2($"lightning target={b.gameObject.name} hp {h1} -> {b.CurrentHealth} (expect -200 x resist, crit popup)");
            var hn = b.CurrentHealth;
            b.TryToDamage(100f, Info(ElementType.None, 100f), false);
            P2($"control None target={b.gameObject.name} hp {hn} -> {b.CurrentHealth}");

            // c: freeze, alive
            var c = t[2];
            var ice = c.GetComponentInParent<IceEffectHandler>() ?? c.GetComponentInChildren<IceEffectHandler>();
            P2($"ice target={c.gameObject.name} iceHandler={(ice != null)}");
            if (ice != null)
            {
                for (var i = 0; i < CrabLandMod.SeaFreezeStacks.Value; i++)
                {
                    ice.Chill(1f, 0.3f, Info(ElementType.Ice, 1f), c);
                    yield return new WaitForSeconds(0.1f);
                }
                var fsh = ice.freezeStateHandler;
                for (var s = 0; s < 5; s++)
                {
                    var r = c.GetComponentInChildren<Renderer>();
                    var mpb = new MaterialPropertyBlock();
                    if (r != null) r.GetPropertyBlock(mpb);
                    P2($"freeze t={s * 1f} frozen={fsh?.IsFrozen} pos={c.transform.position} dark={(r != null && _darkId > 0 ? mpb.GetFloat(_darkId) : -1f)} tint={(_tintId > 0 ? mpb.GetColor(_tintId).ToString() : "-")}");
                    yield return new WaitForSeconds(1f);
                }
            }

            // c2: freeze then kill: must unfreeze on death
            var d = t[3];
            if (d == null || d.IsHealthZero()) { foreach (var x in Targets()) if (x != c) d = x; }   // targets die while the freeze runs
            var ice2 = d == null ? null : d.GetComponentInParent<IceEffectHandler>() ?? d.GetComponentInChildren<IceEffectHandler>();
            if (ice2 != null)
            {
                for (var i = 0; i < CrabLandMod.SeaFreezeStacks.Value; i++) ice2.Chill(1f, 0.3f, Info(ElementType.Ice, 1f), d);
                var fsh2 = ice2.freezeStateHandler;
                P2($"death-freeze target={d.gameObject.name} frozen={fsh2?.IsFrozen}");
                d.TryToDamage(1e7f, Info(ElementType.None, 1e7f), false);
                yield return new WaitForSeconds(0.5f);
                P2($"death-freeze after kill zero={(d != null && d.IsHealthZero())} active={(d != null && d.gameObject.activeInHierarchy)} frozen={fsh2?.IsFrozen} chillsTracked={Chills.Count}");
            }

            // egg: break one laid egg through TryToDamage
            PetrifiedEnemy egg = null;
            for (var w = 0; w < 90 && egg == null; w++)
            {
                var live = (IList)typeof(Eggs).GetField("Live", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                foreach (var o in live)
                {
                    var st = (PetrifiedEnemy)o.GetType().GetField("Statue").GetValue(o);
                    if (st != null && st.healthContainer != null && !st.healthContainer.IsHealthZero()) { egg = st; break; }
                }
                if (egg == null) yield return new WaitForSeconds(1f);
            }
            if (egg == null) { P2("egg none laid within 90 s"); }
            else
            {
                var pos = egg.transform.position; var hc = egg.healthContainer; var eh0 = hc.CurrentHealth;
                var before = new HashSet<int>();
                foreach (var e in UnityEngine.Object.FindObjectsOfType<EnemyHealthContainer>()) before.Add(e.GetInstanceID());
                P2($"egg id={egg.GetInstanceID()} name={egg.name} pos={pos} hp={eh0}");
                hc.TryToDamage(hc.CurrentHealth + 1000f, Info(ElementType.None, hc.CurrentHealth + 1000f), false);
                P2($"egg hp after={(hc != null ? hc.CurrentHealth : -1f)}");
                yield return new WaitForSeconds(2f);
                var spawned = new List<string>();
                foreach (var e in UnityEngine.Object.FindObjectsOfType<EnemyHealthContainer>())
                    if (!before.Contains(e.GetInstanceID()) && Vector3.Distance(e.transform.position, pos) < 4f) spawned.Add($"{e.gameObject.name}@{Vector3.Distance(e.transform.position, pos):F1}");
                P2($"egg spawned within 2s near: [{string.Join(",", spawned)}]");
            }
            P2($"end timeScale={Time.timeScale}");
        }

        static partial void ProbeHit(HealthContainer target, float amount, DamageInfo damageInfo, PlayerDamageInfo player, bool hitIsCritical)
        {
            if (_probe == null) return;
            _probeHits++;
            var frozen = target.GetComponent<FreezeStateHandler>();
            _probe.WriteLine($"{Time.time:F2}\thit\t{target.gameObject.name}\t{damageInfo.Element}\t{amount}\t{hitIsCritical}\t{player?.CriticalChance}\t{player?.CriticalDamage}\t{target.IsServer}\t{(frozen != null ? frozen.IsFrozen.ToString() : "-")}");
        }

        public static void ProbePopupPostfix(EnemyHealthContainer __instance, HealthRemovalData healthRemovalData, DamageInfo damageInfo)
        {
            if (_probe == null || !On || healthRemovalData == null) return;
            _probePopups++;
            _probe.WriteLine($"{Time.time:F2}\tpopup\t{__instance.gameObject.name}\t{damageInfo?.Element}\t{healthRemovalData.DisplayDamage}\t{healthRemovalData.IsCritical}\t\t\t{__instance.IsServer}\t");
        }
    }
}
