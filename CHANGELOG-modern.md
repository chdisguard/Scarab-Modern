## 2.8.4 — 2026-10-18

- Fixed API builds when Managed contains stale Mono.Cecil, MonoMod or MMHOOK DLLs. Vanilla copying now preserves pinned dependencies and generated hooks.
- Repacked and verified embedded API source (modern-3).
- Added real-installer SmokeTest harness for mod lifecycle and full API switching.
- Confirmed API v78 and DebugMod 1.4.10.2 load on Steam Hollow Knight 1.5.12620 / Unity 6000.0.61f1.
- 52 manager tests and HookGen regressions pass; Windows EXE 2.8.4 built and launched.

# Scarab Modern 2.8.3

* Pin the separate AppData API workspace to the newest installed stable .NET 8.0 SDK and verify the selected SDK before restore. SDK 10 previously escaped the manager's global.json and failed with a CET fatal error on the reported Windows build.
* Give a specific SDK 8 prerequisite message when only newer SDKs or runtimes are installed.
* Disable shared Roslyn compilation and parallel MSBuild for API builds after compiler-server pipe timeouts in the supplied report.
* Decode redirected output as UTF-8 and use English tool messages to avoid mojibake in diagnostic exports.
* Keep the complete source archive and extracted SHA-256 verification.

# Scarab Modern 2.8.2

* Fixed incomplete source archive: include all tracked source files, not just changed files.
* Restore the solution, unchanged source, resources and licenses.
* Add a SHA-256 source manifest and verify every entry after extracting the final archive.
* Windows build uses absolute .csproj paths and checks required inputs before restoring packages.

# Scarab Modern 2.8.1

## Installation fixes

* Normalize equivalent game versions (`1.5.12620` / `1.5.12620.0`) in game/API checks.
* Fall back to the clean DLL's `Constants.GAME_VERSION` if Unity's serialized version cannot be read;
  reject mismatches between readable game data and the assembly.
* Accept a directly selected Managed folder and UnityEngine.CoreModule.dll installations.
* Check write access before downloading/building API.
* Check for .NET SDK before API build, use explicit NuGet restore, separate no-restore build and persistent streaming logs.
* Reuse verified API v78 from the previous integration revision instead of rebuilding identical source unnecessarily.
* Strip URL queries and decode Content-Disposition filenames; dispose HTTP responses.
* Retry transient HTTP/network failures, bound downloads and reject truncated bodies.
* Write only actual downloaded bytes, avoiding unused buffer capacity in DLL files.
* Validate missing/cyclic dependencies before installing API or touching mod directories.
* Stage complete mod packages outside Mods, reject unsafe ZIP paths and keep user-owned files.
* Roll back mod directory/state changes on failed install, toggle or uninstall.
* Track installed package files so subsequent updates remove retired files without deleting unrelated skins/settings.
* Restore in-memory installed state if writing JSON fails.
* Restore UI state on errors, refresh API badges after mod installs and dispose event subscriptions correctly.

## Diagnostics

Errors remain in a separate selectable text window. Every operation error is automatically saved as UTF-8 `.txt`.
Copy and Save buttons are available. Settings has a Diagnostics / Save log button.
Reports include runtime, OS, selected game/version, complete exception and recent application/API build log tails.
API build logs are written live, including failed restore/build and timeouts.
Windows source builds save a transcript under the project's `logs/` directory.

## Verification

Added offline regression tests covering version aliases, generated DLL version detection,
version conflicts, dependency cycles/missing/shared dependencies, exact download bytes,
query filenames, transient/non-transient HTTP errors, truncated downloads, staged mod updates,
user asset preservation and rollback. These tests do not use the real game or internet.

Static/source-package checks passed. This environment still cannot initialize CoreCLR;
compilation, xUnit execution and game/UI runtime checks were not completed here.
The user's previous installation error was not reproduced because its text was unavailable.
