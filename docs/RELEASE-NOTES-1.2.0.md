# MeshCore for Windows 1.2.0

Brings the Windows app up to date with **MeshCore One 1.7.1** (the previous release followed 1.6.2).

## What's new

- **Blocks & filters** (Settings ▸ Blocks & filters): hide incoming messages that match a word or phrase.
  - A rule looks at the message, the sender's name, or both, and matches anywhere in it or only the whole text.
  - Choose which chats use the rules: public and hashtag channels are on by default; private channels, direct messages and rooms can be switched on, for all chats or only the ones you pick.
  - Hidden messages aren't saved, notified or counted as unread. Reactions from a hidden sender are hidden too.
  - The latest 100 hidden messages are listed under *Recently blocked*, on this PC only (not in backups).
- **Direct message delivery**: direct messages now try the known path 4 times, then clear it and try 4 times by flood. Change *Attempts on Path*, *Flood Attempts* and *Fall Back to Flood* in Settings ▸ Chats ▸ Delivery (advanced). Room posts use the same settings.
- **Path fallback**: a new switch decides whether sign-in, remote commands and contact telemetry clear a path that gets no reply and try once by flood. Sign-in now does this too (it's on by default).
- **Node telemetry**: the Radio page shows your own radio's telemetry (battery, temperature, GPS…) with the time of the last successful reading.
- **Sensor management**: sign in to a sensor as admin, then use the new *Sensor* tab to see its settings, change one (the app reads it back to verify it) or send a reboot request.
- **MECP emergency messages**: messages in the Mesh Emergency Communication Protocol read as plain phrases in the app's language — severity, each code, the number of people and the UTC time — with the original text under them. Also in the chat list and notifications. The demo radio has an `#emergency` channel with examples.
- **Units**: telemetry temperature, pressure and altitude follow the *Metric units* setting (°C/°F, hPa/inHg, m/ft).

## Fixes

- A ZephCore radio's virtual "V-contact" is no longer exported into configuration files or written back on import.
- Room posts and direct messages share one retry budget.

## Not ported

Live Activity, VoiceOver, iPad, iPhone Duo and macOS changes from MeshCore One 1.7 have no Windows equivalent. Cancelling a connection in progress, sync status while connecting and opt-in debug logs were already in the Windows app.
