# Installing RunReplays (the beta build)

"Play a stream fight" needs the game's **public-beta** branch (v0.111.0). Since 0.3.1 the Steam Workshop RunReplays
serves that build to players on public-beta (players on the regular branch keep 0.1.2), so the short way is:
switch to public-beta (step 1), then subscribe to **RunReplays** and **BaseLib** on the Workshop, start the game and
press **Load Mods**.

The rest of this page installs the GitHub release by hand instead, which takes a few minutes.

## 1. Switch Slay the Spire 2 to the public-beta branch

Steam → Library → right-click **Slay the Spire 2** → **Properties** → **Betas** → pick **public-beta**. Let Steam
finish updating. (The release won't load on the regular branch: the game says its minimum version is higher.)

## 2. Get BaseLib

RunReplays needs BaseLib. Subscribe to **BaseLib** on the Steam Workshop (Slay the Spire 2 → Workshop → search
"BaseLib" → Subscribe). Steam installs and updates it for you.

## 3. The Workshop RunReplays, if you have it

You can stay subscribed: when a mod is both on the Workshop and in the `mods` folder, the game loads the **higher
version** and switches the other off. On public-beta the Workshop copy is the same build (0.3.1), so a hand install
is only needed for a release newer than the Workshop's.

## 4. Put RunReplays in the game's mods folder

1. Download **RunReplays.zip** from the release.
2. Open the game's install folder: Steam → right-click **Slay the Spire 2** → **Manage** → **Browse local files**.
   (Usually `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2`.)
3. Make a folder named **mods** there, next to `SlayTheSpire2.exe`, if it isn't there yet.
4. Inside `mods`, make a folder **RunReplays** and extract the zip into it, so you have:

   ```
   Slay the Spire 2\
     SlayTheSpire2.exe
     mods\
       RunReplays\
         RunReplays.dll
         RunReplays.json
   ```

## 5. Start the game and allow mods

* The first time the game finds mods it asks **"Load Mods?"**. Press **Load Mods**, then close and restart the game.
* With mods on, the game keeps a separate copy of your saves ("Unmodded Saves Copied"): your normal progress is safe,
  and it's untouched when you play without mods again.
* The main menu then shows "Running Modded. Loaded N mods." and a **Run Replays** button.

## Playing a stream fight

Main menu → **Run Replays** → **Play a stream fight** → copy the fight's code or link from the stream's site →
**Paste** → **Load**. Your own saved run is backed up first; **Restore my run** in the same tab puts it back (then use
**Continue** on the main menu).

## Updating or removing

* **Update:** replace the two files in `mods\RunReplays` with the ones from a newer release.
* **Remove:** delete `mods\RunReplays`. To go back to the regular game, also set the Betas entry back to **None**.

## If something goes wrong

* **"Mod RunReplays declares min game version 0.111.0 higher than current game version"**: you're not on the
  public-beta branch (step 1).
* **"Mod RunReplays is missing 1 dependencies: BaseLib"**: subscribe to BaseLib (step 2) and restart.
* **"two or more mods with the same id"**: there are two RunReplays copies inside the `mods` folder (e.g. an old
  `RunReplays.dll` loose in `mods\` plus `mods\RunReplays\`); keep only one.
* **The menu has no "Play a stream fight" tab**: that's 0.1.2, which the Workshop gives the regular branch. Switch to
  public-beta (step 1) and let Steam update the mod too.
* **No Run Replays button**: check the files sit in `mods\RunReplays\` as shown, and that you pressed **Load Mods**
  and restarted.
* **"This fight was played on game version …"**: the fight is from a different game version than yours; it can only
  be played on that version.
