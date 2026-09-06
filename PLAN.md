# BAR Advanced Launcher 2 — Implementation Plan

A developer-focused launcher for **Beyond All Reason** / the **Recoil** engine.
Rewrite of `BAR Advanced Launcher` (WPF) as a **WinUI 3 + MVVM** app.

---

## 1. Goal

Give a BAR developer a single window from which they can:

- point at (or auto-detect) a BAR data directory,
- see every engine / game / map that install contains,
- launch **any combination** of them, either through Chobby or straight into a match,
- keep a **library of start scripts** they can create, edit, duplicate and re-run,
- recover the **last start script the engine actually ran** out of `infolog.txt` (or is `data\_script.txt` a better source?).

Everything must survive the fact that a dev machine has several engines side by
side, some of them developent builds that do not have all the binaries like pr-downloader.exe, just use a random real engine release if you need to use those binaries.

---

## 2. Ground truth (verified on this machine)

These are not assumptions — they were checked against the real install and the
old launcher's source before writing this plan.

### 2.1 Install layout

Root: `%LOCALAPPDATA%\Programs\Beyond-All-Reason\`

| Path | Contents |
| --- | --- |
| `Beyond-All-Reason.exe` | Electron launcher (not ours) |
| `bin\pr-downloader.exe` | Downloader shipped next to the Electron app |
| `infolog.txt` | Log of the **last non-isolated** run |
| `data\` | The engine data-dir (`--write-dir` target) |
| `data\engine\<name>\` | One folder per engine build |
| `data\games\` | `BAR.sdd`, `BYAR-Chobby.sdd` (source checkouts) |
| `data\maps\` | 126 maps as `*.sd7` (+ a `.md5.gz` sidecar each → 252 files) |
| `data\packages\`, `data\pool\`, `data\rapid\` | Rapid CDN blobs and repo indexes |
| `data\cache\ArchiveCache22.lua` | **The archive index — 10 MB** |
| `data\infolog.txt` | Log of the last **isolated** run (what we launch) |
| `data\log\*_infolog.txt` | Rotated historical logs |
| `data\_script.txt` | Start script **Chobby writes** when it starts a skirmish |

### 2.2 Engines are not uniform — this drove several design decisions

```
data\engine\
  development          <- ONLY spring.exe. No pr-downloader, no headless.
  recoil_2025.06.11
  ...
  recoil_2026.07.04    <- spring.exe, spring-headless.exe,
                          spring-dedicated.exe, pr-downloader.exe
```

A local dev build (`development`) is missing most binaries. The launcher must
**probe each engine folder for capabilities** rather than assume `spring.exe`
plus friends are present, and must grey out (not crash on) actions that need a
binary the selected engine does not have.

### 2.3 `ArchiveCache22.lua` is the map/game/menu index

It is a Lua file ending in `return archiveCache`, so it can be executed and its
return value read. Entries look like:

```lua
{
  name = "adamantium_factory_v1.sd7",
  path = "C:/.../data/maps/",
  modified = "1780776370",
  checksum = "9fc7ad65...",
  archivedata = {
    author = "[teh]Beherith (mysterme@gmail.com)",
    description = "64 player metal map by [teh]Beherith",
    mapfile = "maps/Adamantium_Factory_V1.smf",
    modtype = 3,
    name = "AcidicQuarry 5.17",
    name_pure = "AcidicQuarry",
    version = "5.17",
    maxmetal = 4.9, gravity = 100.0, tidalstrength = 0.0,
    extractorradius = 24.0, maphardness = 200.0,
  },
}
```

`modtype` is the discriminator, confirmed by counting this file:

| modtype | meaning | count here |
| --- | --- | --- |
| 1 | Game | 7 |
| 3 | Map | 126 |
| 4 | Base content | 4 |
| 5 | Menu (Chobby) | 5 |

The `name` field is what goes into a start script's `mapname` / `gametype`, e.g.
`mapname=Quicksilver Remake 1.24;`, `gametype=Beyond All Reason $VERSION;`.

**`$VERSION` is literal.** A `.sdd` source checkout has `version = '$VERSION'` in
its `modinfo.lua`, and the engine resolves it. Do not "fix" it.

### 2.4 The infolog start-script line

The engine logs exactly one line we care about:

```
[t=00:00:01.004996][f=-000001] [StartScript] Loading StartScript from: C:\Users\wybre\AppData\Local\BAR Advanced Launcher\StartScripts\generated.txt
```

One historical log contains:

```
[StartScript] Loading StartScript from: All
```

That is a **bug artefact**: a script path was passed unquoted and the engine took
everything up to the first space (a map name beginning "All That…"). Lesson for
us: **every path argument must be quoted**, and the infolog parser should treat a
value that does not resolve to an existing file as suspect rather than as truth.

Menu launches (`--menu`) emit **no** such line. For a Chobby-started skirmish the
real script is `data\_script.txt`, which Chobby rewrites on each launch. So
"recover the last start script" is two sources, not one.

### 2.5 What the old launcher already got right (reuse the logic)

From `BAR Advanced Launcher/BAR Advanced Launcher/`:

- `Services/BarGameDirectoryService.cs` — the `%LOCALAPPDATA%\Programs\Beyond-All-Reason` probe and engine enumeration.
- `Services/LuaCacheParser.cs` — MoonSharp `DoString` on the newest `*.lua` in `data\cache`, switch on `modtype`.
- `Services/UserPreferencesService.cs` — JSON prefs in `%LOCALAPPDATA%\BAR Advanced Launcher\`.
- `Services/StartScriptGeneratorService.cs` — a hardcoded template with `%%GAMETYPE%%` / `%%MAPNAME%%` / `%%MODOPTIONS%%` placeholders.
- `ViewModel/MainViewModel.cs` — the launch argument shape:
  `--write-dir "<bar>\data" --isolation --menu "<menuName>"` and
  `--write-dir "<bar>\data" --isolation "<scriptPath>"`.

Known weaknesses to fix in the rewrite:

1. `LoadPreferences()` is called **twice** in the constructor to work around
   ordering between "engines loaded" and "preferences restored". Replace with an
   explicit async initialisation sequence.
2. All I/O is synchronous on the UI thread, including the 10 MB Lua parse.
3. `catch (Exception ex) { }` swallowing errors silently in `FindEngines`.
4. The generated script is always written to the same `generated.txt`, so
   generating twice destroys the previous one.
5. Only one hardcoded 1v1-vs-SimpleAI template; no real editing.
6. `SelectedGame` / `SelectedMap` are `string` and never populated from the Lua
   cache in the UI, so `GenerateStartScript` could write empty values.

---

## 3. Technology decisions

| Concern | Decision | Why |
| --- | --- | --- |
| UI | WinUI 3, Windows App SDK 2.4.0, `net8.0-windows10.0.19041.0` | Already scaffolded |
| MVVM | `CommunityToolkit.Mvvm` 8.4+ (`[ObservableProperty]`, `[RelayCommand]`) | Same as old app, source-generated |
| DI | `Microsoft.Extensions.DependencyInjection` + `Microsoft.Extensions.Hosting` | Services + typed options |
| Lua | `MoonSharp` 2.0.0 | Proven against this exact cache file |
| Logging | `Microsoft.Extensions.Logging` → file + in-app log pane | Replaces the swallowed exceptions |
| Tests | xUnit, services only (no UI tests) | Parsers are the risky part |

### 3.1 Three WinUI gotchas to handle up front

1. **`PublishTrimmed=True` (already set for Release) will break MoonSharp.**
   MoonSharp is reflection-heavy. Either set `<PublishTrimmed>false</PublishTrimmed>`
   or add a `TrimmerRootAssembly` entry for MoonSharp. Decide in Phase 0 —
   discovering this at first Release build is a bad time.
2. **No `Microsoft.Win32.OpenFolderDialog`.** The old app used the WPF dialog.
   WinUI needs `Windows.Storage.Pickers.FolderPicker` initialised with the window
   handle via `WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd)`.
   Wrap this in an `IDialogService` so view models stay testable.
3. **Unpackaged vs packaged.** Packaged (MSIX) runs with restricted file access
   and `broadFileSystemAccess` friction against a folder the user picks anywhere.
   **Recommendation: ship unpackaged** (`WindowsPackageType=None`), which is the
   right fit for a dev tool. Keep MSIX tooling in the csproj but do not rely on it.

---

## 4. Project layout

```
BAR Advanced Launcher 2/
  App.xaml(.cs)                     host builder, DI, global error handler
  MainWindow.xaml(.cs)              NavigationView shell
  Models/
    BarInstallation.cs              root + data dir + validity
    EngineBuild.cs                  name, path, capabilities
    GameArchive.cs                  from modtype 1
    MapArchive.cs                   from modtype 3 (+ metadata)
    MenuArchive.cs                  from modtype 5
    ArchiveIndex.cs                 the parsed cache as a whole
    StartScript.cs                  name, path, content, parsed model
    StartScriptModel.cs             typed [GAME]/[PLAYERn]/[AIn]/[TEAMn]/...
    LaunchProfile.cs                engine + script/menu + flags + write-dir
    LaunchRecord.cs                 history entry
    AppSettings.cs                  persisted preferences
  Services/
    IBarInstallationLocator.cs      detect / validate / remember install
    IEngineCatalog.cs               enumerate engines + probe capabilities
    IArchiveCatalog.cs              maps / games / menus (cached)
    IArchiveCacheParser.cs          MoonSharp over ArchiveCache*.lua
    IStartScriptStore.cs            CRUD over the script library
    IStartScriptSerializer.cs       parse + write the pseudo-INI format
    IStartScriptFactory.cs          build a script from a selection
    IInfologParser.cs               last-script + last-run facts
    ILaunchService.cs               build args, start process, track it
    ISettingsService.cs             JSON preferences
    IDialogService.cs               folder picker / message dialogs
    IShellService.cs                open folder / open file in editor
  ViewModels/
    ShellViewModel.cs
    LaunchViewModel.cs              the main "pick and play" page
    ScriptsViewModel.cs             the script library
    ScriptEditorViewModel.cs
    ContentViewModel.cs             maps / games / engines browser
    SettingsViewModel.cs
    LogViewModel.cs
  Views/                            one XAML page per view model
  Converters/, Controls/
  Tests/  (separate project)
```

**Rule:** view models never touch `System.IO`, `Process`, or WinRT pickers
directly — always through an interface. That is what makes the parsers testable
against the real files captured from this machine.

---

## 5. Feature specifications

### 5.1 Detect the game directory

`IBarInstallationLocator` resolves candidates **in order**, first valid wins:

1. Path saved in settings (if it still validates).
2. `%LOCALAPPDATA%\Programs\Beyond-All-Reason` — the old launcher's only probe.
3. Registry uninstall keys (`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\*`)
   matching `Beyond-All-Reason` → `InstallLocation`. Catches non-default installs.
4. `%ProgramFiles%\Beyond-All-Reason`.
5. A running `Beyond-All-Reason.exe` / `spring.exe` process → `MainModule.FileName`
   → walk up to the install root. Catches portable copies.
6. Manual pick via `FolderPicker`.

**Validation** — a directory is a BAR install if `data\engine\` exists and
contains at least one subfolder holding `spring.exe`. Report *why* a candidate
failed instead of returning a bare `null`; the old service returned `null` with
no explanation.

Also support **multiple installs**: keep a list in settings with one marked
active, and a switcher in the shell header. Devs commonly have a stable install
and a test install.

### 5.2 Open the folder

`IShellService.OpenFolder(path)` → `Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })`.

Expose a split button with the useful targets, not just one:
install root · `data\` · `data\engine\<selected>` · `data\maps` · `data\games` ·
script library · `data\infolog.txt` · `springsettings.cfg`.

### 5.3 Detect engines

`IEngineCatalog.Discover(install)` enumerates `data\engine\*` and for each folder
records which binaries exist:

```csharp
record EngineCapabilities(
    bool HasSpring,          // spring.exe
    bool HasHeadless,        // spring-headless.exe
    bool HasDedicated,       // spring-dedicated.exe
    bool HasPrDownloader,    // pr-downloader.exe
    bool HasUnitsync);       // unitsync.dll
```

Verified: `development` has only `spring.exe` + `unitsync.dll`; `recoil_2026.07.04`
has all four executables. Commands bind their `CanExecute` to these flags.

Read the file version / build stamp off `spring.exe` where available, and sort
newest-first so the freshest build is the default selection.

### 5.4 / 5.5 Detect maps and games

`IArchiveCacheParser` runs MoonSharp over the newest `ArchiveCache*.lua` in
`data\cache`, then buckets by `modtype` (1 → game, 3 → map, 5 → menu), capturing
the full `archivedata` table rather than just `name`.

Because the file is **10 MB**:

- parse **off the UI thread**, show a progress ring,
- memoise the result to `%LOCALAPPDATA%\BAR Advanced Launcher 2\index.json`,
  keyed on the cache file's path + size + `LastWriteTimeUtc`; re-parse only when
  that key changes,
- offer an explicit **Refresh** button (the engine rewrites the cache after a
  content download).

Cross-check against the filesystem: list `data\maps\*.sd7|*.sdz` and
`data\games\*` and flag anything present on disk but missing from the cache as
"not indexed — run the engine once". Ignore `*.md5.gz` sidecars (they are half
the file count in `data\maps`).

If the cache is missing entirely, fall back to filenames so the app is still
usable, clearly marked as degraded.

### 5.6 Launch an engine + game + map combination

The **Launch page**: three searchable pickers (engine, game, map) plus a mode
selector, and a big Launch button.

Modes:

| Mode | Arguments |
| --- | --- |
| Menu (Chobby) | `--write-dir "<data>" --isolation --menu "<menuName>"` |
| Skirmish | `--write-dir "<data>" --isolation "<generatedScript>"` |
| Named script | `--write-dir "<data>" --isolation "<scriptPath>"` |
| Headless | `spring-headless.exe` + same script |
| Dedicated | `spring-dedicated.exe` + script (host only) |

Skirmish mode synthesises a script from the current selection via
`IStartScriptFactory`, writes it to a temp file, and launches it — the user never
has to author a script for the common case.

**Argument quoting is mandatory** — see §2.4. Every path is wrapped in quotes;
prefer `ProcessStartInfo.ArgumentList` so the runtime escapes for us.

**Environment matters as much as arguments.** pr-downloader is compiled into the
engine and reads its repository settings from the environment only — not from the
command line, not from `springsettings.cfg`. With nothing set it falls back to the
built-in default `repos.springrts.com`, which carries no Beyond All Reason content,
so in-game downloads fail while the same engine started by the official launcher
works. Verified by running the install's own `pr-downloader.exe` both ways: without
the variables it reached `springfiles.springrts.com` and found nothing; with them it
reached `repos-cdn.beyondallreason.dev` and pulled 353 MB.

`IEngineEnvironment` reads the `env_variables` block out of the install's own
`data\config.json` — the official launcher's config, which it keeps up to date — and
`LaunchService` copies those onto every run. The values are deliberately *not*
hard-coded, so a moved CDN reaches this launcher without a code change. On this
machine the block is:

| Variable | Value |
| --- | --- |
| `PRD_HTTP_SEARCH_URL` | `https://files-cdn.beyondallreason.dev/find` |
| `PRD_RAPID_USE_STREAMER` | `false` |
| `PRD_RAPID_REPO_MASTER` | `https://repos-cdn.beyondallreason.dev/repos.gz` |

The same config's `launch.springsettings` writes `RapidTagResolutionOrder` into
`springsettings.cfg`, which already persists on disk — so it needs nothing from this
app, but a `--config` override pointing somewhere else would lose it.

Still an open difference: the official launcher passes `--menu rapid://byar-chobby:test`
where this app passes the resolved archive name from the cache. The resolved name pins
the run to one build, which is what a dev launcher usually wants; the rapid tag lets
the engine fetch and update Chobby itself. Worth offering both in the menu picker.

**Chobby does not download anything itself when it thinks a launcher is listening.**
The official launcher runs a local control socket and writes its address into
`<write-dir>\sl-connection.json`. Chobby's `api_download_handler` hands every request
to `WG.WrapperLoopback`, which posts it over that socket; `VFS.DownloadArchive` — the
engine's own pr-downloader — is only used when the wrapper is absent.

The trap is that `api_spring_launcher` stands the connector down *only when the file's
host and port are missing*. A failed connect logs an error and removes the widget but
never clears `Connector.enabled`, so a file left behind by the official launcher makes
Chobby post every download to a port nobody is listening on, with no fallback and no
visible error. Seen exactly that way: a run at 10:56 logged
`[spring-launcher] Connecting to 127.0.0.1:65176`, the port from a file the official
launcher had written at 10:44.

So `LaunchService` moves `sl-connection.json` aside before a menu launch — renamed to
`sl-connection.json.disabled`, not deleted, and the official launcher rewrites it on its
next start. Chobby then logs `spring-launcher doesn't exist` and downloads through the
engine, which works because the run carries the install's `PRD_*` variables. The two
fixes are a pair: neither is sufficient alone.

The fuller answer is to implement the socket protocol — `Download`, `DownloadProgress`,
`DownloadFinished` and friends over JSON — so Chobby's own download UI works with
progress instead of falling back. That belongs with §6.5's pr-downloader integration.

Two red herrings ruled out along the way, both by comparing against the official
launcher's own infologs: `--menu rapid://byar-chobby:test` behaves identically to the
resolved archive name, and the
`self.gameConfig._defaultGameRapidTag not present` error appears in the official
launcher's sessions too.

**Run options** are per-profile switches surfaced on the Launch page, taken from the
option table inside `spring.exe` itself rather than from documentation:

| Switch | Control | Engine's own description |
| --- | --- | --- |
| `--isolation` | checkbox, on by default | Limit the data-dir (games & maps) scanner to one directory |
| `--safemode` | checkbox | Turns off many things that are known to cause problems |
| `--only-local` | checkbox | Force OnlyLocal mode (no network listening sockets) |
| `--window` / `--fullscreen` | three-way combo | Run in windowed / fullscreen mode |
| `--config <file>` | path + file picker | Exclusive configuration file |
| `--write-dir <dir>` | path + folder picker | Specify where Spring writes to |

`spring-dedicated.exe` builds a reduced option table — its strings carry only
`config`, `isolation` and `menu` — and the engine refuses to start on an option it
does not know, so the four client switches are dropped for Dedicated mode and the
page greys them out. Deliberately *not* surfaced, because the extra-arguments box
already covers them and each would duplicate something the app already sets:
`--name` (the script's `myplayername`), `--game` / `--map` (Phase 5's skirmish
selection), `--isolation-dir` (a second, subtly different isolation concept),
`--nocolor` (only meaningful once stdout is captured — revisit with §6.1), and the
`--list-*` / `--calc-checksum` diagnostics, which exit instead of launching and
belong in a diagnostics feature rather than behind a Launch button.

The Scripts page carries its own isolation checkbox, persisted in `settings.json`
rather than on a profile: launching from the editor means "run this file", not "run
my configured setup".

### 5.7 Start script management

**Library location:** `%LOCALAPPDATA%\BAR Advanced Launcher 2\StartScripts\*.txt`,
with the old app's folder imported on first run so existing scripts carry over.

`IStartScriptSerializer` handles the pseudo-INI format properly — nested
`[section] { ... }` blocks, `key=value;` lines, arbitrary nesting depth. Round-trip
safe: parse → model → write must reproduce a semantically identical file.
`StartScriptModel` exposes `[GAME]` scalars (`mapname`, `gametype`,
`myplayername`, `ishost`, `hostip`, `hostport`, `startpostype`, `nohelperais`,
`gamestartdelay`) plus collections of `PLAYERn`, `AIn`, `TEAMn`, `ALLYTEAMn`, and
a `MODOPTIONS` dictionary.

Operations: create from template · duplicate · rename · delete (to recycle bin) ·
open in external editor · reveal in Explorer · **set as default**.

**The default script** is the one the shell's primary button runs. Ship with a
built-in "Chobby (menu)" default so a fresh install launches the game the way the
task requires, without the user configuring anything.

**Editor page** — two-way:

- *Form view*: map / game dropdowns bound to the real catalog, a player+AI team
  builder (add AI, pick `shortname`, assign team/allyteam, side, colour),
  mod-options key/value grid, start-position type.
- *Raw view*: a monospace text box over the file, so anything the form does not
  model is still editable. Switching views reparses; a parse error is shown
  inline instead of silently discarding the text.

Templates to ship: *1v1 vs AI*, *AI vs AI (spectate)*, *Empty sandbox*,
*Local multiplayer host*. The old `godless_vs_barb.txt` / `ai_ai.txt` /
`vs_ai.txt` names in the logs show these are exactly the shapes actually used.

### 5.8 Recover the last start script from `infolog.txt`

`IInfologParser` reads, in order of recency:

1. `data\infolog.txt` (isolated runs — what this launcher produces),
2. `<root>\infolog.txt` (non-isolated runs),
3. `data\log\*_infolog.txt` (rotated history).

It extracts:

- **Start script path** from `[StartScript] Loading StartScript from: <path>`.
  If the value does not resolve to an existing file, mark it *unresolved* — this
  is the "`… from: All`" case from §2.4, and it must not be presented as a real
  script.
- **Chobby-generated script**: if the run was a `--menu` launch there is no such
  line, so fall back to `data\_script.txt`, whose `mtime` tells us when Chobby
  last wrote it. This is how you capture a game that was set up in the menu.
- Supporting facts worth surfacing: engine build, write-dir, isolation on/off,
  `gametype`, `mapname`, and any `[Error]` / `Exception` lines.

The UI then offers:

- **Re-run** — launch it again as-is.
- **Import to library** — copy the file contents into the script library under a
  chosen name, so a Chobby-configured match becomes a reusable dev script. This
  is the single most valuable feature here: it turns the menu into a script
  authoring tool.
- **Diff** — compare against the library copy if a script of that name exists.

Read logs with `FileShare.ReadWrite` — the engine may still hold the handle.

### 5.9 Launch profiles

A start script says what the *match* is: `mapname`, `gametype`, players, AIs, mod
options. It says nothing about how the engine is invoked. A profile is the rest —
a named bundle that makes a run reproducible in one click:

| Stored | Notes |
| --- | --- |
| Mode | Menu / Script / Headless / Dedicated, which also picks the binary |
| Menu, script | The archive or library file the mode runs |
| Engine | Optional. Null means "follow the page" — see below |
| `--isolation`, `--safemode`, `--only-local` | The run options from §5.6 |
| Window mode, `--config`, `--write-dir` | |
| Extra arguments | Passed through verbatim |
| Install environment | Whether the `PRD_*` variables are applied |

Stored in `profiles.json` beside `settings.json`, indented and with the enums
written as names, so it stays hand-editable — worth having for a dev tool.

**Profiles are their own section on the Launch page**, above the pickers they
govern, with the full set of operations: **Save**, **Reload**, **Save as…**,
**New…**, **Delete**, and, under *More*, **Rename…** and **Set as default**.

Three decisions worth recording, because each rules out an obvious alternative:

1. **Edits are held on the page until Save.** The first cut committed every
   toggle straight to `profiles.json`. That makes a one-off run — ticking safe
   mode once to see whether a build starts — permanently rewrite the profile the
   user launches from every day. Explicit save also gives Reload something to
   revert to. A `•` in the section header marks unsaved edits.
2. **Save stays enabled even when nothing is dirty.** A profile that leaves the
   menu or script null follows whatever the page has selected; pinning that
   current selection is a real thing to want, and a dirty-gated Save could never
   express it. Only Reload follows the dirty flag.
3. **The dirty marker compares against the page as it loaded, not against the
   stored profile.** Those differ: the profile leaves the menu and script null to
   mean "follow the page", and the pickers then default to their first entry —
   so comparing against the stored profile makes an untouched Chobby profile show
   as edited the moment it loads. Caught by running the app, not by a test.

**Engine pinning** is a checkbox rather than implicit. Off — the default — leaves
`EngineName` null so the profile keeps working across an engine upgrade; on
records the selected build, for a profile that means "reproduce this on
2026.06.11". Before this, `LaunchProfile.EngineName` existed but nothing ever
wrote it.

Deleting the built-in Chobby profile is refused in the store, not just greyed out
in the UI: it is what a fresh install launches with, and `profiles.json` is
hand-editable.

---

## 6. Recommended additions

Beyond the requested scope. Ordered by value-for-effort.

1. **Launch history.** Every launch recorded (engine, script snapshot, args,
   exit code, duration, infolog path). One-click replay of any past run. A dev
   relaunches the same combination dozens of times a day; this is the feature
   that saves the most clicks.
2. ~~**Launch profiles.**~~ Promoted out of this list and specified in §5.9;
   built, with management on the Launch page.
3. **Live infolog viewer.** Tail `data\infolog.txt` while the game runs, with
   error highlighting and filtering. Removes the alt-tab-to-notepad loop.
4. **Multi-instance / isolated sandboxes.** Per `SUMMARY.md` §2: create
   `data2`, `data3`, symlink the read-only asset folders (`engine`, `games`,
   `maps`, `pool`, `packages`), copy the mutable configs, and launch with a
   distinct `--write-dir`. Enables running host + client + autohost concurrently
   without file-lock collisions. Note: symlink creation needs Developer Mode or
   elevation — detect and explain, and fall back to junctions for directories,
   which do not require elevation.
5. **pr-downloader integration.** Per `SUMMARY.md` §3, run
   `pr-downloader.exe --download-game <rapid-tag>` / `--download-map "<name>"`
   as a subprocess with `PRD_RAPID_REPO_MASTER`, `PRD_HTTP_SEARCH_URL`,
   `PRD_RAPID_USE_STREAMER` set, streaming output into the log pane. Read the
   available tags from `data\rapid\repos-cdn.beyondallreason.dev\{byar,byar-chobby}\versions.gz`
   (gzip, present on this machine) to populate a picker instead of making the
   user type tags.
6. **Map detail panel.** The cache already carries `author`, `description`,
   `maxmetal`, `gravity`, `tidalstrength`, `extractorradius`, `maphardness`,
   `mapfile`. Show them, and extract the minimap from the `.sd7` (7-zip archive)
   for a thumbnail grid. Makes map selection visual rather than a 126-item
   dropdown.
7. **Process supervision.** Track spawned engines, show running instances, allow
   kill, surface non-zero exit codes with a jump to the failing infolog line.
8. **`springsettings.cfg` editor.** Read/write the config the engine actually
   uses, with a diff against defaults. Devs edit this by hand constantly.
9. **Favourites and recency.** `data\favourite_maps.txt` already exists in the
   install — read it, and let starred maps/engines sort to the top.
10. **Command-line passthrough.** A free-text extra-arguments box per profile,
    plus a "copy full command line" button for pasting into a terminal or a bug
    report.
11. **Crash triage.** After a non-zero exit, scan the infolog for the last error
    block and offer "copy report" with engine, script, and stack.

---

## 7. UI shape

`MainWindow` hosts a `NavigationView`:

| Page | Purpose |
| --- | --- |
| **Launch** | Launch button and command preview, install header, **profile section** (§5.9), what-to-run pickers, run options, running instances, "last run" card with Re-run / Import |
| **Scripts** | Library list, default marker, editor (form + raw tabs) |
| **Content** | Maps / Games / Engines browsers with detail panes |
| **History** | Past launches, replay, jump to infolog |
| **Log** | App log + live engine infolog tail |
| **Settings** | Installs, paths, downloader env vars, theme |

Keep the existing `MicaBackdrop`. Use `InfoBar` for recoverable problems (no
install found, cache missing, engine lacks a binary) — the old app's single
`StatusMessage` string could only show one thing at a time.

---

## 8. Phases

**Phase 0 — Foundation**
Add `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection` +
`Hosting`, `MoonSharp`. Wire the host builder in `App.xaml.cs`. Resolve the
`PublishTrimmed` / MoonSharp question (§3.1). Build the `NavigationView` shell.
Add the test project. *Done when: empty shell navigates and DI resolves a stub.*

**Phase 1 — Discovery**
`IBarInstallationLocator`, `IEngineCatalog` with capability probing,
`ISettingsService`, `IDialogService` folder picker, `IShellService` open-folder.
*Done when: the app finds the install unaided, lists all 9 engines with correct
capability flags, and Open Folder works.*

**Phase 2 — Content catalog**
`IArchiveCacheParser` + `IArchiveCatalog` with background parse, `index.json`
memoisation, filesystem cross-check, Content page.
*Done when: 126 maps, 7 games, 5 menus listed; second start is instant.*

**Phase 3 — Launching**
`ILaunchService` with `ArgumentList` quoting, menu mode and named-script mode,
process tracking. Default Chobby profile.
*Done when: Chobby launches on the selected engine, and a library script runs.*

**Phase 4 — Script library**
`IStartScriptStore`, `IStartScriptSerializer` (round-trip tested against the real
`_script.txt` and `bar_debug_launcher_script.txt` captured from this machine),
`IStartScriptFactory`, Scripts page with raw editor, import of the old app's
folder. *Done when: a script can be created, edited, saved, and launched.*

**Phase 5 — Skirmish generation**
Form editor over `StartScriptModel`, team/AI builder, mod-options grid, and
"launch this engine+game+map now" without hand-authoring.
*Done when: picking three dropdowns and pressing Launch starts a real match.*

**Phase 6 — Infolog**
`IInfologParser` across all three log locations, `_script.txt` fallback,
unresolved-path handling, Re-run / Import / Diff, launch history.
*Done when: a match set up in Chobby can be imported and re-run from the app.*

**Phase 7 — Extras**
In priority order from §6: live log viewer → sandboxes → pr-downloader →
map thumbnails → springsettings editor.

---

## 9. Risks and open questions

| Risk | Mitigation |
| --- | --- |
| MoonSharp broken by Release trimming | Decide in Phase 0; disable trimming or root the assembly |
| 10 MB Lua parse blocks the UI | Background thread + memoised `index.json` |
| Cache schema bumps (`ArchiveCache22` → `23`) | Match `ArchiveCache*.lua` by glob and newest mtime, as the old parser did; treat unknown `modtype` as "other" rather than throwing |
| Symlinks need elevation | Prefer directory junctions; detect Developer Mode and explain |
| Engine holds log file handles | Open with `FileShare.ReadWrite` |
| Packaged MSIX file-access friction | Ship unpackaged |
| Paths with spaces | `ProcessStartInfo.ArgumentList` everywhere — this already caused the "`from: All`" corruption in a real log |

**Decisions (settled 2026-09-05):**

1. **Ship unpackaged.** `WindowsPackageType=None` is set; MSIX tooling stays in the
   csproj but nothing depends on it.
2. **Launch profiles are the mechanism; the default *is* a profile.** There is no
   separate "default start script" setting — the shell's primary button runs the
   pinned profile, and the shipped Chobby default is a profile like any other.
3. **Multi-instance sandboxing is out of scope for v1.** It stays in §6.4 as a
   Phase 7 item; nothing in Phases 1–6 depends on it.
4. **The script library starts empty.** The old app's
   `%LOCALAPPDATA%\BAR Advanced Launcher\StartScripts` folder is neither imported
   nor shared, so the two apps cannot fight over the same files. This supersedes the
   "with the old app's folder imported on first run" sentence in §5.7.

---

## 10. Progress

| Phase | State |
| --- | --- |
| 0 — Foundation | **Done.** DI host, logging to file + in-app pane, NavigationView shell, global error handlers, xUnit project (25 tests). Trimming disabled for MoonSharp (§3.1.1). |
| 1 — Discovery | **Done.** Six-candidate locator with per-candidate rejection reasons, engine capability probing, JSON settings with atomic writes, folder picker, Explorer integration. Verified against the real install: 9 engines, correct flags. |
| 2 — Content catalog | **Done.** MoonSharp parse of the 9.6 MB `ArchiveCache22.lua` in ~440 ms off the UI thread, memoised to a 92 KB `index.json` keyed on path+size+mtime, filesystem cross-check that ignores `.md5.gz` sidecars, degraded filename fallback, Content page with map/game/menu/engine browsers. Verified: 126 maps, 7 games, 5 menus; second start does no parse at all. |
| 3 — Launching | **Done.** `ILaunchService` over `ProcessStartInfo.ArgumentList`, menu and named-script modes, headless/dedicated gated on engine capability, launch profiles with the built-in Chobby default, command-line preview and copy, process tracking with exit codes. Verified on the real install: Chobby booted on `recoil_2026.07.04`, and a library script ran with its full path — spaces and all — intact in the infolog. |
| 4 — Script library | **Done.** `IStartScriptSerializer` over the pseudo-INI format (arbitrary nesting, comments preserved, errors carry a line number), `StartScriptDocument` + typed `StartScriptModel`, `IStartScriptFactory` with the four templates, full store CRUD with delete-to-recycle-bin and atomic BOM-free saves, Scripts page with a monospace raw editor and live parse errors. Round-trips all three real specimens on this machine. Verified live: a script created from a template launched and the engine gave team 1 to `BARb` `stable`. |
| Run options (post-Phase 4) | **Done.** `--isolation`, `--safemode`, `--only-local`, `--window`/`--fullscreen`, `--config` and the `--write-dir` override are per-profile controls on the Launch page, with an isolation checkbox on the Scripts page too. Switch names and descriptions were read out of `spring.exe`'s own option table; the four client switches are dropped for Dedicated, whose binary does not register them. Verified against a real engine run: `--config` moved the writeable configuration source to a path with a space in it, `--window` produced `windowed::decorated` where the same run without it gave `fullscreen::exclusive`, `--write-dir` put the infolog in the chosen folder, and `--isolation` cut `Documents\My Games\Spring` out of the read-only data directories. Also fixed here: text saved from the raw editor kept WinUI's bare-CR line endings, so a hand-edited script landed on disk as one long line. |
| Profile management (post-Phase 4) | **Done.** Profiles moved out of the "what to run" card into their own Launch page section (§5.9) with Save, Reload, Save as…, New…, Delete, Rename… and Set as default, an engine-pin checkbox, and a `•` for unsaved edits. Page edits are no longer committed on every toggle — see §5.9 decision 1. Verified by driving the running app through UI Automation: toggling safe mode raised the marker and enabled Reload; Reload restored the checkbox and cleared both; Save as… wrote a second profile with a fresh id, `IsBuiltIn` false and the page's menu pinned, leaving the built-in untouched; Delete removed it and left Chobby pinned as the default. Fixed during that pass: the marker showed on an untouched profile at startup, because the pickers default to their first entry where the profile stores null. |
| 5–7 | Not started. |

### Deviations from the plan as written

- **§4 layout.** The archive models live in one `ArchiveEntry.cs` (a base record plus
  `MapArchive` / `GameArchive` / `MenuArchive` / `OtherArchive`) rather than one file
  each, so `index.json` round-trips through three concrete arrays with no polymorphic
  type discriminator.
- **§5.6 mode table.** Skirmish mode is declared in `LaunchMode` but rejected with an
  explanation until Phase 5 builds `IStartScriptFactory`; the Launch page offers only
  the four implemented modes.
- **§5.7 "set as default".** Replaced by pinning a profile, per §9 decision 2.
  `AppSettings.DefaultStartScriptName` is gone.
- **§5.7 form view.** The Scripts page ships the raw editor only. The form view is built
  on `StartScriptModel`, which Phase 4 delivered and tested, but the two-way form/raw
  editor belongs with the team and AI builder in Phase 5 rather than half-built here.
- **§5.7 round-trip.** "Semantically identical" rather than byte-identical: the writer
  normalises indentation to four spaces, because the two real specimens disagree about
  it (Chobby writes none, the engine's debug launcher writes four) and neither is more
  correct. Values, keys, order and comments are all preserved exactly; `Tidy` is a
  separate button so reformatting never happens behind the user's back.
- **Container validation.** `ValidateOnBuild` is on, so a missing registration fails at
  startup instead of on the navigation that first needs it. Because that throws before
  the logger exists, `OnLaunched` writes a `startup-failure.log` as a last resort.
