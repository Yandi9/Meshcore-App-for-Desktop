# MeshCore for Windows

A native Windows client for [MeshCore](https://meshcore.io) LoRa mesh radios, ported from **MeshCore One** (the
iPhone/iPad/Mac app). It talks to a MeshCore *companion* radio over **Bluetooth LE, USB cable or WiFi** and brings
over the features of the Apple app.

Built with C# / .NET 10 and Avalonia UI. Runs on Windows 10 (version 2004 or later) and Windows 11, on both
**ARM64** (e.g. Surface Pro X / Surface Pro with Snapdragon) and **x64** PCs.

---

## Running it

The `dist` folder has ready-to-run, self-contained programs (no .NET install needed):

| File | For |
|---|---|
| `MeshCore-arm64.exe` | Windows on ARM (Snapdragon Surface Pro, Copilot+ PCs) |
| `MeshCore-x64.exe` | Regular Intel/AMD PCs |
| `LockScreenWidget\` | The optional lock screen widget — keep this folder next to the program (see below) |

Double-click the one that matches your PC. The programs aren't code-signed, so the first time Windows SmartScreen
may say *"Windows protected your PC"* — click **More info → Run anyway**.

The app stores its data in `%APPDATA%\MeshCore` (database, settings, logs, map tiles). Data from an earlier
"MeshCore One" build in `%APPDATA%\MeshCoreOne` is moved there automatically the first time. Saved remote-node
passwords are encrypted for your Windows account (DPAPI).

### Connecting a radio

Click **Connect** in the bottom-right corner (or the connection icon at the bottom of the left sidebar).

* **Bluetooth** — press *Scan*, choose your radio (named `MeshCore-…`) and *Connect*. Windows pairs with the radio
  using the PIN in the box (MeshCore's default is `123456`; radios with a screen show their PIN). If pairing fails,
  remove the radio in *Windows Settings → Bluetooth & devices* and try again.
* **USB** — plug the radio in with a data cable, pick its COM port, *Connect*. The radio must run *companion USB* firmware.
  Close any other program using the port (another MeshCore app, Arduino IDE, serial monitor).
* **WiFi** — enter the radio's IP address (port 5000 by default). The radio must run *companion WiFi* firmware.
* **Demo** — a simulated radio with contacts, channels and a room server, to try the app without hardware.

The app reconnects automatically if the link drops, and (optionally) connects to the last radio on start-up.
Closing the window keeps it running in the notification area so messages still arrive; right-click the tray icon to quit.
**First run:** the very first time MeshCore opens on a PC, a short welcome guide first asks which language you want
(it can be changed later in *Settings → General → Language*), explains chats, contacts and the
notification area, then offers to connect a radio. The first radio you connect gets a *Set up your radio* card — its
name, the radio preset for your area (the one for your country is suggested), your position and an advert so others
add you. PCs that have used MeshCore before don't see the guide, and once a radio has been connected neither the
prompt nor the set-up comes back (while no radio has ever been connected, MeshCore offers to connect one at start).

**Languages:** MeshCore is in English, Español, Deutsch, Français, Italiano, Nederlands, Polski and Português (the
wording follows the iPhone app's translations). *Settings → General → Language* switches it straight away — no restart,
and the radio stays connected — or follows Windows' language (*Windows default*, English when Windows uses another one).

When MeshCore starts, a short animation plays: the MESHCORE wordmark builds up letter by letter out of blocks, then
radio-wave rays fly in and hit the logo's frame until it's fully charged, and it sends out a ripple (under three
seconds; click or press a key to skip). It has sound: a rising blip as each letter lands, a zap for every ray hitting
the frame over a charging hum, and a deep shock-wave boom with the ripple (made by the app itself, no sound files). It
plays once per launch — not when you bring the window back from the tray — and not when Windows' *Animation effects* is
off. *Settings → General → Startup* has switches for the animation and its sound, and a *Play* button.
*Settings → General → Startup* can also start MeshCore when you sign in to Windows (in the notification area, or with its
window open) — this is the same switch as MeshCore under *Windows Settings → Apps → Startup*.

### The lock screen

* **Status card** — while the PC is locked, one quiet notification shows how many new messages you have and the radio's
  battery, and updates in place as they change (it's removed when you unlock). Windows shows it when *Show
  notifications on the lock screen* is on (*Windows Settings → System → Notifications*). Switch it off in
  *Settings → Notifications → Lock screen*.
* **Widget** — a real Windows 11 widget with the number of new messages and the radio's battery, for the lock screen and
  the Widgets board (Win+W). Windows only takes widgets from installed apps, so it comes as a small signed package in
  the `LockScreenWidget` folder. Click *Install* in *Settings → Notifications → Lock screen*: Windows asks once for
  administrator permission to trust the package's certificate (MeshCore.cer, added to *Trusted People*), then the
  widget and the Windows App Runtime it needs are installed. Then open *Windows Settings → Personalization → Lock
  screen*, choose *Add widget* and pick **MeshCore**. The widget reads its numbers from the running MeshCore app, so
  MeshCore needs to be running (it can start with Windows); the app sends it every change the moment it happens, and
  messages that arrive while the PC is locked stay counted as new even if their chat was open. *Remove* in the same
  place uninstalls it; after an update of MeshCore the button says *Update*.

---

## Features

**Chats**
* Direct messages with delivery status (sent / delivered with round-trip time / failed), automatic retries, and a
  switch to flood routing when the stored path stops working
* Channels: Public, hashtag (`#name`) and private channels; join from `meshcore://` links or QR codes; per-channel
  region flood scope
* Room servers: sign in as guest or with a password, post, read history
* Message chips like the iPhone app: on your channel messages the repeat arrows and how many repeats were heard; on
  received messages an arrow hopping off the ground with the number of hops, and an ear with how many times a channel
  message was heard. Reactions (compatible with MeshCore One and MeshCore Open),
  replies, `@mentions` with suggestions, clickable links, hashtags and coordinates
* Link previews, inline images and map previews for shared coordinates
* Message details: route, hop list with repeater names, SNR, every heard repeat, reactions; show the path on the map
* Per-conversation notification level (all / mentions only / muted), blocking contacts and channel senders
* Windows notifications with **quick reply** from the notification
* Unread counts on every chat, contact and room, a total on the Chats button, and a red badge on the taskbar and
  tray icons (plus the window title)
* A **New Messages** line above the first message you haven't seen — when you open a chat, when you come back to the
  window after minimising it or closing it to the tray, and after restarting the app (it reopens the chat you had open)
* The **+** button next to the message box: pick an emoji; **share your location** (this PC's, or the radio's
  position), **share a contact** (pick one from your list) or **share my info** (your radio's contact card) — the same
  formats as the iPhone app, so a shared contact shows as a tappable *Contact: name* that adds it; or upload a picture
  with [MeshPic](https://meshpic.org) in a small window — the full `https://` link of the picture you upload is
  inserted where the cursor was. Pictures you uploaded before
  are listed at the top of the window and are only inserted if you pick one (the window uses Microsoft Edge WebView2,
  which comes with Windows 11; without it the site opens in your browser and the link is picked up when you copy it)
* **Chat appearance**: give any chat its own background (colour, gradient or your own picture) and bubble/text
  colours with the palette button in the chat header; presets included. The chat's name bar sits right on the
  background. *Settings → Chat looks* is the master control — switch all custom looks on/off, set a default look for
  every chat, and edit or reset any chat's look

**Contacts** — filter bubbles like the Chats list (All, Companions, Repeaters, Rooms, Sensors, Favorites, and
Blocked, which lists blocked contacts and channel senders to unblock), auto-discovered nodes, add from links / QR / codes, favourites, nicknames, share your contact as QR
or link, zero-hop ping, telemetry request, per-contact telemetry permissions, path discovery / edit / reset.

**Map** — contact and discovered-node positions, standard / **dark** / satellite / topographic layers, offline map
download for an area, path drawing, right-click for line-of-sight or setting your radio's position. The Dark layer is
[OpenFreeMap](https://openfreemap.org)'s `dark` style (tiles.openfreemap.org/styles/dark): its vector tiles are drawn
on your PC and cached, so it stays sharp when zoomed in.

**Tools**
* **Trace Path** — pick repeaters, run single or repeated traces, SNR per hop, saved paths
* **Line of Sight** — terrain profile with Fresnel zone, earth curvature, path loss and link-margin estimate
* **RX Log** — every received packet, decoded and decrypted where possible, with hex dump and CSV export
* **Noise Floor** — live noise floor / RSSI / SNR charts
* **Node Discovery** — find repeaters, rooms and sensors in direct range
* **CLI Terminal** — local commands for your radio and the firmware CLI of repeaters / rooms (history, completion)

**Remote node management** — sign in to repeaters and room servers; status, battery / noise / SNR history charts,
telemetry, neighbours, access list, owner info, read and change settings (name, radio, TX power, repeat, advert
intervals, flood hops, position, guest password…), clock sync, reboot, and an inline console.

**Radio** — everything is read from the connected radio (again each time you open the page), and the preset shows
what the radio is set to — e.g. *USA* — using the same rules as the iPhone app (the preset you last applied, then
the one for your country, since some presets share settings). Picking a preset switches the radio to it straight
away (after one confirmation), like the iPhone app, and the app reads the settings back to make sure the radio kept
them. *Settings → General* shows the current preset too.
Name, position (or this PC's location), adverts, radio presets and manual tuning, TX power, off-grid
repeat mode, contact auto-add filters, telemetry sharing, battery chemistry (OCV curves), path hash size, tuning,
Bluetooth PIN, default flood region, custom variables, private key export/import, statistics, reboot, factory reset.

**Settings** — one page per topic (General, Chats, Chat looks, Notifications, Backup & storage, Diagnostics, About)
that rearranges itself for small windows: the radio's current preset, theme, a slim sidebar (Ctrl+B), start with Windows, chat and notification
preferences, the lock screen status card and widget, backups, map cache, diagnostic log.

**Backup & storage** — backups use the MeshCore One **.mc1backup** format and are named like
`MC1 Backup 2026-09-30 012823.mc1backup`. A backup holds only your **messages, contacts, channels, saved paths and
settings** (radio and app settings), for every radio you've used. Disconnect the radio first — *Back up*, *Restore*
and *Export* are greyed out while it's connected. Restoring (from this app or from a phone backup) adds the contacts,
channels, chats and paths to the app straight away; the radio settings, channels and contacts are written to the
radio itself the next time you connect it (MeshCore asks first; *Write now* / *Discard* are in the same place).
Backups can be saved to any folder you choose.

**Sidebar** — the section bar on the left can be minimised to a slim icon strip with the button under the logo (or
Ctrl+B); the app remembers your choice.

---

## Building from source

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download). Visual Studio 2022/2026, Rider or VS Code
all work — open `MeshCore.Windows.sln`.

```powershell
# run from source
dotnet run --project src\MC1.Windows -f net10.0-windows10.0.19041.0

# run the tests
dotnet test tests\MeshCore.Tests
dotnet test tests\MC1.Core.Tests

# build the single-file programs into .\dist (precompiled, so they start quickly)
.\publish.ps1
```

The lock screen widget packages are built with `packaging/widget/build.sh` (Linux, macOS or WSL, with python3, openssl
and osslsigncode); `./publish.sh` runs it automatically when those tools are installed.

`src/MC1.Windows` also builds for plain `net10.0` (Bluetooth, Windows notifications and location are then disabled),
which is how the UI screenshot tool (`tests/MC1.Windows.Screens`) renders every page headlessly on any OS.

### Project layout

| Project | What it is |
|---|---|
| `src/MeshCore` | The MeshCore companion protocol: packet builder/parser, framing, request/response session, RX-log decoding and decryption, telemetry (Cayenne LPP). Transports for USB serial and TCP. |
| `src/MC1.Core` | App logic without UI: SQLite storage, message send/retry/ACK tracking, contacts, channels, rooms, remote nodes, RX log, tools, config/backup, and a simulated radio. |
| `src/MC1.Windows` | The Avalonia desktop app: views, view-models, map/chart controls, the vector-tile renderer for the Dark map (`Services/VectorTiles`), and `Platform/Windows` (WinRT Bluetooth LE, toast notifications, the lock screen status card, start with Windows, the widget installer, location, COM-port names, WebView2, `meshcore://` link registration). |
| `src/MC1.Widget` | The lock screen widget: a Windows App SDK widget provider (COM server) that reads the app's status over a named pipe and shows it with Adaptive Cards. |
| `packaging/widget` | The widget's package manifest and images, and `build.sh`, which packs and signs the `.msix` files (MakeAppx-compatible layout, signed with osslsigncode; the self-signed certificate is created once in `~/.meshcore-signing` and its private key never goes into `dist`). |
| `tests/*` | Protocol unit tests, end-to-end tests against the simulated radio, and the headless screenshot renderer. |

---

## Notes and limitations

* This port was developed and tested against the protocol test-suite and the built-in simulated radio. Please report
  any problems you hit with real hardware — the *Diagnostic log* (Settings → Diagnostics, with *Verbose logging* on)
  records every frame exchanged with the radio.
* Map tiles and terrain data need an internet connection (use *Offline maps* on the Map page to keep an area).
  Standard tiles come from OpenStreetMap, whose usage policy limits bulk downloads — offline areas are capped at
  zoom 16 and 30,000 tiles.
* Bluetooth needs a Bluetooth 4.0+ (LE) adapter. The first connection pairs the radio with Windows.
* The lock screen widget needs Windows 11 with lock screen widgets (rolled out during 2025); it also shows on the
  Widgets board. Organisations can turn lock screen widgets off with Group Policy.
* There's no auto-update; download a newer build to upgrade (your data in `%APPDATA%\MeshCore` is kept).

Developed by **YndreW**. Official MeshCore website: [meshcore.io](https://meshcore.io).

Licensed under GPL-3.0, like MeshCore One. Icons from Material Design Icons (Apache 2.0). Dark map style: OpenFreeMap /
OpenMapTiles Dark Matter (BSD-3 / CC-BY 4.0), map data © OpenStreetMap contributors.
