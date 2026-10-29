using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>
    /// Sea-floor props for the sea theme, made in code: kelp beds (swaying tapered strips), coral (tinted
    /// capsules and spheres) and rocks (flattened spheres). No colliders, so gameplay is unchanged.
    /// Seeded, so every machine places the same layout around the same centre.
    /// </summary>
    internal static class SeaFloor
    {
        private static readonly Color[] KelpColors = { new(0.16f, 0.5f, 0.38f), new(0.1f, 0.38f, 0.22f), new(0.42f, 0.5f, 0.16f) };
        private static readonly Color[] CoralColors = { new(1f, 0.42f, 0.38f), new(0.95f, 0.55f, 0.85f), new(1f, 0.72f, 0.3f), new(0.45f, 0.65f, 1f) };
        private static readonly Color RockColor = new(0.32f, 0.36f, 0.38f);
        private const float Radius = 60f;

        /// <summary>Places the props under <paramref name="parent"/>; returns how many it placed.</summary>
        internal static int Place(Transform parent, Vector3 center, int clusters, Action<Transform, float> sway)
        {
            var plant = SceneMaterial(new[] { "bush", "grass", "flower" }, out var plantSrc);
            var stone = SceneMaterial(new[] { "rock", "stone", "cliff", "boulder" }, out var stoneSrc);
            if (plant == null || stone == null) throw new InvalidOperationException("no scene material for sea-floor props");
            var source = $"plant={plantSrc} stone={stoneSrc}";
            var kelpMats = Array.ConvertAll(KelpColors, c => Tinted(plant, c, 1f, true));
            var coralMats = Array.ConvertAll(CoralColors, c => Tinted(plant, c, 1f, true));
            var rockMat = Tinted(stone, RockColor, 0.4f);
            var ribbon = Ribbon();

            var root = new GameObject("CrabLandSeaFloor").transform;
            root.SetParent(parent, false);
            var rng = new System.Random(1234);
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            int kelp = 0, coral = 0, rocks = 0;
            var ter = Terrain.activeTerrain;

            for (var c = 0; c < clusters; c++)
            {
                var ang = R(0f, Mathf.PI * 2f);
                var dist = Mathf.Sqrt(R(0.02f, 1f)) * Radius;
                var spot = center + new Vector3(Mathf.Cos(ang) * dist, 0f, Mathf.Sin(ang) * dist);
                spot.y = ter != null ? ter.SampleHeight(spot) + ter.transform.position.y : center.y;
                var cluster = new GameObject("Cluster" + c).transform;
                cluster.SetParent(root, false);
                cluster.position = spot;
                switch (rng.Next(3))
                {
                    case 0:   // kelp bed
                        for (int k = 0, n = rng.Next(4, 9); k < n; k++)
                        {
                            var g = new GameObject("Kelp");
                            g.transform.SetParent(cluster, false);
                            g.transform.localPosition = new Vector3(R(-1.2f, 1.2f), 0f, R(-1.2f, 1.2f));
                            g.transform.localRotation = Quaternion.Euler(0f, R(0f, 360f), 0f);
                            g.transform.localScale = new Vector3(R(0.25f, 0.45f), R(2f, 4.5f), 1f);
                            g.AddComponent<MeshFilter>().sharedMesh = ribbon;
                            var mr = g.AddComponent<MeshRenderer>();
                            mr.sharedMaterial = kelpMats[rng.Next(kelpMats.Length)];
                            mr.shadowCastingMode = ShadowCastingMode.Off;
                            sway(g.transform, R(0f, 6.28f));
                            kelp++;
                        }
                        break;
                    case 1:   // coral
                        for (int k = 0, n = rng.Next(3, 7); k < n; k++)
                        {
                            var g = Prim(rng.Next(3) == 0 ? PrimitiveType.Sphere : PrimitiveType.Capsule, cluster, coralMats[rng.Next(coralMats.Length)]);
                            var h = R(0.4f, 1.3f);
                            g.transform.localPosition = new Vector3(R(-0.8f, 0.8f), h * 0.5f, R(-0.8f, 0.8f));
                            g.transform.localRotation = Quaternion.Euler(R(-30f, 30f), R(0f, 360f), R(-30f, 30f));
                            g.transform.localScale = new Vector3(R(0.18f, 0.35f), h * 0.5f, R(0.18f, 0.35f));
                            coral++;
                        }
                        break;
                    default:   // rocks
                        for (int k = 0, n = rng.Next(2, 5); k < n; k++)
                        {
                            var g = Prim(PrimitiveType.Sphere, cluster, rockMat);
                            var s = R(0.5f, 1.6f);
                            g.transform.localPosition = new Vector3(R(-1f, 1f), s * 0.15f, R(-1f, 1f));
                            g.transform.localRotation = Quaternion.Euler(R(0f, 360f), R(0f, 360f), R(0f, 360f));
                            g.transform.localScale = new Vector3(s, s * R(0.45f, 0.75f), s * R(0.7f, 1.1f));
                            rocks++;
                        }
                        break;
                }
            }
            CrabLandMod.Log.Msg($"sea: floor props clusters={clusters} kelp={kelp} coral={coral} rocks={rocks} center={center} " +
                                $"terrain={(ter != null ? ter.name : "none")} material={source}");
            return kelp + coral + rocks;
        }

        private static GameObject Prim(PrimitiveType type, Transform parent, Material m)
        {
            var g = GameObject.CreatePrimitive(type);
            var col = g.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);   // visual only
            g.transform.SetParent(parent, false);
            var mr = g.GetComponent<MeshRenderer>();
            mr.sharedMaterial = m;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            return g;
        }

        /// <summary>A copy of a game material tinted toward <paramref name="c"/>; its texture is kept.</summary>
        private static Material Tinted(Material template, Color c, float amount = 0.75f, bool plain = false)
        {
            var m = new Material(template);
            if (plain && m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", Texture2D.whiteTexture);
            if (plain && m.HasProperty("_MainTex")) m.SetTexture("_MainTex", Texture2D.whiteTexture);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.Lerp(Color.white, c, amount));
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.Lerp(Color.white, c, amount));
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            return m;
        }

        /// <summary>An opaque material from a live scene renderer whose name has one of <paramref name="words"/>.</summary>
        internal static Material SceneMaterial(string[] words, out string source)
        {
            Material any = null;
            foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
            {
                var m = r.sharedMaterial;
                if (m == null || m.shader == null || m.renderQueue >= 2450) continue;
                if (!m.HasProperty("_BaseColor") && !m.HasProperty("_Color")) continue;
                any ??= m;
                var n = r.gameObject.name.ToLowerInvariant();
                foreach (var w in words)
                    if (n.Contains(w)) { source = $"{r.gameObject.name}:{m.name}/{m.shader.name}"; return m; }
            }
            source = any != null ? $"any:{any.name}/{any.shader.name}" : "none";
            return any;
        }

        /// <summary>A tapered, bent vertical strip (kelp blade), pivot at the base, both faces.</summary>
        private static Mesh Ribbon()
        {
            const int seg = 6;
            var v = new Vector3[(seg + 1) * 2];
            var uv = new Vector2[v.Length];
            var tri = new List<int>();
            for (var i = 0; i <= seg; i++)
            {
                float y = (float)i / seg, w = 0.5f * (1f - 0.7f * y), bend = 0.25f * y * y;
                v[i * 2] = new Vector3(-w, y, bend); v[i * 2 + 1] = new Vector3(w, y, bend);
                uv[i * 2] = new Vector2(0f, y); uv[i * 2 + 1] = new Vector2(1f, y);
                if (i == seg) break;
                int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
                tri.AddRange(new[] { a, c, b, b, c, d, a, b, c, b, d, c });
            }
            var m = new Mesh { name = "CrabLandKelp" };
            m.vertices = v; m.uv = uv; m.triangles = tri.ToArray();
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }
    }
}
