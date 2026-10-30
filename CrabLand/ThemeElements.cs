using System;
using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using SpellBrigadeMods.Shared;
using UnityEngine;

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>
    /// Sea elements (Crab Land with the sea theme only): fire hits deal native damage, then the burn ticks for 1 twice and ends in a bubble burst;
    /// lightning hits always crit (water conducts); enemies chilled too often freeze solid for a few seconds
    /// (native FreezeStateHandler) under the dark-corruption overlay tinted blue.
    /// </summary>
    internal static partial class ThemeElements
    {
        private static readonly Color FreezeTint = new(0.35f, 0.75f, 1f, 1f);

        private static int _gateFrame = -1;
        private static bool _gate;
        private static ParticleSystem _burst;

        private sealed class Chill
        {
            public readonly Queue<float> Hits = new();
            public Il2CppSystem.Object Token;
        }
        private static readonly Dictionary<IntPtr, Chill> Chills = new();
        private static int _burnLogged;
        private const int BurnTicks = 2;
        private const float BurnTickDamage = 1f, BurnTickSeconds = 0.25f;
        private static readonly Dictionary<IntPtr, int> Burns = new();   // FireEffectHandler -> ticks dealt

        internal static bool On
        {
            get
            {
                if (_gateFrame == Time.frameCount) return _gate;
                _gateFrame = Time.frameCount;
                return _gate = CrabLandMod.Active && CrabLandMod.SeaTheme.Value && CrabLandMod.SeaElements.Value && CrabLandMod.InBaseWorldScene();
            }
        }

        internal static void AddHooks(Hooks h)
        {
            h.Add<FireEffectHandler>("Burn", postfix: P(nameof(BurnPostfix)))
             .Add<FireEffectHandler>("HandleDamageTick", prefix: P(nameof(BurnTickPrefix)), postfix: P(nameof(BurnTickPostfix)))
             .Add<IceEffectHandler>("Chill", postfix: P(nameof(ChillPostfix)));
            ProbeHooks(h);
        }

        private static System.Reflection.MethodInfo P(string name) => typeof(ThemeElements).GetMethod(name);

        internal static void Reset()
        {
            Chills.Clear();
            Burns.Clear();
            _burst = null;
            _gateFrame = -1;
            ProbeReset();
        }

        private static bool IsFire(ElementType e) => e == ElementType.Fire || e == ElementType.FireDouble;

        private static bool IsLightning(ElementType e) => e is ElementType.Lightning or ElementType.LightningDouble or ElementType.FireLightning
            or ElementType.AcidLightning or ElementType.IceLightning or ElementType.DarkLightning;

        /// <summary>From CrabLandMod.TryToDamagePrefix. False = skip the hit.</summary>
        internal static bool OnTryToDamage(HealthContainer target, ref float amount, DamageInfo damageInfo, ref bool hitIsCritical)
        {
            if (damageInfo == null || !On) return true;
            var player = damageInfo.TryCast<PlayerDamageInfo>();
            ProbeHit(target, amount, damageInfo, player, hitIsCritical);
            if (player == null) return true;
            var e = damageInfo.Element;
            if (IsLightning(e) && !hitIsCritical)
            {
                hitIsCritical = true;
                amount *= CritMultiplier(player.CriticalDamage);
            }
            return true;
        }

        // Live 2026-10-29: CriticalDamage is a percent factor already applied by the caller on native crits
        // (amount 206.25 -> 478.5 at CriticalDamage 232). A crit is never weaker than a hit.
        private static float CritMultiplier(float critDamage) => Mathf.Max(1f, critDamage / 100f);

        /// <summary>Hook: FireEffectHandler.Burn postfix. The burn keeps its native timing but ticks for 1, never crits.</summary>
        public static void BurnPostfix(FireEffectHandler __instance)
        {
            if (!On || __instance == null) return;
            var hc = __instance.healthContainer;
            if (hc == null || hc.TryCast<EnemyHealthContainer>() == null) return;
            ProbeBurn(__instance);
            if (_burnLogged++ < 3) CrabLandMod.Log.Msg($"sea: native burn secondsBetweenTicks={__instance.secondsBetweenTicks} -> {BurnTickSeconds}");
            __instance.secondsBetweenTicks = BurnTickSeconds;
            __instance.damagePerTick = BurnTickDamage;
            __instance.canCriticalHit = false;
            Burns[__instance.Pointer] = 0;
        }

        /// <summary>Hook: FireEffectHandler.HandleDamageTick prefix. No ticks past the second (until the next Burn).</summary>
        public static bool BurnTickPrefix(FireEffectHandler __instance)
        {
            if (!On || __instance == null || !Burns.TryGetValue(__instance.Pointer, out var n)) return true;
            __instance.damagePerTick = BurnTickDamage;
            return n < BurnTicks;
        }

        /// <summary>Hook: FireEffectHandler.HandleDamageTick postfix. After the second tick the burn bursts into bubbles and ends.</summary>
        public static void BurnTickPostfix(FireEffectHandler __instance)
        {
            if (!On || __instance == null || !Burns.TryGetValue(__instance.Pointer, out var n)) return;
            Burns[__instance.Pointer] = ++n;
            ProbeBurnTick(__instance, n);
            if (n < BurnTicks) return;
            Burns.Remove(__instance.Pointer);
            var hc = __instance.healthContainer;
            if (hc != null) Bubbles(hc.transform.position + Vector3.up);
            __instance.StopBurning();
        }

        public static void ChillPostfix(IceEffectHandler __instance, HealthContainer targetHealthContainer)
        {
            if (!On || __instance == null || targetHealthContainer == null) return;
            if (targetHealthContainer.TryCast<EnemyHealthContainer>() == null) return;
            var key = __instance.Pointer;
            if (!Chills.TryGetValue(key, out var c)) Chills[key] = c = new Chill();
            if (c.Token != null) return;
            var now = Time.time;
            c.Hits.Enqueue(now);
            while (c.Hits.Count > 0 && now - c.Hits.Peek() > CrabLandMod.SeaFreezeWindow.Value) c.Hits.Dequeue();
            if (c.Hits.Count < CrabLandMod.SeaFreezeStacks.Value) return;
            c.Hits.Clear();
            var fsh = __instance.freezeStateHandler;
            if (fsh == null || fsh.IsImmune) return;
            c.Token = new Il2CppSystem.Object();
            fsh.AddFreezeStack(c.Token);
            CrabLandMod.Log.Msg($"sea: freeze {targetHealthContainer.gameObject.name} for {CrabLandMod.SeaFreezeSeconds.Value}s frozen={fsh.IsFrozen}");
            MelonCoroutines.Start(Frozen(key, c, fsh, targetHealthContainer));
        }

        private static IEnumerator Frozen(IntPtr key, Chill c, FreezeStateHandler fsh, HealthContainer hc)
        {
            var renderers = Overlay(hc, true);
            var end = Time.time + CrabLandMod.SeaFreezeSeconds.Value;
            // Ends early on death or despawn so pooled enemies never come back frozen.
            while (Time.time < end && hc != null && hc.gameObject.activeInHierarchy && !hc.IsHealthZero())
                yield return null;
            try { if (fsh != null) fsh.RemoveFreezeStack(c.Token); }
            catch (Exception e) { CrabLandMod.Log.Warning($"sea: unfreeze failed: {e.Message}"); }
            SetOverlay(renderers, false);
            c.Token = null;
            Chills.Remove(key);
        }

        // ---- Blue corruption overlay: the dark element's own property on the enemy's own renderers ----

        private static int _darkId = -1, _tintId = -1;
        private static string _tintName;
        private static MaterialPropertyBlock _mpb;

        private static List<Renderer> Overlay(HealthContainer hc, bool on)
        {
            var list = new List<Renderer>();
            try
            {
                var deh = hc.GetComponent<DarkEffectHandler>() ?? hc.GetComponentInChildren<DarkEffectHandler>() ?? hc.GetComponentInParent<DarkEffectHandler>();
                var cv = deh != null ? deh.CorruptionVisualizer : null;
                if (cv == null || cv.renderers == null) { CrabLandMod.Log.Warning($"sea: no CorruptionVisualizer on {hc.gameObject.name}"); return list; }
                foreach (var r in cv.renderers) if (r != null) list.Add(r);
                if (_darkId < 0) _darkId = CorruptionVisualizer.DarkCorruption;
                if (_tintId < 0 && list.Count > 0) FindTint(list[0]);
                SetOverlay(list, on);
            }
            catch (Exception e) { CrabLandMod.Log.Warning($"sea: overlay failed: {e.Message}"); }
            return list;
        }

        // A colour property beside DarkCorruption on the enemy shader; logged once.
        private static void FindTint(Renderer r)
        {
            _tintId = 0;
            var sh = r.sharedMaterial != null ? r.sharedMaterial.shader : null;
            if (sh == null) return;
            var colours = new List<string>();
            for (var i = 0; i < sh.GetPropertyCount(); i++)
            {
                if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Color) continue;
                var n = sh.GetPropertyName(i);
                colours.Add(n);
                if (_tintName == null && (n.IndexOf("Dark", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Corrupt", StringComparison.OrdinalIgnoreCase) >= 0))
                    _tintName = n;
            }
            if (_tintName != null) _tintId = Shader.PropertyToID(_tintName);
            CrabLandMod.Log.Msg($"sea: freeze overlay shader={sh.name} darkId={_darkId} tint={_tintName ?? "none"} colours={string.Join(",", colours)}");
        }

        private static void SetOverlay(List<Renderer> list, bool on)
        {
            _mpb ??= new MaterialPropertyBlock();
            foreach (var r in list)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetFloat(_darkId, on ? 1f : 0f);
                if (_tintId > 0)
                {
                    if (on) _mpb.SetColor(_tintId, FreezeTint);
                    else if (r.sharedMaterial != null) _mpb.SetColor(_tintId, r.sharedMaterial.GetColor(_tintId));
                }
                r.SetPropertyBlock(_mpb);
            }
        }

        // ---- Bubble burst: one world-space emitter, Emit() at each fire hit ----

        private static void Bubbles(Vector3 at)
        {
            try
            {
                if (_burst == null)
                {
                    var go = new GameObject("CrabLandFireBubbles");
                    var ps = go.AddComponent<ParticleSystem>();
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    var main = ps.main;
                    main.loop = false; main.playOnAwake = false; main.maxParticles = 1500;
                    main.simulationSpace = ParticleSystemSimulationSpace.World;
                    main.startLifetime = Range(0.8f, 1.8f);
                    main.startSize = Range(0.1f, 0.4f);
                    main.startSpeed = Range(0.8f, 2.8f);
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.75f, 0.95f, 1f, 0.85f));
                    main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.3f);
                    var em = ps.emission; em.enabled = false;
                    var shape = ps.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 0.6f;
                    var col = ps.colorOverLifetime; col.enabled = true;
                    var g = new Gradient();
                    g.SetKeys(new GradientColorKey[] { new(Color.white, 0f), new(Color.white, 1f) },
                              new GradientAlphaKey[] { new(1f, 0f), new(1f, 0.7f), new(0f, 1f) });
                    col.color = new ParticleSystem.MinMaxGradient { mode = ParticleSystemGradientMode.Gradient, gradient = g };
                    var r = go.GetComponent<ParticleSystemRenderer>();
                    r.sharedMaterial = Theme.ParticleMaterial(Theme.Disc("CrabLandFireBubble", true), out var source);
                    r.renderMode = ParticleSystemRenderMode.Billboard;
                    ps.Play();
                    _burst = ps;
                    CrabLandMod.Log.Msg($"sea: fire bubbles emitter material={source}");
                }
                for (var i = 0; i < 90; i++)
                    _burst.Emit(new ParticleSystem.EmitParams { position = at + UnityEngine.Random.insideUnitSphere * 0.7f }, 1);
            }
            catch (Exception e) { CrabLandMod.Log.Warning($"sea: bubbles failed: {e.Message}"); }
        }

        private static ParticleSystem.MinMaxCurve Range(float min, float max) =>
            new() { mode = ParticleSystemCurveMode.TwoConstants, constantMin = min, constantMax = max };

        static partial void ProbeHooks(Hooks h);
        static partial void ProbeReset();
        static partial void ProbeBurn(FireEffectHandler h);
        static partial void ProbeBurnTick(FireEffectHandler h, int n);
        static partial void ProbeHit(HealthContainer target, float amount, DamageInfo damageInfo, PlayerDamageInfo player, bool hitIsCritical);
    }
}
