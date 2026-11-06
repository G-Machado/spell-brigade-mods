using System;
using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>
    /// Under-the-sea look for a Crab Land run, local on every machine (nothing networked, no gameplay change).
    /// Water tint (fog, light, a global Volume), caustics (generated light cookie on the sun), bubbles and sea
    /// snow (camera-following particles), seagrass sway, god rays and depth (the sea darkens with the wave).
    /// Everything it makes lives in the run scene, so leaving the scene restores the native look.
    /// Logs raw counts and frame times; with SeaThemeABSeconds &gt; 0 it toggles itself off/on for A/B timing.
    /// </summary>
    internal static class Theme
    {
        private static readonly Color Shallow = new(0.08f, 0.42f, 0.5f), Deep = new(0.01f, 0.07f, 0.16f);
        private static readonly Color SunSea = new(0.55f, 0.88f, 0.92f), AmbientSea = new(0.22f, 0.48f, 0.55f);
        private static readonly Color Kelp = new(0.16f, 0.5f, 0.38f);
        private static readonly string[] FoliageWords = { "grass", "plant", "bush", "fern", "flower", "reed", "weed", "shrub", "leaf", "leaves", "foliage", "vine", "tree" };
        private const int MaxSway = 300, CookiePx = 256;

        private sealed class Sway { public Transform T; public Quaternion Base; public float Phase; }

        private static int _scene = -1, _run;
        private static bool _on;
        private static GameObject _root;
        private static Light _sun;
        private static UniversalAdditionalLightData _sunData;
        private static Texture _nativeCookie;
        private static Color _nativeSun, _nativeAmbient, _nativeFog;
        private static float _nativeSunIntensity;
        private static AmbientMode _nativeAmbientMode;
        private static bool _nativeFogOn;
        private static Transform _bubbles, _snow, _rays;
        private static readonly List<Transform> Beams = new();
        private static readonly List<Sway> Swaying = new();
        private static float _depth;

        private static bool On(MelonPreferences_Entry<bool> e) => CrabLandMod.SeaTheme.Value && e.Value;

        /// <summary>Run scene loaded with Crab Land active (every machine).</summary>
        internal static void Apply()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!CrabLandMod.SeaTheme.Value || _scene == scene.handle) return;
            _scene = scene.handle; _run++;
            SeaLife.Reset(); Beams.Clear(); Swaying.Clear(); Retinted.Clear(); _depth = 0f; _bossSeen = false;
            _sun = null; _sunData = null; _nativeCookie = _seaCookie = null; _nativeSunIntensity = 0f;
            _bubbles = _snow = _rays = null;
            _root = new GameObject("CrabLandSea");
            var made = new List<string>();
            void Try(string name, bool enabled, Action a)
            {
                if (!enabled) { made.Add(name + "=off"); return; }
                try { a(); made.Add(name + "=ok"); }
                catch (Exception e) { made.Add(name + "=FAIL"); CrabLandMod.Log.Error($"sea: {name} failed: {e}"); }
            }
            Try("tint", On(CrabLandMod.SeaTint), Tint);
            Try("caustics", On(CrabLandMod.SeaCaustics), Caustics);
            Try("particles", On(CrabLandMod.SeaParticles), Particles);
            Try("seagrass", On(CrabLandMod.SeaGrass), Seagrass);
            Try("kelp", On(CrabLandMod.SeaKelp), () => SeaLife.Kelp(_root.transform, (tr, ph) => Swaying.Add(new Sway { T = tr, Base = tr.localRotation, Phase = ph })));
            Try("fish", On(CrabLandMod.SeaFish), () => SeaLife.Fish(_root.transform, Camera.main != null ? Focus(Camera.main) : Vector3.zero));
            Try("godrays", On(CrabLandMod.SeaGodRays), GodRays);
            _on = true;
            CrabLandMod.Log.Msg($"sea: applied scene={scene.name} {string.Join(" ", made)}");
            MelonCoroutines.Start(Loop(_run, scene.handle));
        }

        // ---------- a. water tint ----------
        private static void Tint()
        {
            foreach (var l in Object.FindObjectsOfType<Light>())
                if (l.type == LightType.Directional && (_sun == null || l.intensity > _sun.intensity)) _sun = l;
            if (_sun != null)
            {
                _nativeSun = _sun.color; _nativeSunIntensity = _sun.intensity;
                _sun.color = SunSea;
            }
            _nativeAmbientMode = RenderSettings.ambientMode; _nativeAmbient = RenderSettings.ambientLight;
            _nativeFogOn = RenderSettings.fog; _nativeFog = RenderSettings.fogColor;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = AmbientSea;
            RenderSettings.fog = true;
            RenderSettings.fogColor = Shallow;

            var profile = ScriptableObject.CreateInstance(Il2CppType.Of<VolumeProfile>()).Cast<VolumeProfile>();
            var ca = ScriptableObject.CreateInstance(Il2CppType.Of<ColorAdjustments>()).Cast<ColorAdjustments>();
            ca.active = true;
            ca.colorFilter.overrideState = true; ca.colorFilter.value = new Color(0.62f, 0.92f, 0.95f);
            ca.saturation.overrideState = true; ca.saturation.value = -12f;
            ca.contrast.overrideState = true; ca.contrast.value = 8f;
            profile.components.Add(ca);
            var vg = ScriptableObject.CreateInstance(Il2CppType.Of<Vignette>()).Cast<Vignette>();
            vg.active = true;
            vg.intensity.overrideState = true; vg.intensity.value = 0.38f;
            vg.smoothness.overrideState = true; vg.smoothness.value = 0.6f;
            vg.color.overrideState = true; vg.color.value = new Color(0f, 0.05f, 0.12f);
            profile.components.Add(vg);
            var bl = ScriptableObject.CreateInstance(Il2CppType.Of<Bloom>()).Cast<Bloom>();
            bl.active = true;
            bl.intensity.overrideState = true; bl.intensity.value = 0.3f;
            bl.threshold.overrideState = true; bl.threshold.value = 1.15f;
            bl.tint.overrideState = true; bl.tint.value = new Color(0.6f, 1f, 1f);
            profile.components.Add(bl);
            profile.isDirty = true;

            var vgo = new GameObject("CrabLandSeaVolume");
            vgo.transform.SetParent(_root.transform, false);
            var vol = vgo.AddComponent<Volume>();
            vol.isGlobal = true; vol.priority = 100f; vol.weight = 1f;
            vol.sharedProfile = profile;
            CrabLandMod.Log.Msg($"sea: tint sun={(_sun != null ? _sun.name : "none")} nativeSun={_nativeSun} intensity={_nativeSunIntensity} " +
                                $"nativeFog={_nativeFog} fogMode={RenderSettings.fogMode} fogStart={RenderSettings.fogStartDistance} fogEnd={RenderSettings.fogEndDistance} " +
                                $"density={RenderSettings.fogDensity} volumeComponents={profile.components.Count}");
        }

        // ---------- b. caustics ----------
        private static void Caustics()
        {
            if (_sun == null)
                foreach (var l in Object.FindObjectsOfType<Light>())
                    if (l.type == LightType.Directional) { _sun = l; break; }
            var asset = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
            CrabLandMod.Log.Msg($"sea: caustics pipeline={(asset != null ? asset.name : "null")} supportsLightCookies={asset?.supportsLightCookies}");
            if (_sun == null) throw new InvalidOperationException("no directional light");
            if (_nativeSunIntensity <= 0f) { _nativeSunIntensity = _sun.intensity; _nativeSun = _sun.color; }
            if (asset != null && !asset.supportsLightCookies) throw new InvalidOperationException("URP asset has light cookies off");

            var tex = new Texture2D(CookiePx, CookiePx, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, name = "CrabLandCaustics" };
            var px = new Color32[CookiePx * CookiePx];
            const float tau = Mathf.PI * 2f;
            for (var y = 0; y < CookiePx; y++)
            for (var x = 0; x < CookiePx; x++)
            {
                float u = (float)x / CookiePx * tau, v = (float)y / CookiePx * tau;
                // three periodic wave fronts; caustic lines where they cancel
                var s = Mathf.Sin(3f * u + 2f * Mathf.Sin(2f * v)) + Mathf.Sin(4f * v + 2f * Mathf.Sin(3f * u + v)) + Mathf.Sin(5f * (u + v) + Mathf.Sin(2f * u - v));
                var c = Mathf.Pow(1f - Mathf.Clamp01(Mathf.Abs(s) / 1.6f), 5f);
                var b = (byte)(255f * Mathf.Clamp01(0.62f + 0.75f * c));
                px[y * CookiePx + x] = new Color32(b, b, b, b);
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);

            _nativeCookie = _sun.cookie;
            _sun.cookie = tex;
            _sunData = _sun.GetComponent<UniversalAdditionalLightData>() ?? _sun.gameObject.AddComponent<UniversalAdditionalLightData>();
            _sunData.lightCookieSize = new Vector2(14f, 14f);
            _sun.intensity = _nativeSunIntensity * 1.25f;
            CrabLandMod.Log.Msg($"sea: caustics cookie={CookiePx}px light={_sun.name} size={_sunData.lightCookieSize} intensity={_sun.intensity}");
        }

        // ---------- c. bubbles and sea snow ----------
        internal static Material ParticleMaterial(Texture2D tex, out string source)
        {
            Material template = null;
            foreach (var r in Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>())
            {
                var m = r.sharedMaterial;
                if (m == null || m.shader == null) continue;
                if (!m.shader.name.Contains("Particles") || m.renderQueue < 3000) continue;
                template = m; break;
            }
            Material mat;
            if (template != null) { mat = new Material(template); source = $"template {template.name} shader={template.shader.name} queue={template.renderQueue}"; }
            else
            {
                var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (sh == null) { source = "none"; return null; }
                mat = new Material(sh);
                mat.SetFloat("_Surface", 1f); mat.SetFloat("_Blend", 2f);
                mat.SetFloat("_SrcBlend", 1f); mat.SetFloat("_DstBlend", 1f); mat.SetFloat("_ZWrite", 0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); mat.EnableKeyword("_BLENDMODE_ADD");
                mat.renderQueue = 3000;
                source = "Shader.Find URP Particles/Unlit";
            }
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", Color.white);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            return mat;
        }

        internal static Texture2D Disc(string name, bool ring)
        {
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = name };
            var px = new Color32[n * n];
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var d = new Vector2(x - n / 2f + 0.5f, y - n / 2f + 0.5f).magnitude / (n / 2f);
                float a = ring
                    ? Mathf.Clamp01(1f - Mathf.Abs(d - 0.82f) / 0.14f) + 0.18f * Mathf.Clamp01(1f - d) + (new Vector2(x - n * 0.35f, y - n * 0.65f).magnitude < n * 0.08f ? 0.8f : 0f)
                    : Mathf.Pow(Mathf.Clamp01(1f - d), 2f);
                a = Mathf.Clamp01(a) * (d < 1f ? 1f : 0f);
                var b = (byte)(255f * a);
                px[y * n + x] = new Color32(255, 255, 255, b);
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        private static ParticleSystem.MinMaxCurve Range(float min, float max) =>
            new() { mode = ParticleSystemCurveMode.TwoConstants, constantMin = min, constantMax = max };

        private static Transform Emitter(string name, Texture2D tex, float rate, Vector2 size, Vector2 life, Vector2 vy, Vector3 box, Color color, float noise)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true; main.playOnAwake = true;
            main.maxParticles = 2000;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = Range(life.x, life.y);
            main.startSize = Range(size.x, size.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f);
            main.startColor = new ParticleSystem.MinMaxGradient(color);
            var em = ps.emission; em.enabled = true; em.rateOverTime = new ParticleSystem.MinMaxCurve(rate);
            var shape = ps.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Box; shape.scale = box;
            var vel = ps.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World;
            vel.x = Range(-0.1f, 0.1f); vel.y = Range(vy.x, vy.y); vel.z = Range(-0.1f, 0.1f);
            var nz = ps.noise; nz.enabled = true; nz.strength = new ParticleSystem.MinMaxCurve(noise); nz.frequency = 0.4f; nz.scrollSpeed = new ParticleSystem.MinMaxCurve(0.2f);
            var col = ps.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new GradientColorKey[] { new(Color.white, 0f), new(Color.white, 1f) },
                      new GradientAlphaKey[] { new(0f, 0f), new(1f, 0.15f), new(1f, 0.8f), new(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient { mode = ParticleSystemGradientMode.Gradient, gradient = g };
            var r = go.GetComponent<ParticleSystemRenderer>();
            var mat = ParticleMaterial(tex, out var source);
            if (mat == null) throw new InvalidOperationException("no particle shader");
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            ps.Play(true);
            CrabLandMod.Log.Msg($"sea: emitter {name} rate={rate} material={source}");
            return go.transform;
        }

        private static void Particles()
        {
            _bubbles = Emitter("CrabLandBubbles", Disc("CrabLandBubble", true), 22f, new Vector2(0.12f, 0.4f), new Vector2(4f, 7f),
                new Vector2(1.2f, 2.6f), new Vector3(46f, 0.5f, 46f), new Color(0.75f, 0.95f, 1f, 0.75f), 0.5f);
            _snow = Emitter("CrabLandSeaSnow", Disc("CrabLandSnow", false), 70f, new Vector2(0.04f, 0.11f), new Vector2(8f, 12f),
                new Vector2(-0.35f, -0.1f), new Vector3(50f, 14f, 50f), new Color(0.85f, 0.95f, 0.9f, 0.5f), 0.35f);
        }

        // ---------- d. seagrass ----------
        private static void Seagrass()
        {
            // TerrainData.detailPrototypes is unusable through the interop (TypeLoadException: DetailPrototype
            // violates Il2CppReferenceArray<T>'s constraint), so the sea floor gets spawned props (SeaFloor).
            int terrains = 0, swaying = 0, tinted = 0, candidates = 0, staticSkipped = 0;
            foreach (var t in Terrain.activeTerrains)
            {
                var td = t.terrainData;
                if (td == null) continue;
                terrains++;
                CrabLandMod.Log.Msg($"sea: terrain {t.name} native wavingGrass strength={td.wavingGrassStrength} amount={td.wavingGrassAmount} " +
                                    $"speed={td.wavingGrassSpeed} tint={td.wavingGrassTint} detailDistance={t.detailObjectDistance}");
                td.wavingGrassTint = Kelp;
                td.wavingGrassStrength = 1f; td.wavingGrassAmount = 1f; td.wavingGrassSpeed = 0.35f;
            }

            var byWord = new Dictionary<string, int>();
            foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
            {
                var n = r.gameObject.name.ToLowerInvariant();
                string word = null;
                foreach (var w in FoliageWords) if (n.Contains(w)) { word = w; break; }
                if (word == null) continue;
                candidates++;
                byWord.TryGetValue(word, out var c); byWord[word] = c + 1;
                if (r.isPartOfStaticBatch || r.gameObject.isStatic) { staticSkipped++; if (Retint(r)) tinted++; continue; }
                if (swaying >= MaxSway) continue;
                var mat = r.material;   // instance
                if (mat != null)
                {
                    if (mat.HasProperty("_BaseColor")) { mat.SetColor("_BaseColor", Color.Lerp(mat.GetColor("_BaseColor"), Kelp, 0.55f)); tinted++; }
                    else if (mat.HasProperty("_Color")) { mat.SetColor("_Color", Color.Lerp(mat.GetColor("_Color"), Kelp, 0.55f)); tinted++; }
                }
                if (word == "tree") continue;   // tint trees, sway only small plants
                Swaying.Add(new Sway { T = r.transform, Base = r.transform.localRotation, Phase = UnityEngine.Random.Range(0f, 6.28f) });
                swaying++;
            }
            var parts = new List<string>();
            foreach (var kv in byWord) parts.Add($"{kv.Key}={kv.Value}");
            var props = SeaFloor.Place(_root.transform, Camera.main != null ? Focus(Camera.main) : Vector3.zero,
                CrabLandMod.SeaPropClusters.Value, (t, phase) => Swaying.Add(new Sway { T = t, Base = t.localRotation, Phase = phase }));
            CrabLandMod.Log.Msg($"sea: seagrass terrains={terrains} meshCandidates={candidates} [{string.Join(" ", parts)}] " +
                                $"static={staticSkipped} tinted={tinted} swaying={swaying} props={props}");
            if (props + swaying + tinted == 0) CrabLandMod.Log.Warning("sea: seagrass touched nothing");
        }

        // ---------- e. god rays ----------
        private static void GodRays()
        {
            const int h = 64;
            var tex = new Texture2D(8, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "CrabLandRay" };
            var px = new Color32[8 * h];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < 8; x++)
            {
                float v = (float)y / (h - 1), u = Mathf.Abs(x - 3.5f) / 4f;
                var a = Mathf.Clamp01(Mathf.Sin(v * Mathf.PI) * (0.3f + 0.7f * v)) * (1f - u * u);
                px[y * 8 + x] = new Color32(255, 255, 255, (byte)(255f * a));
            }
            tex.SetPixels32(px); tex.Apply(false, false);
            var mat = ParticleMaterial(tex, out var source);
            if (mat == null) throw new InvalidOperationException("no particle shader");
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.05f, 0.1f, 0.11f, 1f));
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", new Color(0.05f, 0.1f, 0.11f, 1f));

            var mesh = new Mesh { name = "CrabLandRayQuad" };
            mesh.vertices = new[] { new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(-0.5f, 1f, 0f), new Vector3(0.5f, 1f, 0f) };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3, 0, 1, 2, 1, 3, 2 };
            mesh.RecalculateBounds();

            var raysGo = new GameObject("CrabLandGodRays");
            raysGo.transform.SetParent(_root.transform, false);
            _rays = raysGo.transform;
            for (var i = 0; i < 7; i++)
            {
                var b = new GameObject("Ray" + i);
                b.transform.SetParent(_rays, false);
                b.transform.localPosition = new Vector3(UnityEngine.Random.Range(-16f, 16f), 0f, UnityEngine.Random.Range(-12f, 12f));
                b.transform.localScale = new Vector3(UnityEngine.Random.Range(0.8f, 1.8f), UnityEngine.Random.Range(14f, 20f), 1f);
                b.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = b.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
                Beams.Add(b.transform);
            }
            CrabLandMod.Log.Msg($"sea: godrays beams={Beams.Count} material={source}");
        }

        // ---------- per frame: follow camera, animate, depth, frame time, A/B, screenshots ----------
        private static IEnumerator Loop(int run, int sceneHandle)
        {
            var start = Time.realtimeSinceStartup;
            float statT = 0f, statMax = 0f, abNext = 0f, depthNext = 0f; int statN = 0;
            var shots = new HashSet<string>();
            while (run == _run && UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle == sceneHandle && _root != null)
            {
                yield return null;
                if (run != _run || _root == null) yield break;
                var dt = Time.unscaledDeltaTime;
                statT += dt; statN++; statMax = Mathf.Max(statMax, dt);
                var now = Time.realtimeSinceStartup - start;
                try
                {
                    Animate(now);
                    if (now >= depthNext) { depthNext = now + 2f; Depth(); }
                    if (statT >= 10f)
                    {
                        CrabLandMod.Log.Msg($"sea: frames on={_on} n={statN} avgMs={1000f * statT / statN:F2} maxMs={1000f * statMax:F2} timeScale={Time.timeScale} depth={_depth:F2}");
                        statT = 0f; statN = 0; statMax = 0f;
                    }
                    var ab = CrabLandMod.SeaThemeABSeconds.Value;
                    if (ab > 0f && now >= 30f && now >= abNext) { abNext = now + ab; SetOn(!_on); }
                    var dir = CrabLandMod.SeaScreenshotDir.Value;
                    if (!string.IsNullOrWhiteSpace(dir))
                        foreach (var at in new[] { 20f, 45f, 90f, 150f })
                        {
                            var key = $"{at}";
                            if (now < at || shots.Contains(key)) continue;
                            shots.Add(key);
                            System.IO.Directory.CreateDirectory(dir);
                            var path = System.IO.Path.Combine(dir, $"sea-{DateTime.Now:HHmmss}-t{at:0}-{(_on ? "on" : "off")}.png");
                            MelonCoroutines.Start(Shot(path));
                            CrabLandMod.Log.Msg($"sea: screenshot {path} on={_on} depth={_depth:F2}");
                        }
                }
                catch (Exception e) { CrabLandMod.Log.Error($"sea: frame failed: {e.Message}"); yield break; }
            }
        }

        private static readonly Dictionary<IntPtr, Material> Retinted = new();

        private static bool Retint(Renderer r)
        {
            var m = r.sharedMaterial;
            if (m == null || (!m.HasProperty("_BaseColor") && !m.HasProperty("_Color"))) return false;
            if (!Retinted.TryGetValue(m.Pointer, out var copy))
            {
                copy = new Material(m);
                var key = copy.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
                copy.SetColor(key, Color.Lerp(copy.GetColor(key), Kelp, 0.5f));
                Retinted[m.Pointer] = copy;
            }
            r.sharedMaterial = copy;
            return true;
        }

        private static IEnumerator Shot(string path)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                var png = ImageConversion.EncodeToPNG(tex);
                System.IO.File.WriteAllBytes(path, png);
                Object.Destroy(tex);
                CrabLandMod.Log.Msg($"sea: screenshot written {path} bytes={png.Length}");
            }
            catch (Exception e) { CrabLandMod.Log.Error($"sea: screenshot failed: {e.Message}"); }
        }

        private static Vector3 Focus(Camera cam)
        {
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            return new Plane(Vector3.up, Vector3.zero).Raycast(ray, out var d) && d < 200f ? ray.GetPoint(d) : cam.transform.position + cam.transform.forward * 20f;
        }

        private static void Animate(float t)
        {
            var cam = Camera.main;
            if (cam != null)
            {
                var f = Focus(cam);
                if (_bubbles != null) _bubbles.position = f + Vector3.up * 0.2f;
                if (_snow != null) _snow.position = f + Vector3.up * 7f;
                if (_rays != null)
                {
                    _rays.position = f;
                    var camFwd = cam.transform.forward; camFwd.y = 0f;
                    for (var i = 0; i < Beams.Count; i++)
                    {
                        var b = Beams[i];
                        // face the camera around a slanted axis, sway slowly
                        var lean = Quaternion.Euler(18f + 4f * Mathf.Sin(t * 0.3f + i), 0f, 10f * Mathf.Sin(t * 0.21f + i * 1.7f));
                        b.rotation = Quaternion.LookRotation(camFwd.sqrMagnitude > 0.001f ? camFwd : Vector3.forward) * lean;
                    }
                }
            }
            if (_sunData != null && _on)
                _sunData.lightCookieOffset = new Vector2(t * 0.35f + 0.8f * Mathf.Sin(t * 0.4f), t * 0.22f + 0.8f * Mathf.Cos(t * 0.33f));
            if (cam != null) SeaLife.Animate(t, Time.deltaTime, Focus(cam));
            for (var i = 0; i < Swaying.Count; i++)
            {
                var s = Swaying[i];
                if (s.T == null) continue;
                s.T.localRotation = s.Base * Quaternion.Euler(5f * Mathf.Sin(t * 1.1f + s.Phase), 0f, 4f * Mathf.Sin(t * 0.8f + s.Phase * 1.3f));
            }
        }

        private static void Depth()
        {
            if (!On(CrabLandMod.SeaDepth) || !_on) return;
            var rm = Object.FindObjectOfType<RunManager>();
            if (rm == null) return;
            var wave = rm.CurrentWave.Value;
            var target = Mathf.Clamp01((wave - 1) / 19f);
            if (BossFight.ShellOn || _bossSeen) target = 1f;
            _depth = Mathf.MoveTowards(_depth, target, 0.02f);
            if (On(CrabLandMod.SeaTint))
            {
                RenderSettings.fogColor = Color.Lerp(Shallow, Deep, _depth * 0.85f);
                RenderSettings.ambientLight = Color.Lerp(AmbientSea, AmbientSea * 0.45f, _depth);
                if (_sun != null) _sun.intensity = (_nativeSunIntensity > 0 ? _nativeSunIntensity : 1f) * (On(CrabLandMod.SeaCaustics) ? 1.25f : 1f) * (1f - 0.45f * _depth);
            }
            if (_rays != null) _rays.gameObject.SetActive(_depth < 0.85f);
        }

        private static bool _bossSeen;
        internal static void BossArrived() { _bossSeen = true; CrabLandMod.Log.Msg("sea: boss arrived, abyss"); }

        /// <summary>A/B: take the sea look off (pre-sea lights and fog, no volume/particles/rays) and put it back.</summary>
        private static void SetOn(bool on)
        {
            _on = on;
            SeaLife.SetOn(on);
            if (_root != null) _root.SetActive(on);
            if (_sun != null)
            {
                if (!on)
                {
                    if (_sun.cookie != _nativeCookie) _seaCookie = _sun.cookie;
                    _sun.cookie = _nativeCookie; _sun.color = _nativeSun; _sun.intensity = _nativeSunIntensity;
                }
                else
                {
                    if (_seaCookie != null) _sun.cookie = _seaCookie;
                    if (On(CrabLandMod.SeaTint)) _sun.color = SunSea;
                }
            }
            if (!on)
            {
                RenderSettings.ambientMode = _nativeAmbientMode; RenderSettings.ambientLight = _nativeAmbient;
                RenderSettings.fog = _nativeFogOn; RenderSettings.fogColor = _nativeFog;
            }
            else if (On(CrabLandMod.SeaTint))
            {
                RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = AmbientSea;
                RenderSettings.fog = true; RenderSettings.fogColor = Shallow;
                _depth = Mathf.Max(0f, _depth - 0.02f);   // Depth() re-darkens on its next tick
            }
            CrabLandMod.Log.Msg($"sea: ab on={on}");
        }

        private static Texture _seaCookie;
    }
}
