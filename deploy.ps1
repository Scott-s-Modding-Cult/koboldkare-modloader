# Copies plugins staged by `dotnet build -p:Deploy=false` (ModSource\_staged\<Mod>\*.dll)
# into BepInEx\plugins\<Mod>\. Run it after closing KoboldKare (the game locks loaded DLLs).
#
#   powershell -ExecutionPolicy Bypass -File ModSource\deploy.ps1

$ErrorActionPreference = 'Stop'
$modSource = $PSScriptRoot
$staged = Join-Path $modSource '_staged'
$plugins = Join-Path (Split-Path $modSource -Parent) 'BepInEx\plugins'

if (Get-Process KoboldKare -ErrorAction SilentlyContinue) {
    Write-Host 'KoboldKare is still running - close it first (it locks the plugin DLLs).' -ForegroundColor Yellow
    exit 1
}
if (-not (Test-Path $staged)) {
    Write-Host 'Nothing staged. Build with: dotnet build -c Release -p:Deploy=false'
    exit 0
}

foreach ($dir in Get-ChildItem $staged -Directory) {
    $target = Join-Path $plugins $dir.Name
    New-Item -ItemType Directory -Force $target | Out-Null
    # Recursive, so bundled extras (e.g. UITheme's themes\ folder) come along.
    foreach ($file in Get-ChildItem $dir.FullName -File -Recurse) {
        $relative = $file.FullName.Substring($dir.FullName.Length).TrimStart('\')
        $dest = Join-Path $target $relative
        New-Item -ItemType Directory -Force (Split-Path $dest -Parent) | Out-Null
        Copy-Item $file.FullName $dest -Force
        Write-Host "Deployed $($dir.Name)\$relative"
    }
}
Remove-Item $staged -Recurse -Force
Write-Host 'Done. Start the game from Steam so workshop mods load too.' -ForegroundColor Green
