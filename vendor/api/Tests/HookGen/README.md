# Hook generator regression tests

From the repository root:

```sh
dotnet build Tests/HookGen --configuration Release
mono Tests/HookGen/bin/Release/net472/HookGen.exe
```

On Windows, run the executable directly. These tests need no Hollow Knight files. They generate independent facade and target assemblies in memory, run the real hook generator, and inspect the serialized result for legacy signatures, external target references, matching subscription operations, unchanged ordinary hooks, and rejection of invalid forwarding methods.

Forwarded methods must preserve the target's calling convention and compatible parameter and return types. Distinct enum types with the same underlying type are supported; the forwarding-body check does not establish type compatibility for other conversions.

These checks validate generated assembly contracts. Native detours and game behavior require a separate runtime smoke test.
