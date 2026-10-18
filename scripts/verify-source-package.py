#!/usr/bin/env python3
"""Validate the exact API source embedded into the manager without game binaries."""
from pathlib import Path
import hashlib
import json
import zipfile
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
archive = root / 'vendor/Unity6Api-source.zip'
meta = json.loads((root / 'vendor/api-provenance.json').read_text())
assert hashlib.sha256(archive.read_bytes()).hexdigest() == meta['embedded_source_sha256'], 'Source ZIP checksum differs'
project = ET.parse(root / 'Scarab/Scarab.csproj')
assert any(r.attrib.get('Include') == '../vendor/Unity6Api-source.zip' and
           r.attrib.get('LogicalName') == 'Scarab.Unity6Api.zip'
           for r in project.iter('EmbeddedResource')), 'API source is not embedded in the manager'
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None, 'Source ZIP is corrupt'
    assert z.read('hollowknight.version').decode().strip() == meta['game']
    assert z.read('moddingapi.version').decode().strip() == f"v{meta['api']}"
    assert 'LICENSE' in z.namelist() and 'MonoMod.Patcher/LICENSE' in z.namelist()
    for info in z.infolist():
        path = Path(info.filename)
        assert not path.is_absolute() and '..' not in path.parts
        assert '.git' not in path.parts and 'Vanilla' not in path.parts
        assert (root / 'vendor/api' / path).read_bytes() == z.read(info), f'Stale embedded source: {path}'
print(f"PASS: embedded API v{meta['api']} for {meta['game']}, exact source bytes and licenses verified")
