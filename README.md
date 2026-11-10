# Jellyfin Comskip Plugin

A Jellyfin 12.x plugin that runs [comskip](https://github.com/erikkaashoek/Comskip) on your DVR recordings (and any other library you choose) and publishes the commercial breaks as **Commercial media segments**. Jellyfin clients then show a **Skip** button, or skip on their own, the same way Intro Skipper handles intros.

It grew out of [Jellyfin-Comskip-Post-Processor](https://github.com/williamthrilliam/Jellyfin-Comskip-Post-Processor) and keeps that script's robustness tricks. It does not rewrite your files, though:

| Post-processor script | This plugin |
|---|---|
| Configured as the Live TV post-processor | Installed from a plugin repository; analysis starts on its own when a recording finishes |
| Remuxes `.ts` to `.mkv`, injects chapters, deletes the original | **Never modifies media files.** Results are stored by the plugin and served as segments |
| Needs the Chapter Segments Provider plugin | Is its own segment provider |
| `batch-comskip.sh` for an existing library | **Scheduled Tasks → Comskip → Detect commercials** |
| Sanitizing remux so corrupt OTA streams don't crash comskip | Same, into a temporary file in Jellyfin's cache (setting: *Sanitize before analysis*) |
| Auto-generates `comskip.ini` | Ships a tuned ini (from the original repo); you can point to your own |

## Requirements

- **Jellyfin 12.0 or newer.** Plugins built for 10.11 do not load on 12.x, and this one does not load on 10.11.
- **comskip installed on the Jellyfin server**, either on `PATH` or at a path you enter in the settings:
  - Ubuntu/Debian bare metal: `sudo apt install comskip`
  - **Docker:** the official image is based on Debian 13, which no longer ships comskip. Use the ready-made image this repository builds instead: the official Jellyfin image with comskip compiled in, rebuilt every week.
    ```yaml
    services:
      jellyfin:
        image: ghcr.io/zenmaster14/jellyfin-comskip:latest   # instead of jellyfin/jellyfin
        # ...everything else stays the same
    ```
    The recipe is in [`docker/Dockerfile`](docker/Dockerfile) if you'd rather build it yourself.
- ffmpeg is optional. By default the plugin uses Jellyfin's own `jellyfin-ffmpeg` for the sanitizing remux.

## Install

### From a plugin repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories → +** and add:
   - **Name:** `Comskip`
   - **URL:** `https://raw.githubusercontent.com/zenmaster14/jellyfin-plugin-comskip/main/manifest.json`
2. Under **Catalog**, install **Comskip**, then restart Jellyfin.

### Publishing a new version

Set `version:` in `build.yaml` (e.g. `1.0.1.0`) and push to `main`. The **Release** workflow builds the DLL, publishes `comskip_<version>.zip` as a [GitHub release](https://github.com/zenmaster14/jellyfin-plugin-comskip/releases), and adds the version to `manifest.json`, so Jellyfin offers it as an update. You can also start the workflow by hand from the repo's **Actions** tab.

### Manually

```bash
dotnet publish Jellyfin.Plugin.Comskip/Jellyfin.Plugin.Comskip.csproj -c Release -o out
mkdir -p /var/lib/jellyfin/plugins/Comskip_1.0.0.0
cp out/Jellyfin.Plugin.Comskip.dll /var/lib/jellyfin/plugins/Comskip_1.0.0.0/
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/Comskip_1.0.0.0
sudo systemctl restart jellyfin
```
In Docker the plugin folder is `/config/plugins`. Building needs the .NET 10 SDK.

## Set up

1. Open **Dashboard → Plugins → Comskip**. The status box at the top shows whether comskip and ffmpeg were found.
2. Choose what to analyze. **Live TV recordings** is on by default; tick any other libraries you want.
3. If you used the old script, clear **Dashboard → Live TV → Recording post-processing**. The plugin replaces it, and remuxing to MKV is no longer required.
4. Each user sets **Settings → Playback → Media segments → Commercial** to *Ask to skip* or *Skip*.
5. To backfill recordings you already have, run **Dashboard → Scheduled Tasks → Comskip → Detect commercials (Comskip)**. It also runs nightly at 03:00 to catch anything missed.
6. In each library's settings, make sure the **Comskip** media segment provider is enabled. It is enabled by default.

## How it works

- **Library watcher:** when Jellyfin adds or updates a video in scope, the item is queued. Items are only analyzed once the recording is no longer in progress and the file hasn't changed for *Settle seconds* (120 by default). Busy files are retried every 5 minutes for up to 24 hours.
- **Analysis:** comskip runs in its own folder under Jellyfin's cache directory (`<cache>/comskip/<itemId>`), with EDL output forced on, and the folder is deleted afterwards. `.ts` files are first stream-copied into a clean temporary transport stream: corrupt packets are dropped, timestamps regenerated and data/SCTE-35 streams removed.
- **Storage:** one JSON file per item in `<config>/plugins/configurations/Comskip/results/`. A result stays valid while the file's size and modification time are unchanged. Remove the item, or delete its result through the API, and its segments go too.
- **Segments:** after each run the plugin asks Jellyfin to refresh that item's media segments. Breaks are merged, padded (by default 1 s is trimmed off the end of each break so you don't miss the show's first second) and filtered (breaks under 15 s are dropped).
- **Optional `.edl` sidecars** for Kodi and MPV (off by default).

### Admin API

| Method | Path | Purpose |
|---|---|---|
| GET | `/Comskip/Status` | Tool detection, queue size, recent activity |
| POST | `/Comskip/Analyze/{itemId}?force=true` | Queue one item, or every video under a series, season or folder |
| GET | `/Comskip/Results/{itemId}` | The stored result |
| DELETE | `/Comskip/Results/{itemId}` | Forget the result and remove its segments |

## Tuning

Detection quality comes down to `comskip.ini`. The bundled ini is the original project's, tuned for US OTA broadcasts. To change it, copy `Jellyfin.Plugin.Comskip/Configuration/default-comskip.ini` somewhere the `jellyfin` user can read, edit it, and enter that path under **Custom comskip.ini**. The plugin always overrides `output_edl`, `output_ffmeta`, `output_txt`, `live_tv`, `hardware_decode` and `thread_count`.

## Notes

- Sanitizing writes a temporary copy about the size of the recording into Jellyfin's cache folder, so leave free space there.
- *Files analyzed at once* (default 1) only takes effect after a Jellyfin restart.
- Failed analyses aren't retried until the file changes. You can also force a retry with `POST /Comskip/Analyze/{itemId}`.

## License

MIT. Based on Jellyfin-Comskip-Post-Processor © 2026 Will Carlson (MIT).
