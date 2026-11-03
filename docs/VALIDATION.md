# Version 1.1.0 validation

Prepared 2026-11-02 with .NET SDK 10.0.403 / .NET 10.0.14. This release updates the app from MeshCore One 1.5.1 to 1.6.2 (upstream commit ecbc8bfd).

## Results

- **Builds** (self-contained Release, single-file, ReadyToRun):
  - Windows x64: succeeded.
  - Windows ARM64: succeeded. It was cross-compiled and not run on ARM64 hardware.
  - Cross-platform `net10.0` build: succeeded.
  - One existing compiler warning remains (MVVMTK0034 in the welcome guide); it has no effect.
- **Protocol tests** (`tests/MeshCore.Tests`): 176 passed, 0 failed. These include the new firmware 1.18 command, reply, power-off and contact/channel frame tests.
- **Application tests** (`tests/MC1.Core.Tests`): 271 passed, 0 failed. They run end to end against the simulated radio and include:
  - pinned chats and the favourites migration;
  - mark read/unread, drafts, pending direct messages and channel regions on send;
  - map filters, offline map policy and contact sorting;
  - favourites confirmed by the radio and discovered paths stored on it;
  - presets, *Your area* and the line-of-sight maths;
  - radio WiFi, power off, time zone, Bluetooth/WiFi links and Remote CLI;
  - restoring an iPhone backup into a connected radio. This sets its preset and frequency, restores pins and *Your area*, and never grants Remote CLI;
  - the CoreScope link: which pasted links are accepted, the site check and its name, which pages stay in the tab, and saving or removing the link.
- **Headless screenshot harness** (`tests/MC1.Windows.Screens`): 65 screens each in English (dark), German (light) and Spanish (light), run with the text-overflow check. There were no exceptions and no clipped text. New screens were reviewed by eye, including:
  - the pinned-chats section and chat menu;
  - the map filters and offline maps panels;
  - the radio WiFi, Bluetooth/WiFi switching, Remote CLI and power sections;
  - the remote commands console;
  - the restore dialog.
- **CoreScope tab**: the harness linked a CoreScope in Settings and checked that the tab appears after Map, keeps its page while other tabs are shown, and disappears when the link is removed. It was checked in English, German, Polish and Spanish with no clipped text. The harness has no built-in browser, so the CoreScope site itself (Microsoft Edge WebView2) was not shown here.
- **Translations**: 1771 strings in each of German, Spanish, French, Italian, Dutch, Polish and Portuguese. No keys are missing and the placeholders were checked.
- **Data and privacy**:
  - Existing 1.0.0 databases are migrated in place (schema v2) and existing data is kept.
  - Backups strip the Remote CLI permission flag on export and on restore.
- **Release ZIPs**: integrity and SHA256 checksums were verified during packaging.

## Not covered here — check before calling it stable

Firmware 1.18 features (radio CLI, WiFi settings, power off, Bluetooth/WiFi switching, Remote CLI) were tested only against the simulated radio. Check them on a real 1.18 radio. On older firmware they should stay hidden. A test covers this with the simulated radio set to an older protocol version.

Also check:

- the CoreScope tab with a real CoreScope link (the page, back/forward, links to other sites opening in the web browser, light and dark theme);
- the x64 and ARM64 builds on their own hardware;
- Bluetooth, USB and WiFi connections;
- restoring an iPhone backup to a real radio;
- offline map downloads over a real connection;
- the widget alongside 1.1.0.

None of these hardware or installation checks were done here.

The widget MSIX packages are the same signed version 1.2.0.0 packages as in 1.0.0, with their matching public certificate and Microsoft Windows App Runtime 2.5.1.0. No signing private key is distributed.
