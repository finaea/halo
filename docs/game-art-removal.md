# Removing game art

Azur Archive's game art is removed on request, as explained in [NOTICE.md](../NOTICE.md). For a
GitHub notice, the response deadline is given in the notice; the
[DMCA takedown policy](https://docs.github.com/en/site-policy/content-removal-policies/dmca-takedown-policy)
describes the process.

## Why removal is small

All of the art lives in one folder, `assets/skins/azur-archive/game-art/`, and nowhere else:

| Path | What it is |
| --- | --- |
| `game-art/blue-archive/*.png` | Character portraits and face crops |
| `game-art/azur-lane/manjuu.png` | Manjuu, shown on cards with no data |
| `game-art/talk/lines.json` | A few in-game lines used as chat messages |
| `game-art/halos/*.json` | Simplified vector halo shapes; without them every face gets a plain ring |
| `game-art/previews/*.png` | Settings-gallery pictures rendered with the art in them |
| `game-art/CREDITS.md` | One row per file: what it is, where it came from, who owns it |

The skin works without the folder. Character pictures disappear, chat messages use initials, and
bundled in-game lines are omitted. Halo's own messages remain. The Settings gallery uses the
art-free pictures in `assets/skins/azur-archive/previews/`.

README and docs screenshots are kept art-free. Any other images made from the game art are listed
under "Derived images outside this folder" in `CREDITS.md` and included in removal.

## Steps

1. **Delete the named files**, or the whole `game-art/` folder, and remove their rows from
   `CREDITS.md` (if only some files go). Commit and push.
2. **Confirm removal.** Reply to GitHub or the rights holder within the requested deadline.
3. **Remove history if requested.** The folder can be removed with
   [git-filter-repo](https://github.com/newren/git-filter-repo), followed by a force push:

   ```powershell
   git filter-repo --path assets/skins/azur-archive/game-art --invert-paths
   ```

4. **Re-cut affected releases.** Installers and portable zips carry the art, because
   `tools\build.ps1` copies the whole `assets` tree into the release folder. Rebuild with
   `tools\build.ps1 -Clean -Installer -Zip`, replace the files on each affected GitHub Release (or
   delete those releases), and check the new output as in [Checking a build](#checking-a-build).
   `build.ps1` replaces `assets\skins` in its output folder rather than merging into it, so art
   from an earlier build in the same folder does not survive, and it only renders art-bearing
   gallery pictures when the art folder exists.
5. **Regenerate anything derived from the art.** Gallery pictures with the art are inside
   `game-art/previews/`, so step 1 already removed them. Anything listed under "Derived images
   outside this folder" in `CREDITS.md` is replaced with an art-free version.
6. **Reword the skin's description** once the Blue Archive cast is gone. The Settings blurb in
   `src/Halo.Shared/Skins/AzurArchiveSkinInfo.cs` describes the skin as themed around that cast,
   which would oversell it without the art.

### A copy that is already installed

Installing the rebuilt release over an existing copy removes the previous bundled game art before
copying the new files. Uninstalling removes the program folder.

A portable copy is replaced by unzipping the new release into an empty folder; unzipping over the
old one would keep the old files. The art folder can also be deleted by hand from either kind of
copy, and the skin carries on without it.

## Checking a build

For a full removal, the output should contain no `game-art` folder. For selected files, it should
contain no copy of the removed files. From the repository root, with the release in `dist\app`:

```powershell
# no game-art folder in the output
Get-ChildItem -Recurse dist\app -Directory -Filter game-art
# no output file identical to a removed art file (run against a copy of the removed files)
$art = Get-ChildItem -Recurse <removed art> -File | Get-FileHash | ForEach-Object Hash
Get-ChildItem -Recurse dist\app -File | Get-FileHash | Where-Object { $art -contains $_.Hash }
```

After a full removal, both commands should print nothing. After selected files are removed, the
hash check should print nothing; the folder may remain. The following commands render cards for a
visual check:

```powershell
dist\app\Halo.Widgets.exe --render cpu.png --type cpu-ram --skin azur-archive --preset port-day --fixture idle --warp
dist\app\Halo.Widgets.exe --render companion.png --type companion --skin azur-archive --preset port-day --fixture hot --warp
```
