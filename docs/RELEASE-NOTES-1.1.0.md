# MeshCore for Windows 1.1.0

Brings the Windows app up to date with **MeshCore One 1.6.2** (the previous release followed 1.5.1).

## What's new

- **Pinned chats**: pin direct chats, channels and rooms into a *Pinned* section. Pins are separate from the radio's favourites; former favourites are pinned once on first start.
- **Mark as read or unread** from a chat's right-click menu.
- **Map filters**: filter by last-heard time and hop count, with presets, custom ranges and removable chips.
- **Offline maps**: download exactly the framed area, choose the level of detail, and see a size estimate. Downloads can be paused and resumed. The topographic layer can't be downloaded.
- **Contacts** can be sorted by last advert, name, distance or hops, and have a right-click menu.
- **Firmware 1.18 radios**:
  - the radio's own command line in the CLI tool;
  - radio WiFi settings;
  - power off and time zone sync;
  - switching the same radio between Bluetooth and WiFi;
  - Remote CLI permissions, and sending remote commands to other companion radios.

  On older firmware these options stay hidden.
- **Radio presets**:
  - new: British Columbia (Canada) and PhillyMesh at 919.5 MHz;
  - corrected: the Australia and Czech presets;
  - *Your area* (country and state or province) picks the recommended preset, as in the iPhone app.
- **Backups**:
  - Restoring a backup, including one exported from the iPhone app, brings back the radio's preset and frequency.
  - A backup can now be restored while the radio is connected; the radio settings are written to it straight away.
  - Backups include pins and *Your area*, and never pass on remote-command permissions.
- A *Syncing messages…* status shows while waiting messages are fetched from the radio.
- **CoreScope tab**: paste the link to your state's or region's CoreScope (the MeshCore network analyzer) in *Settings ▸ General ▸ CoreScope*. A **CoreScope** tab then appears in the sidebar and shows the site inside the app, with back, forward, reload and *Open in browser*. Links to other sites open in your web browser.

## Fixes

- **Line of sight**: correct sampling and clearance, and a clear note when terrain data is missing.
- **Status charts** no longer show impossible readings.
- **Favourites and paths**: favourites are confirmed by the radio before they're saved. Paths found with *Discover path* are stored on the radio and survive contact syncs.
- **Messages**:
  - Unsent text is kept when you switch chats or the radio disconnects.
  - A direct message cut off by a disconnect is sent when the radio is back.
  - A quick reply from a notification can't go out through a different radio.
- **Channels**: channel regions are applied to every send, retry and reaction.
- **Link previews** no longer hold up messages, and preview images are fetched safely.
- **Protocol**: several decoding fixes from MeshCore One.

## Downloads

- **MeshCore-1.1.0-win-x64.zip** — Intel/AMD Windows PCs.
- **MeshCore-1.1.0-win-arm64.zip** — Windows on ARM.
- **MeshCore-1.1.0-source.zip** — matching Windows source code.
- **SHA256SUMS.txt** — download checksums.

Extract the complete ZIP and run its executable. To upgrade, close MeshCore from its tray menu, extract the new ZIP and run the new executable. Your chats and settings in `%APPDATA%\MeshCore` are kept. Keep the included `LockScreenWidget` folder next to the executable if you use the optional Windows 11 widget. The widget is unchanged from 1.0.0, so you don't need to reinstall it.

Windows 10 version 2004 or newer is required. The desktop executables are unsigned, so Windows SmartScreen may ask you to confirm the first start.

Based on **MeshCore One by Avi0n and contributors**. Desktop adaptation by **YndreW**. Distributed under GPL-3.0, with upstream and third-party notices included.

## Validation

- Both architectures build.
- Protocol tests: 176/176 passed.
- Application tests: 271/271 passed.
- Screen checks in English, German and Spanish found no clipped text.

Firmware 1.18 features were tested only against the built-in demo radio, and the CoreScope tab's built-in browser needs a check on a Windows PC. Real hardware, ARM64 execution and the widget still need manual checks. See `docs/VALIDATION.md`.
