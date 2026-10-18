$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tests = Join-Path $root 'Scarab.Tests/Scarab.Tests.csproj'
$app = Join-Path $root 'Scarab/Scarab.csproj'
$hooks = Join-Path $root 'vendor/api/Tests/HookGen/HookGen.csproj'
$hookExe = Join-Path $root 'vendor/api/Tests/HookGen/bin/Release/net472/HookGen.exe'
$output = Join-Path $root 'out/win-x64'
foreach ($required in @($tests, $app, $hooks, (Join-Path $root 'Scarab/Models/ModItem.cs'), (Join-Path $root 'Scarab/resources.Designer.cs'), (Join-Path $root 'vendor/Unity6Api-source.zip'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Incomplete source archive: missing $required. Extract the full source archive into one directory." }
}
$manifestPath = Join-Path $root 'source-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Source manifest missing. Extract the complete 2.8.2 source archive.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
foreach ($entry in $manifest.PSObject.Properties) {
    $path = Join-Path $root $entry.Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Incomplete source archive: missing $path. Extract the full archive again." }
}
Push-Location -LiteralPath $root
$transcript = $false
try {
    New-Item -ItemType Directory -Force -Path logs | Out-Null
    Start-Transcript -Path (Join-Path (Get-Location) ('logs/build-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')) -Force | Out-Null
    $transcript = $true
    $config = Join-Path $root 'NuGet.Config'
    Write-Host '[1/7] Restoring manager/test packages from nuget.org (first run can take several minutes)...'
    dotnet restore $tests --configfile $config --disable-parallel --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed. See the NuGet error above.' }

    Write-Host '[2/7] Running manager tests...'
    dotnet test $tests -c Release --no-restore --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; no build was produced.' }

    Write-Host '[3/7] Restoring HookGen regression packages...'
    dotnet restore $hooks --configfile $config --disable-parallel --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw 'HookGen package restore failed.' }

    Write-Host '[4/7] Building HookGen regression checks...'
    dotnet build $hooks -c Release --no-restore --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw 'HookGen regression build failed.' }

    Write-Host '[5/7] Running HookGen regression checks...'
    & $hookExe
    if ($LASTEXITCODE -ne 0) { throw 'HookGen regression checks failed.' }

    Write-Host '[6/7] Restoring Windows publish runtime...'
    dotnet restore $app --configfile $config -r win-x64 -p:SelfContained=true --disable-parallel --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw 'Windows runtime restore failed.' }

    Write-Host '[7/7] Publishing Scarab...'
    dotnet publish $app -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -o $output --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Write-Host 'Ready: out/win-x64/Scarab.exe'
} finally {
    if ($transcript) { Stop-Transcript | Out-Null }
    Pop-Location
}
