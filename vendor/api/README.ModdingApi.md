# Hollow Knight Modding API

This development build targets Hollow Knight 1.5.12620 (Unity 6). Use a build that matches the game version. The macOS build contains universal native libraries for both x86-64 and arm64 game processes. Compatibility with every existing mod has not been established.

## Installation

Close the game and copy this package's files into its `Managed` directory:

- macOS: `hollow_knight.app/Contents/Resources/Data/Managed`
- Windows and Linux: `hollow_knight_Data/Managed`

Place each mod in its own subdirectory of `Managed/Mods`. The title screen lists loaded mods and any initialization failures.

## Apple Silicon

The macOS package contains universal (x86-64 and arm64) native libraries, so the game runs natively on Apple Silicon as well as through Rosetta. Steam builds refuse to start when Steam initialization fails, so launch the game from Steam, or, when starting the executable directly, keep the Steam client running and set the app id environment variables:

```sh
SteamAppId=367520 SteamGameId=367520 "/path/to/hollow_knight.app/Contents/MacOS/Hollow Knight"
```

Steam sets these variables itself when the game is started from the Steam library, where no wrapper script is needed.

## Removal

Restore the original game files using Steam's **Properties → Installed Files → Verify integrity of game files**, or the equivalent repair command in your game client. This removes the API from the game assemblies; manually added mod files may remain in `Managed/Mods`.
