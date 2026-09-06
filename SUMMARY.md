Developer Overview: Custom Launchers for Recoil Engine
1. Architectural Paradigm
The Beyond All Reason (BAR) ecosystem uses a decoupled architecture. The core Recoil Engine (C++23 fork of Spring 105.0) strictly handles deterministic simulation and rendering. External Launchers manage the state machine: updating assets, handling authentication, generating match configurations, and orchestrating subprocesses.   

2. Virtual File System (VFS) and Isolation
The engine mounts a data-dir containing read-only assets and read-write configuration states.

VFS Structure:

engine/: Contains specific engine binaries (e.g., 105.1.1-2511-g747f18b BAR105).   

games/ & maps/: Game logic (.sdd/.sdz) and map files (.smf).   

pool/, packages/, rapid/: Stores hashed blobs for the "Rapid" deduplication CDN.   

Execution Isolation: To prevent file lock collisions during concurrent multi-client/autohost testing, launchers must enforce sandboxing using CLI flags.   

--isolation: Ignores OS-level user profiles (~/.local/state/BAR).   

--write-dir <path>: Sets the mutable directory for infolog.txt, springsettings.cfg, and chobby_config.json.   

Concurrent Symlinking: For multiple instances, create a temp folder (e.g., data2), symlink the read-only asset folders, copy mutable configs, and run with --write-dir ..

3. Asset Synchronization (pr-downloader)
Launchers must invoke pr-downloader as a subprocess to fetch game data before executing the engine.   

Targeting: Use Rapid tags (e.g., byar:test).   

Command: pr-downloader dev-game:test "Angel Crossing 1.4".   

Env Var	Description	Typical Value
PRD_RAPID_REPO_MASTER	Upstream package resolution	
https://repos-cdn.beyondallreason.dev/repos.gz

PRD_HTTP_SEARCH_URL	Fallback HTTP search for maps	
https://files-cdn.beyondallreason.dev/find

PRD_RAPID_USE_STREAMER	Toggles HTTP streaming	
"false"

4. Engine Execution Pipeline (spring.exe)
The engine is launched via subprocess calls constructed dynamically.

Lobby/UI Mode: Boot into Chobby (Lua UI) bypassing a direct match.   

spring.exe --isolation --write-dir <path> --menu rapid://byar-chobby:test

Headless/Autohost Mode: Run without GPU/audio initialization for servers/CI.   

spring.exe --isolation --write-dir <path> --no-gui spring://Player:pass@127.0.0.1:20001

5. Match Initialization (script.txt)
To bypass the UI and launch a direct match/skirmish, the launcher generates script.txt, a pseudo-INI file defining deterministic lockstep state.   

Block	Parameter	Description
[GAME]	MapName / GameType	
Defines environment (e.g., .smf map and byar:test mod).

[GAME]	IsHost / HostIP	
Defines network topology. Server: IsHost=1. Client: HostIP=<addr>.

[PLAYERx]	Name / Team	
Human player display name and assigned faction ID.

[TEAMx]	TeamLeader / AllyTeam	
Player ID owning the faction and diplomatic alliance group.

[ALLYTEAMx]	NumAllies	
Number of teams in this specific alliance.

[AIx]	ShortName	
Native library/Lua script (e.g., BARbarianAI).

[MODOPTIONS]	StartMetal / GameMode	
Injected dynamically into Lua rulesets (e.g., initial resources, win condition).

