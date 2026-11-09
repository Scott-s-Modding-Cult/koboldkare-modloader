# Creates a new ModKit mod from the ExampleMod template.
#
#   powershell -ExecutionPolicy Bypass -File ModSource\new-mod.ps1 -Name MyMod [-DisplayName "My Mod"]
#
# Result: ModSource\mods\MyMod\ with project KoboldKare.MyMod, namespace MyMod, plugin GUID koboldkare.mymod,
# added to KoboldKareMods.sln. Build it with:  dotnet build ModSource\mods\MyMod -c Release

param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z][A-Za-z0-9]*$')][string]$Name,
    [string]$DisplayName = $Name
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$template = Join-Path $root 'mods\ExampleMod'
$target = Join-Path $root "mods\$Name"

if (Test-Path $target) { throw "ModSource\mods\$Name already exists." }

New-Item -ItemType Directory $target | Out-Null
$lower = $Name.ToLowerInvariant()
# Source files only: skip bin/ and obj/ (generated AssemblyInfo would duplicate attributes).
$files = Get-ChildItem $template -File -Recurse -Include *.cs, *.csproj |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
foreach ($file in $files) {
    $text = Get-Content $file.FullName -Raw
    # -creplace = case-sensitive (plain -replace would also hit "KoboldKare.ExampleMod").
    $text = $text -creplace 'koboldkare\.example', ('koboldkare.' + $lower)
    $text = $text -creplace '"Example Mod"', ('"' + $DisplayName + '"')
    $text = $text -creplace 'ModPanel\.AddPage\("Example"', ('ModPanel.AddPage("' + $DisplayName + '"')
    $text = $text -creplace 'ExampleModPlugin', ($Name + 'Plugin')
    $text = $text -creplace 'ExampleMod', $Name
    $text = $text -creplace '"/example"', ('"/' + $lower + '"')
    $text = $text -creplace '/example ', ('/' + $lower + ' ')
    # Your copy is a real mod, not a template.
    $text = $text -creplace '(?m)^\s*<!-- Template only:.*-->\r?\n', ''
    $text = $text -creplace '(?m)^\s*<IsTemplate>true</IsTemplate>\r?\n', ''
    $relative = $file.FullName.Substring($template.Length).TrimStart('\') -creplace 'ExampleMod', $Name
    $outPath = Join-Path $target $relative
    New-Item -ItemType Directory -Force (Split-Path $outPath -Parent) | Out-Null
    Set-Content -Path $outPath -Value $text -Encoding UTF8 -NoNewline
}

$sln = Join-Path $root 'KoboldKareMods.sln'
if (Test-Path $sln) { dotnet sln $sln add (Join-Path $target "$Name.csproj") | Out-Null }

Write-Host "Created ModSource\mods\$Name. Build: dotnet build `"$target`" -c Release" -ForegroundColor Green
Write-Host "(Close the game first, or add -p:Deploy=false and run deploy.ps1 later.)"
