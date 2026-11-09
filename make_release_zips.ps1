# Builds every mod and the Mod Manager, then makes the zips for a GitHub Release:
#   mods\<Mod>.zip                  one per mod, committed to the repo. Unzip into the game folder
#                                   (each holds BepInEx\plugins\<Mod>\...), or use the Mod Manager's Add Mod.
#   dist\KoboldKare-ModManager.zip  just the Mod Manager, for a game that already has BepInEx.
#   dist\KoboldKare-ModLoader.zip   everything in one: BepInEx, the unstripped Mono corlib it needs on this
#                                   game, the doorstop config, every mod and the Mod Manager.
#
#   powershell -ExecutionPolicy Bypass -File make_release_zips.ps1 [-GameDir "X:\...\KoboldKare"]
#
# BepInEx and the corlib come from the game folder's working install. UnityEngine.AIModule.dll in
# unstripped_corlib is Unity's, so it is never packed (see README). Test themes are never packed either.
param(
    [string]$GameDir = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$here = $PSScriptRoot
$build = Join-Path $here 'build'
$dist = Join-Path $here 'dist'
$modsOut = Join-Path $here 'mods'
$bepinex = Join-Path $GameDir 'BepInEx'

if (-not (Test-Path "$bepinex\core\BepInEx.dll")) { throw "No BepInEx install found in $GameDir." }
foreach ($dir in $build, $dist) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -Confirm:$false }
}
New-Item -ItemType Directory -Force $build, $dist, $modsOut | Out-Null

# Build into build\plugins instead of the game (or ModSource\_staged, which the Mod Manager would install).
# SkipDeploy is cleared so a local Directory.Build.local.props can't leave a mod out of the zips.
$plugins = Join-Path $build 'plugins'
dotnet build "$here\KoboldKareMods.sln" -c Release -p:Deploy=false "-p:SkipDeploy=" "-p:StagingDir=$plugins" "-p:GameDir=$GameDir" -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$manager = Join-Path $here 'ModManager\bin\Release\KoboldKare Mod Manager.exe'

# UITheme ships with the colours-only Night Sky sample theme.
Copy-Item "$here\ThemeSamples\NightSky" "$plugins\UITheme\themes\NightSky" -Recurse -Force

# Not ZipFile.CreateFromDirectory: on Windows PowerShell it writes "\" in entry names, which other unzippers
# turn into odd file names. The zip format wants "/".
function Zip($folder, $zip) {
    if (Test-Path $zip) { Remove-Item $zip -Force -Confirm:$false }
    $root = (Resolve-Path $folder).Path.TrimEnd('\')
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in Get-ChildItem $root -Recurse -Force) {
            $name = $item.FullName.Substring($root.Length + 1).Replace('\', '/')
            if ($item.PSIsContainer) {
                if (-not (Get-ChildItem $item.FullName -Force)) { [void]$archive.CreateEntry("$name/") }
            } else {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $item.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
            }
        }
    } finally {
        $archive.Dispose()
    }
}
function Copy-Into($from, $to) {
    New-Item -ItemType Directory -Force (Split-Path $to -Parent) | Out-Null
    Copy-Item $from $to -Recurse -Force
}

$mods = Get-ChildItem $plugins -Directory | ForEach-Object { $_.Name }
foreach ($mod in $mods) {
    $stage = Join-Path $build "zip-$mod"
    Copy-Into "$plugins\$mod" "$stage\BepInEx\plugins\$mod"
    Zip $stage "$modsOut\$mod.zip"
}

$stage = Join-Path $build 'zip-manager'
Copy-Into $manager "$stage\KoboldKare Mod Manager.exe"
Zip $stage "$dist\KoboldKare-ModManager.zip"

$stage = Join-Path $build 'zip-loader'
Copy-Into "$bepinex\core" "$stage\BepInEx\core"
foreach ($dll in 'mscorlib.dll', 'System.dll', 'System.Core.dll') {
    Copy-Into "$bepinex\unstripped_corlib\$dll" "$stage\BepInEx\unstripped_corlib\$dll"
}
foreach ($file in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt') {
    Copy-Into "$GameDir\$file" "$stage\$file"
}
foreach ($mod in $mods) { Copy-Into "$plugins\$mod" "$stage\BepInEx\plugins\$mod" }
New-Item -ItemType Directory -Force "$stage\BepInEx\config", "$stage\BepInEx\patchers" | Out-Null
Copy-Into $manager "$stage\KoboldKare Mod Manager.exe"
Copy-Into "$here\CREDITS.txt" "$stage\BepInEx\CREDITS.txt"
Copy-Into "$here\licenses" "$stage\BepInEx\licenses"
Zip $stage "$dist\KoboldKare-ModLoader.zip"

# Nothing private or Unity-owned may end up in a zip.
foreach ($zip in @(Get-ChildItem $modsOut -Filter *.zip) + @(Get-ChildItem $dist -Filter *.zip)) {
    $archive = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
    $names = $archive.Entries | ForEach-Object { $_.FullName }
    $archive.Dispose()
    $bad = $names | Where-Object { $_ -match 'AIModule|VideoModule|AlpineAche|LogOutput|\.cfg$' }
    if ($bad) { throw "$($zip.Name) contains files that must not be published: $($bad -join ', ')" }
}
Remove-Item $build -Recurse -Force -Confirm:$false
Get-ChildItem $modsOut, $dist -Filter *.zip | Select-Object Name, @{ n = 'KB'; e = { [math]::Ceiling($_.Length / 1KB) } }
