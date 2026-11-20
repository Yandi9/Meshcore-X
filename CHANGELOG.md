# Changelog

## 1.2.0

Brings the Windows app up to date with MeshCore One 1.7.1 (from 1.6.2).

**New**
- Blocks & filters (Settings ▸ Blocks & filters): word or phrase rules that hide incoming messages, looking at the message, the sender's name or both, matching anywhere or only the whole text. Choose the kinds of chat (public and hashtag channels on by default; private channels, direct messages and rooms optional) and all or some chats of each. Hidden messages aren't saved, notified or counted as unread; reactions from a hidden sender are hidden too. The latest 100 are listed under *Recently blocked* on this PC (not in backups).
- Direct messages try the known path 4 times, then clear it and try 4 times by flood. *Attempts on Path*, *Flood Attempts* and *Fall Back to Flood* in Settings ▸ Chats ▸ Delivery (advanced); room posts use the same budget. The settings are in backups under the iPhone app's names.
- Path fallback switch: sign-in, remote commands and contact telemetry clear a path that gets no reply and try once by flood (sign-in now does this too), or keep the path when it's off.
- Node telemetry of the connected radio on the Radio page, with the time of the last successful reading.
- Sensor management: list a sensor's settings, change one with read-back verification, and send a reboot request (Sensor tab, admin sign-in).
- MECP (Mesh Emergency Communication Protocol) messages read as phrases in the app's language in chats, the chat list and notifications, with the original text under them. MECP language packs (CC BY 4.0) included. The demo radio has an `#emergency` channel.
- Telemetry temperature, pressure and altitude follow the *Metric units* setting.

**Fixed**
- ZephCore's virtual V-contact is left out of exported configuration files and never written back on import.

Database schema v3 (adds the *Recently blocked* table); existing data is kept.

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
