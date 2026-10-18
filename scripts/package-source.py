#!/usr/bin/env python3
"""Package the complete source tree and verify it after extraction."""
from pathlib import Path
import argparse
import hashlib
import json
import subprocess
import tempfile
import zipfile

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('output')
args = parser.parse_args()
output = Path(args.output).resolve()
# --cached is essential: unchanged tracked sources are required to build the project.
raw = subprocess.check_output(['git', '-C', str(root), 'ls-files', '--cached', '--others', '--exclude-standard', '-z'])
files = {p.decode() for p in raw.split(b'\0') if p}
files.update(str(p.relative_to(root)) for p in (root / 'vendor').rglob('*') if p.is_file())
files.discard('source-manifest.json')
required = {
    'Scarab.sln', 'LICENSE', 'global.json', 'NuGet.Config', 'Start-Windows.cmd',
    'Scarab/Scarab.csproj', 'Scarab/Program.cs', 'Scarab/Usings.cs',
    'Scarab/Models/ModItem.cs', 'Scarab/resources.Designer.cs', 'Scarab/Resources.resx',
    'Scarab/Assets/omegamaggotprime.ico', 'Scarab.Tests/Scarab.Tests.csproj',
    'scripts/build-windows.ps1', 'scripts/verify-source-package.py',
    'vendor/Unity6Api-source.zip', 'vendor/api/LICENSE', 'vendor/api/Tests/HookGen/HookGen.csproj',
}
assert required <= files, f'Missing required source files: {sorted(required - files)}'
manifest = {}
for name in sorted(files):
    path = root / name
    assert path.is_file(), f'Missing source: {name}'
    manifest[name] = hashlib.sha256(path.read_bytes()).hexdigest()
(root / 'source-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
output.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as archive:
    for name in sorted(files | {'source-manifest.json'}):
        archive.write(root / name, Path('Scarab-Modern') / name)
with tempfile.TemporaryDirectory(prefix='source-qa-', dir=output.parent) as tmp:
    with zipfile.ZipFile(output) as archive:
        assert archive.testzip() is None
        archive.extractall(tmp)
    extracted = Path(tmp) / 'Scarab-Modern'
    for name, expected in manifest.items():
        path = extracted / name
        assert path.is_file(), f'File omitted from final ZIP: {name}'
        assert hashlib.sha256(path.read_bytes()).hexdigest() == expected, f'Changed ZIP bytes: {name}'
    subprocess.run(['python3', str(extracted / 'scripts/verify-source-package.py')], check=True)
print(f'PASS: complete source archive, {len(manifest)} verified files, {output.stat().st_size} bytes')
