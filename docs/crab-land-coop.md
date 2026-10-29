# Crab Land co-op test (second PC)

Both PCs need the same game build, MelonLoader 0.7.3 and the same `CrabLand.dll`. The host picks Crab
Land; the other player joins through the normal online lobby.

## 1. Check the game build

1. Steam must have The Spell Brigade fully updated.
2. Open `C:\Program Files (x86)\Steam\steamapps\appmanifest_2904000.acf` in Notepad.
3. The `"buildid"` line must read `26133722`. If it is different, stop and tell the host.

## 2. Install MelonLoader 0.7.3

1. Download `MelonLoader.Installer.exe` from https://github.com/LavaGang/MelonLoader/releases.
2. Run it, pick **The Spell Brigade**. The game exe is
   `C:\Program Files (x86)\Steam\steamapps\common\The Spell Brigade\TheSpellBrigade.exe`.
3. Choose version **0.7.3**, then click Install.
4. Launch the game once and wait. The first launch generates files for several minutes, and the main
   menu takes about 2 more minutes. Then quit.

## 3. Install the mod

1. Unzip `crab-land-coop.zip`.
2. Copy `Mods\CrabLand.dll` into `C:\Program Files (x86)\Steam\steamapps\common\The Spell Brigade\Mods\`.
3. Launch the game, reach the main menu, then quit.
4. Open `...\The Spell Brigade\MelonLoader\Latest.log`. It must contain:
   - `[CrabLand] hooks 22/22`
   - `[CrabLand] prefs AutoStartCrabLand=False DebugGodMode=False DebugSkipToBoss=False ...`

   If it shows a lower hooks count, or no `[CrabLand]` lines at all, send the log to the host.

## 4. Play

1. Host: open the world selection and click the **Crab Land** button, then open the online lobby as usual.
2. Joiner: join the host's lobby the normal way (friend invite / join).
3. Host starts the run.

## 5. Co-op checklist — both players note each item

- [ ] Joiner sees the beach reskin (different light/colours from the normal meadow).
- [ ] Only crabs spawn for both (Crabling, FleshCrab, ShieldCrab).
- [ ] After the first objective succeeds, **each** player gets an infusion pick and can choose an element.
- [ ] About 5 s later the boss objective starts: a big Crablodon (3× size) with a boss health bar on both screens.
- [ ] Boss dies, the run ends for both and returns to the menu.
- [ ] Joiner's `Latest.log` has `boss: scale (1.00, 1.00, 1.00) -> (3.00, 3.00, 3.00)` (proves the joiner scales the boss locally).
- [ ] Joiner's `Latest.log` has `net: reward from 0` and `reward: OfferSpellInfusion spell=...`.

After the run, both players send their `Latest.log` to the host.

## If something goes wrong

- The infusion pick hangs or freezes the game: in `...\The Spell Brigade\UserData\MelonPreferences.cfg`,
  under `[CrabLand]`, set `GrantInfusionReward = false` on **both** PCs, then play again.
- To remove the mod: delete `Mods\CrabLand.dll`.

## Known gaps

- The reward is the native infusion pick only. The second half of the reward, "that spell's remaining
  levels", is not built yet.
- The infusion pick has not run live yet; this test is its first run.
