# KoboldKare Mod Loader

A mod manager, a small framework (ModKit) and a few mods for KoboldKare, all on [BepInEx 5](https://github.com/BepInEx/BepInEx).
Tick the mods you want and play.

**You need your own copy of KoboldKare.** This repository holds no game files. Not affiliated with the developers of KoboldKare.
These mods are adult content, like the game.

## Installing (players)

Get the zips from the [Releases page](https://github.com/scottricity/koboldkare-modloader/releases).

1. Close the game.
2. Unzip **`KoboldKare-ModLoader.zip`** into the KoboldKare folder (the one with `KoboldKare.exe`), and say yes
   to replacing files. To find the folder: right-click KoboldKare in Steam, then **Manage > Browse local files**.
3. Run **`KoboldKare Mod Manager.exe`** in that folder, tick the mods you want, and click **Save and Play**.

That zip holds BepInEx, the unstripped Mono `mscorlib`/`System`/`System.Core` that BepInEx needs on this game
(the game ships stripped copies), every mod below and the Mod Manager. It's safe to unzip again over an
older version. Your settings in `BepInEx\config` are kept.

Already have BepInEx? Use **`KoboldKare-ModManager.zip`** and each mod's zip from `mods\` (or the release)
instead. A mod's zip unzips into the game folder, or use **Add Mod...** in the Mod Manager.

To play without mods, untick **Load mods (BepInEx)** in the Mod Manager.

## Mods

| Mod | What it does | Needs |
|---|---|---|
| ModKit | Framework the other mods build on: chat commands, the F8 mod panel, settings and map-load events | |
| NPC AI | NPC kobolds roam the map and pair up at animation stations. `/npc-ai` and an F8 panel tab | ModKit |
| UI Theme | Recolours the game UI with themes. Pick one in Options > Graphics > UI Theme, the F8 panel, or `/theme`. **Create theme** (under Import theme, or in the F8 panel) opens a colour editor with a live preview; your themes keep the game's frame shapes and show up in the dropdown, and **Edit theme** changes them later. Comes with the Night Sky sample theme | ModKit |
| Voice Chat | Proximity voice chat: voices come from each player's kobold and fade with distance. Push-to-talk (**V**), toggle or open mic; volume, hearing distance, mute and a mic test (hear yourself) in the F8 panel or `/voice`. To test alone, `/voice block` (or **Place test block** in the panel) drops a cube that plays your mic back in 3D, so you can walk around it and hear the falloff. Voices behind walls sound muffled and quieter (adjustable, or off), and they pick up the map's reverb zones like other game sounds. Shows who's talking in the bottom-left corner | ModKit |
| Movie Block | A floating 3D video screen in front of you that browses **e621** or **rule34.xxx** by tag and plays the videos with 3D sound. `/movieblock e621` or `/movieblock r34` opens it; **F7** switches between using the screen (free cursor: type tags with autocomplete, click thumbnails, seek, loop, volume, click a video's tags to add or exclude them) and playing. Only you see it. Needs the Unity VideoModule (see below) | ModKit |

**Voice Chat notes:** everyone who wants to talk or listen needs the mod (players without it can still join, they
just don't hear voice). It uses Steam voice, so start the game through Steam; your mic is the one picked in
**Steam > Settings > Voice**. Voice goes over the game's own multiplayer (Photon) connection, which the game's
developer pays for, so it only goes to players who have the mod and is sent about 10 times a second while you talk.
Change the talk key with `/voice key B` (or `TalkKey` in `BepInEx/config/koboldkare.voicechat.cfg`).

**Movie Block notes:** the game ships a stripped `UnityEngine.VideoModule.dll` that can't play anything. Copy
`UnityEngine.VideoModule.dll` from the same Unity **2021.3.45f2** folder as AIModule (below) into
`BepInEx\unstripped_corlib`. It's Unity's file, so it isn't included. rule34.xxx only answers API requests with a free
key: log in, open **My Account > Options**, generate an **API Access Credentials** key, then paste the
`&api_key=...&user_id=...` text with `/movieblock r34key <text>`. e621 works without a login. Videos download to
`%TEMP%\KoboldKare-MovieBlock` (capped at 1.5 GB; `/movieblock cache clear` empties it). Content involving minors is
always filtered out; block more tags with `Blacklist` in `BepInEx/config/koboldkare.movieblock.cfg`
(default `gore scat watersports`).

**NPC AI and path-finding:** the game also ships a stripped `UnityEngine.AIModule.dll` without NavMesh path
queries. That file is Unity's, so it isn't included here. Without it NPC AI walks straight toward its target
instead of following paths. To get paths, copy `UnityEngine.AIModule.dll` from a Unity **2021.3.45f2** install
(`Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_player_nondevelopment_mono\Data\Managed`)
into `BepInEx\unstripped_corlib`.

## Mod Manager

Run `KoboldKare Mod Manager.exe` in the game folder.

- **Tick / untick** a mod, then **Save**. Off mods move to `BepInEx/plugins-disabled/` (BepInEx loads every
  DLL under `plugins/`, so a disabled mod has to live outside it). Ticking a mod also ticks the mods it needs.
- The details pane reads each DLL's `[BepInPlugin]`/`[BepInDependency]` attributes (via BepInEx's Mono.Cecil,
  without locking files) plus the assembly description (`<Description>` in the `.csproj`) or a Thunderstore
  `manifest.json`. It shows what the mod needs, what needs it, its config file, and whether it loaded last run.
- Warnings: missing or disabled dependencies, too-old versions, incompatibilities, duplicate plugin IDs, errors
  in the last `LogOutput.log`.
- **Add Mod...** (or drop files on the list) installs a `.dll` or `.zip`, laid out as `BepInEx/plugins/...`,
  `plugins/...` (Thunderstore), a single folder, or loose DLLs. A mod being replaced goes to the Recycle Bin.
- **Save** also installs anything in `ModSource/_staged` (the same thing `deploy.ps1` does).
- **Load mods (BepInEx)** turns off all mods at once (`enabled` in `doorstop_config.ini`).
- **Save and Play** starts the game through Steam, so workshop mods load too.
- Saving is blocked while KoboldKare is running, because the game locks the plugin DLLs.

# Building (modders)

Clone this repository into the game folder as `ModSource` (or pass `-p:GameDir="X:\path\to\KoboldKare"` to
`dotnet build`). The game needs BepInEx installed, since mods compile against the game's own DLLs and BepInEx's.
Open `KoboldKareMods.sln` in Visual Studio / Rider / VS Code, or use the `dotnet` CLI (.NET SDK 6 or newer).

| Folder | What it is | Installs to |
|---|---|---|
| `mods/ModKit/` | Framework plugin every mod here builds on | `BepInEx/plugins/ModKit/` |
| `mods/NpcAI/` | NPC AI mod | `BepInEx/plugins/NpcAI/` |
| `mods/UITheme/` | UI Theme mod | `BepInEx/plugins/UITheme/` |
| `mods/VoiceChat/` | Voice Chat mod | `BepInEx/plugins/VoiceChat/` |
| `mods/ExampleMod/` | Template to copy (not installed) | — |
| `mods/<Mod>.zip` | Built mod zips, made by `make_release_zips.ps1` | — |
| `ModManager/` | The Mod Manager (Windows Forms, .NET Framework 4.7.2). Building copies it to the game folder; if it's open, that copy is skipped with a warning | `KoboldKare Mod Manager.exe` |
| `ThemeSamples/` | Sample themes (Night Sky, as a folder and as an importable zip) | — |

**Test themes:** themes in `mods/UITheme/test-themes/` are local only: git ignores them and they're never bundled or
released. Every build copies them into the game's `BepInEx/themes`, so the theme loader still loads them.

**Releases:** `make_release_zips.ps1` builds everything and remakes `mods\<Mod>.zip` (committed) plus
`dist\KoboldKare-ModLoader.zip` and `dist\KoboldKare-ModManager.zip` (attached to a GitHub Release). It takes
BepInEx from the game folder's working install, and refuses to pack Unity's AIModule or any test theme.

## Build and install

The game **locks loaded plugin DLLs**, so how you build depends on whether it's running:

```bash
# Game closed: build everything and copy straight into BepInEx/plugins
dotnet build KoboldKareMods.sln -c Release

# Game running: build into ModSource/_staged instead ...
dotnet build KoboldKareMods.sln -c Release -p:Deploy=false
# ... then after closing the game:
powershell -ExecutionPolicy Bypass -File deploy.ps1
```

Start the game **from Steam** (Play button or `steam://rungameid/1102930`), not `KoboldKare.exe`, or
Steam workshop mods won't load.

## Make a new mod

```bash
powershell -ExecutionPolicy Bypass -File new-mod.ps1 -Name MyMod -DisplayName "My Mod"
dotnet build mods/MyMod -c Release
```

That copies `mods/ExampleMod` into `mods/MyMod` (project `KoboldKare.MyMod`, GUID `koboldkare.mymod`,
command `/mymod`, panel tab "My Mod") and adds it to the solution. A mod's `.csproj` is tiny; game
references and deploy steps come from `Directory.Build.props` / `Directory.Build.targets`.

## ModKit API

Add `[BepInDependency(ModKit.Guid)]` to your plugin and `using KoboldKare.ModKit;`.

**Lifecycle**: survives KoboldKare destroying plugin objects during map loads.

```csharp
ModKit.Tick      += () => { /* every frame */ };
ModKit.MapLoaded += () => { /* a map (and its chat/commands) just came up */ };
ModKit.InLevel / ModKit.IsHost / ModKit.IsAlone / ModKit.LocalKobold
```

A handler that throws is logged once and skipped; other mods keep running.

**Chat commands**

```csharp
ModCommands.Register("/hello", "[name]  Say hello.", ctx => {
    ctx.RequireHost();                       // or ctx.RequireCheats()
    if (ctx.Arg(1) == null) ctx.Fail("Usage: /hello [name]");
    ctx.Reply("Hello " + ctx.Arg(1));
}, autocomplete: (argIndex, text) => new[] { "world" });
```

`/mods` (built in) lists every ModKit command with its description and the panel tabs.

**Panel tab** (F8; hotkey in `BepInEx/config/koboldkare.modkit.cfg`)

```csharp
ModPanel.AddPage("My Mod", "ico_star", build: page => {
    var status = page.Card();                                   // live title + body text
    page.Button("Do it", "ico_lightning", DoIt);                // ButtonStyle.Primary/Okay/Cancel/Subtle
    page.Header("Settings", "ico_gear");
    page.Slider("Speed", speedCfg, 0f, 10f, 0.5f, v => $"{v:0.0} m/s");
    page.Toggle("Enabled", enabledCfg);
    page.Buttons(new ButtonSpec("Reset", "ico_trashcan", Reset, ButtonStyle.Cancel));
    page.Note("Small print that wraps to fit.");
}, refresh: () => { /* ~every 0.4 s while the tab is visible */ });
```

Sliders and toggles bind to BepInEx `ConfigEntry`s and save automatically. After changing config in
code, call `page.SyncFromConfig()` (or `ModPanel.SyncFromConfig()`). Icons are the game's own
sprites (`ico_*`, see `Assets/KoboldKare/Textures` in the game's source); not all are loaded at all
times, so `Theme.FirstAvailable("ico_refresh", "ico_save")` picks one that is. `Theme` and
`UiFactory` expose the game's colours, font and widgets for custom UI.

**Helpers**

- `Nav.TryGetPath / TrySample / TryGroundY`: NavMesh paths and ground height, with graceful fallback.
- `SelfCheck.Run(typeof(MyPlugin).Assembly, Logger)`: call in `Awake`; reports anything this game
  stripped that your code needs, by name, instead of a vague failure later.

## Gotchas (this game specifically)

- **Target `net472`**, never netstandard (the game has no `netstandard.dll`; the symptom is Unity's
  misleading *"The script '…' could not be instantiated!"*). Already set in `Directory.Build.props`.
- **The game's DLLs are stripped.** Missing members fail at compile time because we compile against
  the game's own copies. Seen so far: `Random.insideUnitCircle`, `Mathf.PI`, `Physics.DefaultRaycastLayers`,
  and the whole NavMesh query API. Use `System.Math` or literal values instead.
- `BepInEx/unstripped_corlib` holds full `mscorlib`/`System`/`System.Core` (needed by BepInEx itself) and
  `UnityEngine.AIModule` (NavMesh) and `UnityEngine.VideoModule` (Movie Block playback). Don't delete it.
- Unity's log isn't forwarded to BepInEx (stripped). Unity output is in
  `%USERPROFILE%\AppData\LocalLow\Naelstrof\KoboldKare\Player.log`; BepInEx/mod output is in
  `BepInEx/LogOutput.log`.
- Don't create GameObjects with `HideFlags.HideAndDontSave` for scripts, and don't rely on
  `DontDestroyOnLoad` objects surviving a map load; use `ModKit.Tick` / `ModKit.MapLoaded`.
- The game's source is public at [github.com/naelstrof/KoboldKare](https://github.com/naelstrof/KoboldKare) (Unity 2021.3.45f2).
