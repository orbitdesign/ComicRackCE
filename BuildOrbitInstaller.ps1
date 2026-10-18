# Builds the setup program of this build ("ComicRack CEO") with Inno Setup 6.
#   1. Build ComicRack in Release first (MSBuild.exe ComicRack\ComicRack.csproj -restore -t:build -property:Configuration=Release).
#   2. Run this script from the repository root.
# The setup program has an optional "Translator helper" component when -HelperSrc names the folder with the helper's files.
param(
    [string]$Version = "",                    # default: the BuildVersion in ComicRack\Properties\AssemblyInfo.cs
    [string]$HelperSrc = "S:\FROG\Claude AI\Comicrack\ComicTranslator-src\ComicTranslator",
    [string]$RedistDir = "S:\FROG\Claude AI\Comicrack\Installer\_CommonRedist",     # holds VC_redist.x64.exe (https://aka.ms/vs/17/release/vc_redist.x64.exe)
    [string]$OutDir = "S:\FROG\Claude AI\Comicrack\Installer"
)
$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (!(Test-Path $iscc)) { $iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" }
if (!(Test-Path $iscc)) { Write-Error "Inno Setup 6 is not installed (https://jrsoftware.org/isdl.php)."; exit 1 }
if (!$Version) {
    $m = Select-String -Path ".\ComicRack\Properties\AssemblyInfo.cs" -Pattern 'BuildVersion", "([^"]+)"' | Select-Object -First 1
    $Version = "v" + $m.Matches[0].Groups[1].Value
}
$args2 = @("Installer.iss", "/DMyAppVersion=$Version", "/DMyAppSetupFile=ComicRackSetup_orbitdesign_$Version", "/DRedistDir=$RedistDir", "/O$OutDir")
if ($HelperSrc -and (Test-Path $HelperSrc)) { $args2 += "/DHelperSrc=$HelperSrc" }
& $iscc @args2
