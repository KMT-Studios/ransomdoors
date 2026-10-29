# RANS0M

> ## Modified by KMT Studio
>
> **This repository is a modified fork, not the original project.**
> **KMT Studio modified the project of the original author, Ixar.**
>
> RANS0M was created by **Ixar** — <https://github.com/Ixars/ransomdoors>.
> That repository is the canonical and original source for this software; this
> one is a derivative work modified by **KMT Studio** and published under the
> same terms, with no changes to those terms.
>
> Modifications made by KMT Studio:
>
> - **Added Twitch Channel Points integration.** Viewers can summon, force, or
>   dismiss a ransom by redeeming a custom reward, over Twitch's EventSub
>   WebSocket API. See [Twitch integration](#twitch-integration-channel-points)
>   below. This does not exist in the original project.
> - **Config rewrite.** `Config` now binds directly to a typed
>   `AppConfig`/`TwitchSettings` record pair with a `System.Text.Json`
>   serializer, replacing the reflection-based `Dictionary<string, object>`
>   loader that could round-trip values back out as a different JSON type.
>   `config.json` stays byte-compatible. Settings that would produce an
>   unplayable or unsafe state are clamped on load and save.
> - **Miscellaneous fixes and optimizations** in the overlay, gold coin
>   manager, and window topmost handling, plus assorted comment and structure
>   cleanups. The asset files, sounds, and the RANSOM concept itself are
>   unchanged and remain the original work.
>
> All original credit for the design, assets, and concept goes to Ixar and to
> **LSPLASH** (creator of *Doors*), credited in [Credits](#credits). Released
> under [LICENSE.md](LICENSE.md); no changes were made to its terms.

A fan-made recreation of the RANSOM (A-90) entity from the Roblox game
*Doors*, as a Windows desktop app. It randomly pops the entity's face up on
your screen, you need to stop moving your mouse and stay off the keyboard, or it
"infects" your PC: gold coin files (worth different amounts, `.gold1`-`.gold6`)
get scattered around your user folders, your cursor changes, and your
wallpaper turns dark red. You have to drag enough of them onto the ransom
window to cover the configured ransom amount before the timer runs out.
There's also a rare `.crucifix` file that, if found and dragged in, clears
the ransom instantly.

This was built for fun, it's kind of poorly coded.
Right now the only noticable bug is that the ransom window doesn't always stay on top of other windows, but it should be fine for the most part. It can't go on top of fullscreen apps.

## Read this before running it

By default, failing to pay in time just resets everything (no crash, no
command). But the app **can** shut down or crash your computer, or run an
arbitrary shell command, if you turn that on yourself in the config window
— it's opt-in, off by default. If you enable "crash on death" it calls
`shutdown /s /t 0`; if you enable "run command on death" it runs whatever
command you typed in. So:

- Only run it on a machine you own, and if you enable the crash/command
  options, save your work first and expect it to actually act on them.
- Not run it on anyone else's computer without them knowing exactly what
  it does and agreeing to it.

It is not malware in the sense of trying to steal anything, hide itself, or
spread, it doesn't touch your files besides dropping/deleting its own
harmless `.gold`/`.crucifix` marker files, and it's fully open source so you
can check that yourself. See [LICENSE.md](LICENSE.md) for the full terms and
disclaimer.

## Requirements

- Windows (uses Win32 hooks, `shutdown.exe`, the registry, etc. This
  won't run anywhere else)
- [.NET 10 SDK](https://dotnet.microsoft.com/) or newer
- Visual Studio 2022+ (optional, for the WinForms designer) or just the
  `dotnet` CLI

## Building & running

```
git clone https://github.com/Ixars/ransomdoors
cd rans0m
dotnet build
dotnet run
```

Or open `rans0m.slnx` in Visual Studio and hit F5.

The app runs from a system tray icon. Right-click it for:
- **Configuration** — opens the settings window (see below).
- **Close** — disabled while a ransom is active, so you can't just dodge it
  from the tray.

## Configuration

All settings are managed through the **Configuration** window (tray icon →
Configuration), and saved to `config.json` next to the executable:

- **Spawn automatically** — whether the entity shows up on its own timer.
- **Min/max spawn delay** — how often (in seconds) it can randomly appear.
- **Infection duration** — how long (in seconds) you have to pay once
  infected.
- **Ransom amount** — how much gold you need to collect to pay it off.
- **Drawer mode** — scatter coins into a temp folder tree instead of your
  real Desktop/Documents/Pictures/Music/Videos/Downloads folders.
- **Crash on death** — run `shutdown /s /t 0` if you fail to pay in time.
  Off by default.
- **Run command on death** — run a custom shell command if you fail to pay in
  time. Off by default.

You can also trigger a ransom immediately from that window to test your
settings.

## Twitch integration (channel points)

The entity can be summoned by your viewers redeeming a Channel Points reward, so
chat can spam it and you can go AFK. Turn on **Enable Twitch**, and the
configuration window grows a Twitch section.

**One-time setup**

1. Register an application at
   [dev.twitch.tv/console/apps](https://dev.twitch.tv/console/apps).
2. Copy the **OAuth Redirect URL** from your app's settings page into the
   **Redirect URI** box in RANS0M. RANS0M listens on whichever host and port
   that value names, so whatever you registered is what it uses.

   ```http
   http://localhost:3000/
   ```

   Twitch compares the redirect URI as a literal string, so the two must match
   character for character — same host, same port, same trailing slash. A
   mismatch fails with `redirect_mismatch` in the browser, and RANS0M reports
   that on the failed-login page. If you registered something else (a different
   port, `127.0.0.1`, a path like `/callback`), just put that exact string in
   the box instead; it does not have to be the value above.

   If the port is already taken when you press Connect, RANS0M says so instead
   of silently switching to a different one, since that would break the match.
3. Paste the client ID and client secret into the Twitch section.
4. Type your channel name (not the URL) and press **Connect**. A browser opens so
   you can approve access, and the app stores the resulting user token.
5. Pick which custom reward **triggers** the ransom, which one **forces** it, and optionally
   which one **dismisses** it. The reward lists are read from your channel, so create the
   rewards in the Twitch dashboard first.

**Behaviour**

- Enabling Twitch turns off **Spawn Automatically**, so the entity only appears
  when someone redeems. Your original auto-spawn setting is remembered and
  comes back when you switch Twitch off. The Spawn button still works for
  testing.
- Redeeming the trigger reward starts a ransom encounter, same as pressing
  **Spawn** in the config window. This one is dodgeable: the entity shows a
  warning, and if you leave the cursor perfectly still and don't press anything,
  it goes away.
- Redeeming the force reward starts the same encounter but **ignores that
  check**, so the attack lands even on someone who never touched the cursor.
  Intended as the expensive premium version of the trigger reward.
- Redeeming the dismiss reward clears the active ransom early. The spawn
  rewards are also accepted while one is already running, but are ignored until
  the current one is solved or dismissed, so you can never stack two.
- Redemptions arrive over Twitch's EventSub WebSocket API, which needs the
  `channel:read:redemptions` scope. Twitch sends a keepalive about every ten
  seconds and closes the socket if the app stops reading, so the connection is
  managed in the background and reconnects on its own.
- Connection state is written to the console, which the app opens on startup.

**Heads up:** the client secret, access token, and refresh token are stored in
plaintext in `config.json` next to the executable. That file is effectively a
password — don't commit it or share it, and revoke the app in the Twitch console
if it leaks.

Images, sounds, and taunt window titles live in `Properties/Resources.resx`
and the `Resources/`/`Assets/` folders if you want to swap them out.

## Credits

- **Original project:** RANS0M by **Ixar** — <https://github.com/Ixars/ransomdoors>.
  All original design, code, and assets.
- **This fork:** modified by **KMT Studio**, which added the Twitch Channel Points
  integration, rewrote the config layer, and applied assorted fixes and
  optimizations. Released under the original project's terms, unchanged.
- **Doors** is made by **LSPLASH**. The RANSOM/A-90 entity, its name, look,
  and concept are their original work — this project is an unofficial fan
  recreation, not affiliated with or endorsed by LSPLASH. Go play the real
  game.
- Built with [NAudio](https://github.com/naudio/NAudio) for audio playback.
- Sound effects and images are from the game, taken from the wikis.

## License

Source-available, free to use/modify/redistribute for educational and
non-commercial purposes, with credit required and reselling (original or
modified) forbidden. Full terms in [LICENSE.md](LICENSE.md) — read it, it's
short. This fork is distributed under those same terms and does not modify
them.

Fork modifications © 2026 **KMT Studio**. The original work remains
© 2026 **Ixar**; the original copyright notice and license are retained
unchanged.
