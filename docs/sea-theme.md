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

## Elements (CrabLand `ThemeElements.cs`, 2026-10-29)

Gameplay change, gated on Crab Land active + `SeaTheme` + `SeaElements` + the run scene; normal maps keep native behaviour.
Hooks: `HealthContainer.TryToDamage` (the existing CrabLand prefix calls `ThemeElements.OnTryToDamage` first),
`FireEffectHandler.Burn` (prefix, skipped), `IceEffectHandler.Chill` (postfix). Only hits with a `PlayerDamageInfo` are touched.

| pref | default | effect |
|---|---|---|
| `SeaElements` | true | switch for the three below |
| `SeaFreezeStacks` | 6 | `Chill` calls on one enemy within `SeaFreezeWindow` that freeze it |
| `SeaFreezeWindow` | 4 | seconds |
| `SeaFreezeSeconds` | 3 | freeze length; ends early on death, despawn or zero hp |

- Fire (`Fire`, `FireDouble`): hit skipped (no damage), bubble burst at the target (world-space emitter, `Emit`), every burn
  blocked. Combos keep their native damage; `FireLightning` counts as lightning.
- Lightning (any lightning element): `hitIsCritical` forced; a non-crit's amount is scaled by `CriticalDamage / 100`. Live:
  `CriticalChance` is a percent (26) and `CriticalDamage` a percent factor that the caller has already applied on native crits
  (206.25 -> 478.5 at 232). TryToDamage runs on the host (`IsServer=True`).
- Freeze: native `FreezeStateHandler.AddFreezeStack/RemoveFreezeStack` with a token object (enemy stops in place). Overlay is the dark
  element's own: the `CorruptionVisualizer.renderers` get `DarkCorruption=1` and `_DarkGlow` tinted (0.35, 0.75, 1) through a
  MaterialPropertyBlock (enemy shader `Shader Graphs/HuntTargets`; colours `_EdgeColor,_EmmisionColor,_HitColor,_DarkGlow`),
  property tint, no material swap; both restored on unfreeze.
- Live self-test (synthetic `PlayerDamageInfo` through the native methods, host, 2026-10-29): fire hp 254.1 -> 254.1;
  lightning 100 -> 220 hp (x2.00 crit x1.1 enemy multiplier); freeze IsFrozen 3 s, position held, then moves, overlay on/off.

## Open
- Elements: a real fire-infused spell and a real lightning spell in play (only synthetic hits so far); unfreeze on death live;
  co-op (does `Chill` / the freeze run on the host only, and does the overlay show on clients).
- Polish pass (user 2026-10-29: "the graphics need more polish"; kelp "looks better (still raw modeling)"): kelp/coral look with the white base
  texture, a god-ray design that suits a top-down camera, coral shapes.
- Two-machine test (clients apply the look themselves; the props layout is seeded, so it should match).

## Kelp for trees, fish (2026-10-30, `ThemeLife.cs`, not yet seen live)
- `SeaKelp`: every `MeshRenderer` named *tree* is disabled (colliders untouched; re-enabled when the sea look is toggled off),
  and up to 150 tree spots (2.5 m cells) get 3 tall (5–9 m) swaying kelp ribbons. Logs `sea: kelp replaced treeRenderers=N`; N=0 is an error.
- `SeaFish`: 7 schools of 5–9 low-poly fish (diamond body, wiggling tail), drifting on curving paths, wrapped within 45 m of the
  camera focus, kept ≥0.6 m above the terrain, no colliders. Logs `sea: fish schools=… fish=…`.
