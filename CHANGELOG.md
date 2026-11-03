# Changelog

## 1.1.0

Brings the Windows app up to date with MeshCore One 1.6.2 (from 1.5.1).

**New**
- Pinned chats: pin direct chats, channels and rooms into a *Pinned* section, separate from the radio's favourites (former favourites are pinned once on first start).
- Mark a chat as read or unread from its right-click menu.
- Map filters by last-heard time and hop count, with presets, custom ranges and removable chips.
- Offline maps: download exactly the framed area with a choice of detail and a per-layer size estimate; downloads can be paused and resumed (no offline download for the topographic layer).
- Place names on the Dark map in the app's language.
- Contacts sorted by last advert (also by name, distance or hops); right-click menu on contacts.
- Firmware 1.18: the radio's own command line in the CLI tool, radio WiFi settings, power off, time zone sync, switching the same radio between Bluetooth and WiFi, Remote CLI permissions, and remote commands for other companion radios.
- Radio presets: British Columbia (Canada) and PhillyMesh at 919.5 MHz; corrected Australia and Czech presets; *Your area* (country and state/province) picks the recommended preset, like the iPhone app.
- Restoring a backup (also an iPhone backup) brings back the radio's preset and frequency, and can now be done while the radio is connected — its settings are written straight away. Backups include pins and *Your area*, and never pass on remote-command permissions.
- *Syncing messages…* status while the radio's waiting messages are fetched.
- CoreScope tab: paste the link to your state's or region's CoreScope (MeshCore network analyzer) in Settings ▸ General, and it gets its own tab in the sidebar, shown in the app's built-in browser.

**Fixed**
- Line of sight results (sampling, signed clearance, missing terrain data) and status charts that showed impossible readings.
- Favourites are confirmed by the radio before they're saved; paths found with *Discover path* are stored on the radio and survive contact syncs.
- Unsent text is kept when switching chats or after a disconnect; a direct message cut off by a disconnect is sent when the radio is back; a quick reply from a notification can't go out through a different radio.
- Channel regions are applied to every send, retry and reaction.
- Link previews no longer hold up messages; preview images are fetched over https with checked redirects and size limits.
- Contact updates use the 144-byte frame; channel names are limited to 31 bytes; several protocol decoding fixes from MeshCore One.
- Trace path batches stop when cancelled; remote admin requests keep running when the window is closed.

## 1.0.0

- Initial Windows desktop distribution for x64 and ARM64, built from the standalone C# source.
- Optional Windows lock screen widget included in both download ZIPs.
- Source-code and upstream-credit links in About.
- Removed Apple-specific file-picker metadata, font fallback and iCloud backup prompts.
- Replaced personal node names and location examples in demo/test fixtures with fictional examples.
- Added source archive, installation guide, dependency notices and SHA256 checksums.

Build date: 2026-10-01. See docs/VALIDATION.md for validation scope and remaining manual checks.
