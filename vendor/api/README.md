Hollow Knight Modding API
=========================
![build](https://github.com/hk-modding/api/actions/workflows/build.yaml/badge.svg)
![docs](https://github.com/hk-modding/api/actions/workflows/docs.yaml/badge.svg)
![GitHub all releases](https://img.shields.io/github/downloads/hk-modding/api/total)

A Hollow Knight Modding API/loader for Hollow Knight 1.5.12620 (Unity 6). Uses [MonoMod](https://github.com/MonoMod/MonoMod).

This branch builds on the [Unity 6 port](https://github.com/hk-modding/api/pull/164) and is under development. Existing mods may require changes for Unity 6.

If you're a mod developer, there are [examples](https://github.com/hk-modding/api/tree/master/Examples) in the repo.

Documentation can be found [here](https://hk-modding.github.io/api/).

Build
=======

Install the .NET SDK (8.0 or later). On macOS and Linux, also install Mono and make sure `mono` is on `PATH`; the build runs .NET Framework patching tools through Mono.

Copy the `Managed` folder from a matching, unmodified Hollow Knight installation into the repository root as `Vanilla`. On macOS this folder is inside `hollow_knight.app/Contents/Resources/Data/Managed`.

Build from the repository root, choosing the runtime for the game process:

```sh
dotnet build Assembly-CSharp --configuration Release --runtime osx-x64
```

Use `win-x64` on Windows or `linux-x64` on Linux. The project builds its patching tools and dependencies automatically and writes the installable files to `OutputFinal`.

The patcher disables method inlining in modified game assemblies so runtime hooks can observe calls reliably.

On Apple Silicon, use `osx-x64`. The build merges the x86-64 and arm64 native libraries into universal binaries with `lipo`, which ships with the macOS command line tools, so the package works with both native and Rosetta game processes. The GitHub Actions macOS job installs [konoui/lipo](https://github.com/konoui/lipo) on its Linux runner for the same merge. Runtime hooks, including mods such as Benchwarp that use native detours, run in a native arm64 game process.

Builds leave the game installation untouched unless `CopyDir` is explicitly supplied. To install the result, close the game and copy the contents of `OutputFinal` into its `Managed` folder, or build with `-p:CopyDir="/absolute/path/to/Managed"`.

The GitHub Actions workflow needs access to the private `hk-modding/hk-binary-archives` repository through `BINARY_ARCHIVE_DEPLOY_KEY`. Local builds use the supplied `Vanilla` folder.

The [hook generator regression tests](Tests/HookGen/README.md) run without game files.

License
=======
Distributed under the MIT [license](https://github.com/hk-modding/api/blob/master/LICENSE).
