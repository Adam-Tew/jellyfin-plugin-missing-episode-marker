# Missing Episode Marker

A Jellyfin plugin that marks TV series and seasons that are missing episodes, so
viewers don't unknowingly skip an episode.

It asks **Sonarr** which monitored episodes have no file, and burns a configurable
marker (grey filter, colour overlay and/or a banner) **into the poster image**.
Because the marker is part of the image, every client shows it: the web UI,
Android, Samsung/LG TV apps, Swiftfin, Infuse and so on.

Requires **Jellyfin 12.x** and **Sonarr v3+**.

## Features

- **Ended shows:** marked when any monitored episode has no file.
- **Ongoing shows:** a missing episode only counts once it aired more than a
  configurable time ago (days, weeks, months or years). Missing episodes in older
  seasons count too.
- **Stays correct by itself:** if Jellyfin swaps a marked poster back (e.g. during a
  metadata refresh), the marker returns within seconds; a series is rechecked
  shortly after episodes are added or removed; a full check runs on a schedule you
  choose and optionally after library scans.
- **Series and season posters** can be marked separately; season banners can show
  the number of missing episodes (`{count}`).
- **Appearance:** grey poster, colour filter with adjustable opacity, banner at the
  top, bottom, diagonally or as a corner ribbon, any colour and text. Live preview
  on the settings page.
- **Specials (season 0):** off, on for all series, or only for chosen series; pick
  specials by Sonarr monitoring or tick them in a picker per series.
- **Anime:** missing filler (and optionally "mixed canon/filler") can be ignored.
  Filler lists come from animefillerlist.com (cached, address configurable), or can
  be entered by hand per series.
- **Per-series rules:** never mark a series, include/exclude its specials, override
  filler handling, set the filler list page or a manual filler list. Search and add
  several series at once with the same rule; shows and anime have separate tabs.
- **Status tab:** progress bar with time estimate, and a report of every incomplete
  series with its missing episodes and whether its poster could be marked.
- **Optional dashboard menu entry.**
- **Safe:** original poster files are never changed. The plugin only points
  Jellyfin at a marked copy, and points it back when the series is complete, when
  marking is turned off, or when the plugin is uninstalled.

## Install

1. Jellyfin → **Dashboard → Plugins → Repositories → +**
2. Name: `Missing Episode Marker`, URL:
   ```
   https://raw.githubusercontent.com/Adam-Tew/jellyfin-plugin-missing-episode-marker/main/manifest.json
   ```
3. **Catalog** → *Missing Episode Marker* → **Install**, then restart Jellyfin.
4. **Dashboard → Plugins → Missing Episode Marker**: enter your Sonarr URL (as the
   Jellyfin server reaches it, e.g. `http://sonarr:8989`) and API key, then
   **Save & test connection**.
5. On the **Status & report** tab, click **Run check now**. After that it keeps
   itself up to date (see the **General** tab for the schedule).

## How it works

- Series are matched between Sonarr and Jellyfin by TVDB id (then IMDb, then TMDB).
  Only series managed by Sonarr are checked.
- Marked posters are stored in `<jellyfin data>/missing-episode-marker/posters/`.
- If a metadata refresh replaces a poster, the next run marks the new one.
- Episodes without an air date (TBA) and future episodes never count.

## Removing the plugin

Uninstalling restores the original posters automatically. To be extra safe, click
**Remove all markers** on the settings page before uninstalling.

## Releasing a new version (maintainers)

1. Bump `version` in `build.yaml` (four numbers, e.g. `0.2.0.0`) and update `changelog`.
2. On GitHub: **Releases → Draft a new release**, create the tag `v0.2.0.0`
   (same number with a `v`), and click **Publish release**.
3. The **Release** workflow builds the plugin, attaches the zip to the release and
   adds the version to `manifest.json`. Jellyfin then offers the update.

## License

GPL-3.0. Not affiliated with Jellyfin, Sonarr or animefillerlist.com.
