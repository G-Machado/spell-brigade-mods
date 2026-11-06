using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>
    /// Sea life for the sea theme, local on every machine, no colliders: tall swaying kelp where the trees were
    /// (tree renderers are switched off, their colliders stay) and small schools of low-poly fish around the camera.
    /// </summary>
    internal static class SeaLife
    {
        private const int MaxStalks = 150, SchoolCount = 7, FishPerSchool = 9;
        private const float Wrap = 45f;

        private sealed class School
        {
            public Vector3 Center, Vel;
            public float Lift, Phase;
            public Transform[] Fish, Tails;
            public Vector3[] Off;
            public float[] Wig;
        }

        private static readonly List<Renderer> Hidden = new();
        private static readonly List<School> Schools = new();

        internal static void Reset() { Hidden.Clear(); Schools.Clear(); }

        /// <summary>Re-show the trees when the sea look is switched off (A/B).</summary>
        internal static void SetOn(bool on)
        {
            foreach (var r in Hidden) if (r != null) r.enabled = !on;
        }

        internal static void Kelp(Transform root, Action<Transform, float> sway)
        {
            var plant = SeaFloor.SceneMaterial(new[] { "bush", "grass", "flower" }, out var src);
            if (plant == null) throw new InvalidOperationException("no scene material for kelp stalks");
            var mats = Array.ConvertAll(SeaFloor.KelpColors, c => SeaFloor.Tinted(plant, c, 1f, true));
            var ribbon = SeaFloor.Ribbon();
            var holder = new GameObject("CrabLandKelpStalks").transform;
            holder.SetParent(root, false);
            var cells = new HashSet<long>();
            var rng = new System.Random(4321);
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            int trees = 0, stalks = 0;
            foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
            {
                if (!r.enabled || !r.gameObject.name.ToLowerInvariant().Contains("tree")) continue;
                if (r.transform.IsChildOf(root)) continue;
                trees++;
                r.enabled = false;
                Hidden.Add(r);
                var b = r.bounds;
                var cell = ((long)Mathf.FloorToInt(b.center.x / 2.5f) << 32) ^ (uint)Mathf.FloorToInt(b.center.z / 2.5f);
                if (!cells.Add(cell) || stalks >= MaxStalks) continue;
                var baseP = new Vector3(b.center.x, b.min.y, b.center.z);
                for (var k = 0; k < 3 && stalks < MaxStalks; k++, stalks++)
                {
                    var g = new GameObject("TreeKelp");
                    g.transform.SetParent(holder, false);
                    g.transform.position = baseP + new Vector3(R(-0.7f, 0.7f), 0f, R(-0.7f, 0.7f));
                    g.transform.rotation = Quaternion.Euler(0f, R(0f, 360f), 0f);
                    g.transform.localScale = new Vector3(R(0.45f, 0.8f), R(5f, 9f), 1f);
                    g.AddComponent<MeshFilter>().sharedMesh = ribbon;
                    var mr = g.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = mats[rng.Next(mats.Length)];
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    sway(g.transform, R(0f, 6.28f));
                }
            }
            CrabLandMod.Log.Msg($"sea: kelp replaced treeRenderers={trees} hidden={Hidden.Count} stalks={stalks} material={src}");
            if (trees == 0) CrabLandMod.Log.Error("sea: kelp found 0 tree renderers; nothing replaced");
        }

        internal static void Fish(Transform root, Vector3 focus)
        {
            var plant = SeaFloor.SceneMaterial(new[] { "bush", "grass", "flower" }, out var src);
            if (plant == null) throw new InvalidOperationException("no scene material for fish");
            Color[] cols = { new(1f, 0.55f, 0.2f), new(0.35f, 0.75f, 1f), new(1f, 0.85f, 0.3f), new(0.8f, 0.45f, 0.95f) };
            var body = BodyMesh();
            var tail = TailMesh();
            var holder = new GameObject("CrabLandFish").transform;
            holder.SetParent(root, false);
            var rng = new System.Random(777);
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            var fish = 0;
            for (var s = 0; s < SchoolCount; s++)
            {
                var mat = SeaFloor.Tinted(plant, cols[s % cols.Length], 1f, true);
                var sc = new School
                {
                    Center = focus + new Vector3(R(-Wrap, Wrap), 0f, R(-Wrap, Wrap)),
                    Lift = R(1.2f, 4f), Phase = R(0f, 6.28f),
                    Fish = new Transform[FishPerSchool], Tails = new Transform[FishPerSchool],
                    Off = new Vector3[FishPerSchool], Wig = new float[FishPerSchool],
                };
                var ang = R(0f, 6.28f);
                sc.Vel = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * R(1.5f, 3f);
                var n = rng.Next(5, FishPerSchool + 1);
                for (var i = 0; i < FishPerSchool; i++)
                {
                    var g = new GameObject("Fish");
                    g.transform.SetParent(holder, false);
                    g.AddComponent<MeshFilter>().sharedMesh = body;
                    var mr = g.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = mat; mr.shadowCastingMode = ShadowCastingMode.Off;
                    var t = new GameObject("Tail");
                    t.transform.SetParent(g.transform, false);
                    t.transform.localPosition = new Vector3(0f, 0f, -0.3f);
                    t.AddComponent<MeshFilter>().sharedMesh = tail;
                    var tr = t.AddComponent<MeshRenderer>();
                    tr.sharedMaterial = mat; tr.shadowCastingMode = ShadowCastingMode.Off;
                    g.transform.localScale = Vector3.one * R(0.7f, 1.2f);
                    sc.Fish[i] = g.transform; sc.Tails[i] = t.transform;
                    sc.Off[i] = new Vector3(R(-1.8f, 1.8f), R(-0.7f, 0.7f), R(-1.8f, 1.8f));
                    sc.Wig[i] = R(0f, 6.28f);
                    if (i >= n) g.SetActive(false); else fish++;
                }
                Schools.Add(sc);
            }
            CrabLandMod.Log.Msg($"sea: fish schools={Schools.Count} fish={fish} material={src}");
            if (fish == 0) CrabLandMod.Log.Error("sea: fish spawned 0");
        }

        /// <summary>Per frame: schools drift on slow curving paths, wrap around the camera focus, stay above the ground.</summary>
        internal static void Animate(float t, float dt, Vector3 focus)
        {
            if (Schools.Count == 0) return;
            var ter = Terrain.activeTerrain;
            foreach (var s in Schools)
            {
                s.Vel = Quaternion.Euler(0f, 72f * Mathf.Sin(t * 0.17f + s.Phase) * dt, 0f) * s.Vel;
                s.Center += s.Vel * dt;
                var d = s.Center - focus;
                if (d.x > Wrap) s.Center.x -= 2f * Wrap; else if (d.x < -Wrap) s.Center.x += 2f * Wrap;
                if (d.z > Wrap) s.Center.z -= 2f * Wrap; else if (d.z < -Wrap) s.Center.z += 2f * Wrap;
                var ground = ter != null ? ter.SampleHeight(s.Center) + ter.transform.position.y : focus.y;
                s.Center.y = Mathf.Lerp(s.Center.y == 0f ? ground + s.Lift : s.Center.y, ground + s.Lift, 0.05f);
                var rot = Quaternion.LookRotation(s.Vel.sqrMagnitude > 0.01f ? s.Vel.normalized : Vector3.forward);
                for (var i = 0; i < s.Fish.Length; i++)
                {
                    var f = s.Fish[i];
                    if (f == null || !f.gameObject.activeSelf) continue;
                    var o = s.Off[i];
                    var p = s.Center + rot * new Vector3(o.x + 0.4f * Mathf.Sin(t * 0.9f + s.Wig[i]), o.y, o.z);
                    p.y = Mathf.Max(p.y, ground + 0.6f);
                    f.SetPositionAndRotation(p, rot * Quaternion.Euler(0f, 12f * Mathf.Sin(t * 5f + s.Wig[i]), 0f));
                    s.Tails[i].localRotation = Quaternion.Euler(0f, 35f * Mathf.Sin(t * 11f + s.Wig[i]), 0f);
                }
            }
        }

        // Fish faces +Z. Body: an elongated diamond; tail: a flat triangle behind it (double-sided).
        private static Mesh BodyMesh()
        {
            var v = new[] { new Vector3(0f, 0f, 0.35f), new Vector3(0.12f, 0f, 0.05f), new Vector3(-0.12f, 0f, 0.05f), new Vector3(0f, 0.1f, 0.05f),
                            new Vector3(0f, -0.1f, 0.05f), new Vector3(0f, 0f, -0.3f) };
            var tri = new[] { 0, 1, 3, 0, 3, 2, 0, 2, 4, 0, 4, 1, 5, 3, 1, 5, 2, 3, 5, 4, 2, 5, 1, 4 };
            var m = new Mesh { name = "CrabLandFishBody", vertices = v, triangles = tri };
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        private static Mesh TailMesh()
        {
            var v = new[] { new Vector3(0f, 0f, 0f), new Vector3(0f, 0.16f, -0.22f), new Vector3(0f, -0.16f, -0.22f) };
            var m = new Mesh { name = "CrabLandFishTail", vertices = v, triangles = new[] { 0, 1, 2, 0, 2, 1 } };
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }
    }
}
