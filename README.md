# Aion2Dps

A passive, local DPS meter for **AION 2 (Steam / Global)**: a live party DPS overlay with boss HP, per-skill
breakdowns, hit quality and defense stats, rotation timelines, PvP review, saved fight history, personal trends,
character lookup, themes and EN/KO/ZH game names.

It reads a copy of the game's own server→client network traffic through **Npcap** (like Wireshark). It never injects
into the game, never reads or writes game memory, never automates input and never sends anything to the game servers.
The only network requests it makes itself are read-only lookups on the official AION 2 character site, and only when
you use the Character page.

## Features

* **Overlay**: borderless, click-through-capable, never takes focus. Boss name, HP bar and timer, ranked rows with
  DPS / total / contribution (optional gear score, crit %, max hit), DPS / TOTAL / TAKEN / HEAL views, boss-only / all-targets /
  PvP modes, target cycling, training stopwatch (30 s–5 min), copy-to-chat summary, KILL / WIPE badges, an HP-check
  warning when decoded damage does not explain the boss's HP loss, and clear states for "Npcap missing",
  "waiting for game", "detecting" and "waiting for combat".
* **Breakdown** (click a row): DPS timeline, rotation ribbon, per-skill table (hits, casts, crit, min/avg/max, DoT),
  accuracy (crit / back / perfect / double / parry / dodge), defense (damage taken by source), buff uptime;
  ctrl+click two rows to compare players side by side.
* **History**: every boss kill and wipe is saved to a local SQLite database (`%APPDATA%\Aion2Dps\history.db`) with
  the full encounter (every hit), grouped by instance and boss, with a full report per fight.
  Simultaneous bosses share an encounter with separate HP checks and damage breakdowns for each boss.
* **Trends**: best / median / last DPS and fastest kill per boss, with a per-fight DPS chart.
* **Character**: your character as detected from login data, plus lookup of any character's gear, stats and
  daevanion boards through the official AION 2 site (Global, Korea, Taiwan).
* **Dashboard**: live capture and protocol diagnostics (opcode census, decoder errors), recording of the game
  connection to `.pcapng`, 7 themes plus custom colours, settings (language, adapter, idle timeouts, party-only, …),
  tray icon and global hotkeys (Ctrl+Alt+D reset, C copy, O toggle overlay, L lock + click-through).

## Install

Run this in PowerShell on Windows 10/11 x64:

```powershell
irm https://raw.githubusercontent.com/TheLoop705/aion2dps/main/install.ps1 | iex
```

The installer downloads the [latest release](https://github.com/TheLoop705/aion2dps/releases/latest), verifies its
SHA-256 checksum, installs to `%LOCALAPPDATA%\Programs\Aion2Dps`, creates a Start Menu shortcut and starts the meter.
The release includes the .NET runtime. Run the same command to update after closing the meter; settings and fight
history in `%APPDATA%\Aion2Dps` are preserved. You can also download the release ZIP and extract it anywhere.

Version 0.2.1 hardens capture during dungeon transitions, corrects summon identity and shield damage accounting,
and checks boss HP loss against damage and effective healing. The installer uses the latest published GitHub release.

## Requirements

* Windows 10/11 x64
* [.NET 10 SDK](https://dotnet.microsoft.com/download) only if building from source
* [Npcap](https://npcap.com/#download) for live capture, installed with **"WinPcap API-compatible Mode"** checked.
  Leave "Restrict Npcap driver's access to Administrators only" unchecked so the meter runs without admin rights.
  Replays, the simulator and the demo work without Npcap.

## Build and test

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
dotnet build Aion2Dps.sln
dotnet test Aion2Dps.sln
```

## Run the meter

```powershell
# Live capture (default). Start it before or after the game; it finds the AION 2 process and its connection,
# picks the right adapter and locks onto the game flow by its heartbeat signature.
.\src\Aion2Dps.App\bin\Debug\net10.0-windows\Aion2Dps.exe

# Real decoder + combat engine fed by the wire simulator (scripted boss kill, trash pull, wipe-then-kill, looping);
# fights go to a separate %APPDATA%\Aion2Dps\demo-history.db
.\src\Aion2Dps.App\bin\Debug\net10.0-windows\Aion2Dps.exe --sim

# Replay a recording through the real pipeline (speed 1 = real time, 0 = as fast as possible);
# fights go to %APPDATA%\Aion2Dps\replay-history.db
.\src\Aion2Dps.App\bin\Debug\net10.0-windows\Aion2Dps.exe --replay C:\path\fight.pcapng --speed 4

# Self-contained demo with fake services and seeded history (no capture, no decoding)
.\src\Aion2Dps.App\bin\Debug\net10.0-windows\Aion2Dps.exe --demo
```

Other options: `--no-overlay`, `--allow-multiple`, and the offscreen verification modes `--render-screens <dir>`
(runs the real simulated pipeline and renders the overlay, breakdowns, reports, history, trends and dashboard to PNG
without showing any window) and `--render-demo-screens <dir>` (the design catalog of every overlay state × theme).

Settings, logs and the fight history live in `%APPDATA%\Aion2Dps`; recordings go to `Documents\Aion2Dps\captures`.

To show gear score in the overlay, open **Appearance → Rows & columns → Columns → Gear score**. Scores appear as
**GS** when available from the game; unknown scores show **—**. Clicking a player also shows GS in their breakdown.

## Command-line tool

`aion2dps-cli` (`src\Aion2Dps.Cli\bin\Debug\net10.0-windows\aion2dps-cli.exe`) runs the same pipeline without UI:

| Command | What it does |
|---|---|
| `replay <file> [--speed N] [--json out.json]` | Decodes a `.pcap` / `.pcapng` / hex log through flow detection, TCP reassembly, the protocol decoder and the combat engine; prints every completed encounter (kind, boss, outcome, duration, per-player damage / DPS / share / crit / healing / damage taken, HP check) and optionally writes the full encounter records as JSON |
| `census <file>` | Opcode census (count, bytes, decoded, failed per opcode) and decoder statistics (resyncs, bundle errors, decode errors, recent errors with hex) |
| `simulate <scenario> --out file.pcap [--seed N]` | Writes a simulator scenario (`BossKill`, `BossWipeThenKill`, `TrashPull`, `PvpSkirmish`, `TrainingDummy`) as a pcap with handshake, client packets and a TLS decoy flow |
| `selftest [--seeds N]` | Runs every scenario through an in-memory stream, single-flow pcap and concurrent world/instance pcap, plus a storage round trip; compares with exact ground truth; exit code 0 = pass, 1 = fail |
| `live [--seconds N] [--adapter NAME] [--record file.pcapng] [--stop-file path] [--json out.json]` | Live capture of world and instance connections; prints the meter every second. Creating the stop file stops capture gracefully; JSON exports completed encounters (exit code 2 with install instructions when Npcap is missing) |
| `adapters` | Lists Npcap capture adapters |
| `locate` | Shows the AION 2 processes, their game connections and the adapter that would be used |

## How it works

```
Npcap ─► Capture (process/connection locator, adapter choice, heartbeat flow lock, TCP reassembly)
      ─► Protocol (varint framing, LZ4 bundles, resync, opcode → typed events)
      ─► Combat (entities, summon owners, party, encounters, HP check, PvP, training)
      ─► overlay snapshot (4×/s) · encounter records ─► Analysis views · SQLite history
```

| Project | Role |
|---|---|
| `Aion2Dps.Contracts` | Shared events, records, interfaces, theme keys |
| `Aion2Dps.Capture` | Npcap capture, game process locator, flow detection, TCP reassembly, pcap/pcapng reader/writer, replay |
| `Aion2Dps.Protocol` | Frame decoder (framing, LZ4, resync) and packet decoder |
| `Aion2Dps.GameData` | Localized skill / NPC / map / server / class tables (EN, KO, zh-Hans, zh-Hant) |
| `Aion2Dps.Combat` | Combat engine |
| `Aion2Dps.Storage` | SQLite fight history |
| `Aion2Dps.Simulator` | Wire encoder, scripted scenarios with exact ground truth, simulated capture, pcap export |
| `Aion2Dps.Analysis` | Breakdown, compare, report, PvP review, history and trends views with custom charts |
| `Aion2Dps.Armory` | Character lookup client and view |
| `Aion2Dps.App` | WPF overlay, dashboard, themes, settings, tray, hotkeys, composition root |
| `Aion2Dps.Cli` | Command-line tool |

The end-to-end tests (`tests/Aion2Dps.EndToEnd.Tests`) encode every simulator scenario to the wire (random
segmentation, LZ4 and nested bundles, padding), decode it with the real pipeline and the real game data, and require
the encounter records to match the ground truth exactly (per-player damage, healing, hits, crits, DoT, summon damage
attributed to owners, boss max HP, outcome, duration to the millisecond, HP check), also through a pcap file and a
SQLite round trip.

## Data credits

The game data tables in `data/` are AION 2 game data © NCSOFT, compiled by open-source AION 2 meters released under
GPL-3.0: [taengu/A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter) (skills, NPCs, dungeons, servers, DoT
ids, skill icons, open-world maps), [Kuroukihime/AIon2-Dps-Meter](https://github.com/Kuroukihime/AIon2-Dps-Meter) via
[cyberbadger6969/aion2-dps-meter](https://github.com/cyberbadger6969/aion2-dps-meter) (healing skill ids, field boss
maps). Details per file are in [`data/NOTICE.txt`](data/NOTICE.txt). The application code was written from scratch from
this project's protocol notes.

## License and disclaimer

GPL-3.0-or-later (see [`LICENSE`](LICENSE)). AION 2 is © NCSOFT; this project is not affiliated with or
endorsed by NCSOFT. It is a personal, passive tool; use it at your own risk and in line with the game's terms.
