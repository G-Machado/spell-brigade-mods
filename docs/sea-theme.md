# Crab Land sea theme (CrabLand `Theme.cs`, `ThemeSeaFloor.cs`, 2026-10-29)

Local look on every machine with the mod (no host gate, nothing networked, no gameplay change). Applied
from `OnSceneWasLoaded` after `Reskin.Apply()` when Crab Land is active; everything it makes lives in the
run scene, so leaving the scene restores the native look. Logs `sea: applied ... tint=ok caustics=ok ...`.

| pref | default | effect |
|---|---|---|
| `SeaTheme` | true | master switch |
| `SeaTint` | true | teal fog and flat ambient, sea-tinted sun, global Volume (priority 100): ColorAdjustments, Vignette, Bloom |
| `SeaCaustics` | true | 256 px caustic texture made in code as the sun's light cookie (14 m tiles), offset animated |
| `SeaParticles` | true | camera-following bubbles (ring texture, rising) and sea snow (soft dots, sinking) |
| `SeaGrass` | true | sea-floor props: `SeaPropClusters` (60) clusters of swaying kelp strips, coral, rocks; static foliage gets kelp-tinted material copies |
| `SeaGodRays` | **false** | additive light shafts; off by default (see below) |
| `SeaDepth` | true | fog, ambient and sun darken with `RunManager.CurrentWave` (wave 20 = deepest), full abyss in the boss shell phase |
| `SeaThemeABSeconds` | 0 | debug: toggles the look off/on every N s to compare frame times |
| `SeaScreenshotDir` | "" | debug: screenshots at 20/45/90/150 s after scene load |

## Live (build 26133722, VerdantMeadows)
- URP asset `URP-Balanced`, `supportsLightCookies=True`. Sun `Light`: beach colour, intensity 2.3 → 2.875.
  Fog linear 17.3–156.
- Particles: no shader is created; the material is copied from a loaded particle material
  (`ps_artifact_pulse`, `Universal Render Pipeline/Particles/Unlit`, queue 3000).
- Frame time, A/B run 18:39–18:42 (20 s toggles, 10 s windows): on 11.98 ms mean (n=9), off 11.30 ms (n=6).
- Props: 139 kelp, 76 coral, 61 rocks. Map foliage: 633–1250 grass/flower/tree/bush mesh renderers, all
  static-batched (no transform sway); 71 got a tinted material copy.
- Screenshots: `tools/probes/out/sea/sea-185522-t20-on.png` (gitignored).

## Tried and dropped
- `TerrainData.detailPrototypes`: TypeLoadException (`DetailPrototype` violates `Il2CppReferenceArray<T>`'s
  constraint). Terrain `wavingGrass*` setters work.
- `Shader.Find("Universal Render Pipeline/Lit")` returns a shader, but new materials on it render black
  (assumed: stripped variants). Props copy a scene material instead. `SetTexture(null)` on those copies
  also gave black; kelp and coral now use `Texture2D.whiteTexture` (not yet seen live).
- `ScreenCapture.CaptureScreenshot(string)`: `Method not found ReadOnlySpan.GetPinnableReference` (interop).
  `CaptureScreenshotAsTexture` + `ImageConversion.EncodeToPNG` at `WaitForEndOfFrame` works.
- God rays (user: a bug, opaque yellow blades moving with the player): at full strength they washed the screen cyan under bloom; when toned down they read as yellow
  spikes from the top-down camera. Off by default.
- `ParticleSystem.MinMaxCurve(float, float)` and `MinMaxGradient(Gradient)` constructors are missing in
  the interop; set `mode`/`constantMin`/`constantMax`/`gradient` instead.

## Open
- Polish pass (user 2026-10-29: "the graphics need more polish"; kelp "looks better (still raw modeling)"): kelp/coral look with the white base
  texture, a god-ray design that suits a top-down camera, coral shapes.
- Two-machine test (clients apply the look themselves; the props layout is seeded, so it should match).
