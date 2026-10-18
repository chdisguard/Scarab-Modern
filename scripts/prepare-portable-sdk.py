import pathlib,json,urllib.request,hashlib,zipfile
r=pathlib.Path(__file__).resolve().parents[1]
p=json.loads((r/"scripts/portable-sdk.json").read_text())
z=r/"out"/("dotnet-sdk-"+p["version"]+"-win-x64.zip")
print("Downloading official .NET SDK",p["version"],flush=True)
if not z.exists():urllib.request.urlretrieve(p["url"],z)
assert hashlib.sha512(z.read_bytes()).hexdigest().lower()==p["sha512"].lower(),"SDK checksum mismatch"
print("SHA-512 verified; extracting",flush=True)
d=r/"out/win-x64/tools/dotnet"
d.mkdir(parents=True,exist_ok=True)
with zipfile.ZipFile(z) as a:a.extractall(d)
print("Portable SDK ready",flush=True)
