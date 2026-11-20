# Version 1.2.0 validation

Prepared 2026-11-20 with .NET SDK 10.0.404 / .NET 10.0.15. This release updates the app from MeshCore One 1.6.2 to 1.7.1 (upstream commit 5e980a53).

## Results

- **Builds** (self-contained Release, single-file, ReadyToRun):
  - Windows x64: succeeded.
  - Windows ARM64: succeeded. The tests and the screenshot harness ran natively on an ARM64 Windows PC; the packaged executables themselves were not started.
  - Cross-platform `net10.0` build: succeeded.
  - One existing compiler warning remains (MVVMTK0034 in the welcome guide); it has no effect.
- **Protocol tests** (`tests/MeshCore.Tests`): 176 passed, 0 failed.
- **Application tests** (`tests/MC1.Core.Tests`): 286 passed, 0 failed (271 from 1.1.0, 15 new). They run end to end against the simulated radio. The new tests cover:
  - the direct message budget (4 attempts on the path, then 4 by flood; clamping; switching the flood fallback off and on);
  - filter rules: *Contains* and whole-field matching on the message, the sender or both, which chats they apply to, saving them, hiding a matching channel message from the demo radio and listing it under *Recently blocked* (newest 100, no duplicates);
  - MECP decoding (severity, codes, people, UTC time, ETA, free text) and the English, German and fallback readings;
  - sensor settings: the `sensor list` pages, `get`/`set` replies, and listing, changing and verifying a setting on the demo radio's sensor;
  - the ZephCore V-contact key and the telemetry units.
  - Three existing tests were updated for the demo radio's new `#emergency` channel, and one for database schema v3.
- **Headless screenshot harness** (`tests/MC1.Windows.Screens`): every screen in English (dark) with the text-overflow check, plus the new screens (Blocks & filters, Delivery settings with and without flood fallback, an `#emergency` chat, node telemetry on the Radio page, the sensor tab) in English, German and Polish. No exceptions and no clipped text. The new screens were reviewed by eye.
- **Translations**: 68 new strings in each of German, Spanish, French, Italian, Dutch, Polish and Portuguese. 34 come from MeshCore One 1.7's own translations; the rest were written for this release. No keys are missing.
- **Data and privacy**:
  - 1.1.0 databases are migrated in place to schema v3 (a new *Recently blocked* table); existing data is kept.
  - *Recently blocked* stays on this PC and is not written to backups.
  - The new direct message settings are in backups under the iPhone app's names (`dmDirectAttempts`, `dmFloodAttempts`, `remoteFloodFallback`).
- **Release ZIPs**: integrity and SHA256 checksums were verified after packaging.

## Not covered here — check before calling it stable

Everything new was tested against the simulated radio only. Check on real hardware:

- direct messages over a real path that fails, to see the path reset and the flood attempts;
- sign-in to a repeater or room whose stored path is stale (Path fallback on and off);
- node telemetry from a real companion radio;
- sensor management with a real MeshCore sensor;
- MECP messages sent by another MECP-capable app;
- Bluetooth, USB and WiFi connections, and the x64 build on Intel/AMD hardware;
- the widget alongside 1.2.0.

The widget MSIX packages are the same signed version 1.2.0.0 packages as in 1.0.0 and 1.1.0, with their matching public certificate and Microsoft Windows App Runtime 2.5.1.0. No signing private key is distributed.
