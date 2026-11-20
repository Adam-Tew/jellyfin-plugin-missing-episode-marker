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
- **Ongoing shows:** a missing episode only counts once it aired more than *N* weeks
  ago (configurable grace period). Missing episodes in older seasons count too.
- **Series and season posters** can be marked separately; season banners can show
  the number of missing episodes (`{count}`).
- **Appearance:** grey poster, colour filter with adjustable opacity, banner at the
  top, bottom, diagonally or as a corner ribbon, any colour and text. Live preview
  on the settings page.
- **Specials (season 0):** off, on for all series, or only for chosen series; pick
  specials by Sonarr monitoring or by listing them per series.
- **Anime:** missing filler (and optionally "mixed canon/filler") can be ignored.
  Filler lists come from animefillerlist.com (cached, address configurable), or can
  be entered by hand per series.
- **Per-series rules:** never mark a series, include/exclude its specials, override
  filler handling, set the filler list page or a manual filler list.
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
5. Click **Run check now**. Afterwards it runs every 6 hours and after each
   library scan.

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
2. Commit, then push a tag with the same number:
   ```
   git tag v0.2.0.0
   git push origin v0.2.0.0
   ```
3. The **Release** workflow builds the plugin, publishes a GitHub release and adds
   the version to `manifest.json`. Jellyfin then offers the update.

## License

GPL-3.0. Not affiliated with Jellyfin, Sonarr or animefillerlist.com.
