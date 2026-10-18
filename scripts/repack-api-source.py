from pathlib import Path
import zipfile,hashlib,json
r=Path(__file__).resolve().parents[1]
z=r/'vendor/Unity6Api-source.zip'
with zipfile.ZipFile(z) as a: names=a.namelist()
with zipfile.ZipFile(z,'w',zipfile.ZIP_DEFLATED) as a:
 for n in names: a.write(r/'vendor/api'/n,n)
p=r/'vendor/api-provenance.json'
m=json.loads(p.read_text());m['embedded_source_sha256']=hashlib.sha256(z.read_bytes()).hexdigest()
m['integration_revision']='unity6-f0057c4-modern-3';m['local_revision']='unity6-f0057c4-modern-3'
fix='exclude stale loader DLLs from Vanilla copy before patching'
if fix not in m['local_fixes']:m['local_fixes'].append(fix)
p.write_text(json.dumps(m,indent=2))
print('API source repacked')
