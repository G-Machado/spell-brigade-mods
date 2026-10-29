using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>Beach look at runtime: warm sun, sandy ambient, sea-haze fog, a warm color filter, sand-tinted terrain.
    /// Runs on every machine with the mod; logs raw counts of what it touched.</summary>
    internal static class Reskin
    {
        private static readonly Color Sun = new Color(1f, 0.9f, 0.72f);
        private static readonly Color Ambient = new Color(0.95f, 0.85f, 0.7f);
        private static readonly Color Haze = new Color(0.62f, 0.85f, 0.88f);
        private static readonly Color Filter = new Color(1f, 0.93f, 0.8f);
        private static readonly Color SandTint = new Color(1f, 0.88f, 0.62f);
        private static int _appliedForScene = -1;

        public static void Apply()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (_appliedForScene == scene.handle) return;
            _appliedForScene = scene.handle;
            int lights = 0, volumes = 0, adjustments = 0, terrains = 0;
            try
            {
                foreach (var l in Object.FindObjectsOfType<Light>())
                {
                    if (l.type != LightType.Directional) continue;
                    l.color = Sun; l.intensity *= 1.15f; lights++;
                }
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = Ambient;
                RenderSettings.fog = true;
                RenderSettings.fogColor = Haze;
                if (RenderSettings.fogMode == FogMode.Linear) RenderSettings.fogEndDistance *= 1.3f;
                else RenderSettings.fogDensity = Mathf.Max(RenderSettings.fogDensity, 0.004f);

                foreach (var v in Object.FindObjectsOfType<Volume>())
                {
                    volumes++;
                    var profile = v.profile;   // instance copy, so the shared asset is untouched
                    if (profile == null) continue;
                    foreach (var c in profile.components)
                    {
                        var ca = c.TryCast<ColorAdjustments>();
                        if (ca == null) continue;
                        ca.active = true;
                        ca.colorFilter.overrideState = true;
                        ca.colorFilter.value = Filter;
                        adjustments++;
                    }
                }

                foreach (var t in Terrain.activeTerrains)
                {
                    if (t.materialTemplate == null) continue;
                    t.materialTemplate = new Material(t.materialTemplate) { color = SandTint };
                    terrains++;
                }
            }
            catch (Exception e) { CrabLandMod.Log.Error($"reskin: failed part-way: {e.Message}"); }
            CrabLandMod.Log.Msg($"reskin: scene={scene.name} directionalLights={lights} volumes={volumes} colorAdjustments={adjustments} terrains={terrains} fogMode={RenderSettings.fogMode}");
            if (lights + volumes + terrains == 0) CrabLandMod.Log.Error("reskin: touched 0 lights, volumes and terrains");
        }
    }
}
