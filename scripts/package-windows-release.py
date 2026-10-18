from pathlib import Path
import hashlib, json, zipfile
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
app = root / "out/win-x64"
version = ".".join(ET.parse(root / "Scarab/Scarab.csproj").findtext("./PropertyGroup/Version").split(".")[:3])
archive = root / "out" / f"Scarab-Modern-{version}-win-x64.zip"
required = ["Scarab.exe", "av_libglesv2.dll", "libHarfBuzzSharp.dll", "libSkiaSharp.dll", "tools/dotnet/dotnet.exe", "tools/dotnet/LICENSE.txt", "tools/dotnet/ThirdPartyNotices.txt"]
for name in required:
    assert (app / name).is_file(), f"Missing release file: {name}"
pin = json.loads((root / "scripts/portable-sdk.json").read_text())
assert (app / "tools/dotnet/sdk" / pin["version"] / "MSBuild.dll").is_file()
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for name in required[:4]: z.write(app/name, name)
    for path in sorted((app/"tools").rglob("*")):
        if path.is_file(): z.write(path, path.relative_to(app))
    for name in ["README.md", "CREDITS.md", "LICENSE"]: z.write(root/name, name)
    z.write(root/"vendor/api/LICENSE", "credits/API-LICENSE.txt")
    z.write(root/"vendor/api/MonoMod.Patcher/LICENSE", "credits/MonoMod-LICENSE.txt")
    z.write(root/"vendor/api-provenance.json", "credits/api-provenance.json")
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    assert all(name in z.namelist() for name in required)
checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
(archive.parent / (archive.name + ".sha256")).write_text(checksum + "  " + archive.name + "\n")
print(str(archive), checksum, flush=True)
