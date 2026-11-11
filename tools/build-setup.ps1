<#
    Construit l'installateur de NeoSuite pour un nouvel utilisateur, dans dist\ :
        NeoSuite.exe   le programme d'installation (petit)
        NeoSuite.pak   ses fichiers : NeoSuite, paquets des applis, info.json (version et nouveautés)
    Les deux fichiers vont ensemble (même dossier). Pourquoi deux : Windows analyse en entier un .exe inconnu
    à son premier lancement, et 700 Mo, c'est de 20 secondes à plusieurs minutes d'attente. Un fichier de
    données de la même taille, lui, n'est pas analysé au lancement.

    1. Reconstruit les paquets des applis (packages\*.zip, sauf avec -SkipPackages)
    2. Publie NeoSuite (Release, autonome)
    3. Rassemble NeoSuite\, Packages\ et info.json dans une archive ZIP
    4. Publie l'installateur (Setup\NeoSetup) en un seul .exe
    5. Pose NeoSuite.exe et NeoSuite.pak dans dist\ (avec -SingleFile : un seul .exe, données collées dedans)

    Utilisation :  powershell -ExecutionPolicy Bypass -File tools\build-setup.ps1 [-SkipPackages] [-SingleFile]
#>
param([switch]$SkipPackages, [switch]$SingleFile)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
$apps = 'NeoDriver', 'NeoUninstaller', 'NeoBoost', 'NeoSoft', 'NeoRar', 'NeoMedia', 'NeoSound', 'NeoTorrent'
$work = Join-Path $env:TEMP 'NeoSetupBuild'
$dist = Join-Path $root 'dist'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work, $dist | Out-Null

# 1. Paquets des applis
if (-not $SkipPackages) {
    foreach ($app in $apps) { & (Join-Path $PSScriptRoot 'build-packages.ps1') -Apps $app }
}
foreach ($app in $apps) {
    if (-not (Test-Path (Join-Path $root "packages\$app.zip"))) { throw "Paquet manquant : packages\$app.zip (relancez sans -SkipPackages)" }
}

# 2. NeoSuite
Write-Host 'Publication de NeoSuite...' -ForegroundColor Cyan
$suiteOut = Join-Path $work 'NeoSuite'
dotnet publish (Join-Path $root 'NeoSuite\NeoSuite.csproj') -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $suiteOut -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Échec de la publication de NeoSuite' }

# Version et nouveautés : lues dans NeoSuite.exe et dans la première section de NeoSuite\CHANGELOG.md
$suiteVersion = (Get-Item (Join-Path $suiteOut 'NeoSuite.exe')).VersionInfo.ProductVersion -replace '[+-].*$', ''
$notes = @()
$inFirst = $false
foreach ($line in (Get-Content (Join-Path $root 'NeoSuite\CHANGELOG.md') -Encoding UTF8)) {
    if ($line -match '^## ') { if ($inFirst) { break } else { $inFirst = $true; continue } }
    if ($inFirst -and $line -match '^- (.+)$') { $notes += $Matches[1] }
}
$infoPath = Join-Path $work 'info.json'
$info = [ordered]@{ suiteVersion = $suiteVersion; built = (Get-Date).ToString('s'); notes = @($notes) } | ConvertTo-Json -Compress
[System.IO.File]::WriteAllText($infoPath, $info, (New-Object System.Text.UTF8Encoding $false))
Write-Host "NeoSuite $suiteVersion ($($notes.Count) nouveautés)" -ForegroundColor Cyan

# 3. Archive : NeoSuite compressé, paquets stockés tels quels (déjà compressés)
Write-Host 'Assemblage des fichiers à installer...' -ForegroundColor Cyan
$payload = Join-Path $work 'NeoSuite.pak'
$zip = [System.IO.Compression.ZipFile]::Open($payload, 'Create')
try {
    Get-ChildItem $suiteOut -Recurse -File | ForEach-Object {
        $name = 'NeoSuite/' + $_.FullName.Substring($suiteOut.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $name, 'Optimal')
    }
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $infoPath, 'info.json', 'Optimal')
    $catalog = Join-Path $root 'packages\catalog.json'
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $catalog, 'Packages/catalog.json', 'Optimal')
    foreach ($app in $apps) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root "packages\$app.zip"), "Packages/$app.zip", 'NoCompression')
    }
}
finally { $zip.Dispose() }

# 4. Installateur
Write-Host "Publication de l'installateur..." -ForegroundColor Cyan
$setupOut = Join-Path $work 'setup'
dotnet publish (Join-Path $root 'Setup\NeoSetup\NeoSetup.csproj') -c Release -o $setupOut -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Échec de la publication de l'installateur" }
$stub = Join-Path $setupOut 'NeoSuite.exe'

# 5. dist\ : on repart de zéro (anciens noms compris : NeoSuite-Setup.exe)
Get-ChildItem $dist -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'NeoSuite*.exe' -or $_.Name -like 'NeoSuite*.pak' } | Remove-Item -Force
$target = Join-Path $dist 'NeoSuite.exe'
if ($SingleFile) {
    $out = [System.IO.File]::Create($target)
    try {
        $stubBytes = [System.IO.File]::ReadAllBytes($stub)
        $out.Write($stubBytes, 0, $stubBytes.Length)
        $offset = [long]$stubBytes.Length
        $in = [System.IO.File]::OpenRead($payload)
        try { $in.CopyTo($out) } finally { $in.Dispose() }
        $trailer = [BitConverter]::GetBytes($offset) + [System.Text.Encoding]::ASCII.GetBytes('NEOSETUP')
        $out.Write($trailer, 0, $trailer.Length)
    }
    finally { $out.Dispose() }
    Write-Host ("Installateur prêt : {0} ({1:N0} Mo, fichier unique : lent au premier lancement)" -f $target, ((Get-Item $target).Length / 1MB)) -ForegroundColor Green
}
else {
    Copy-Item $stub $target
    Copy-Item $payload (Join-Path $dist 'NeoSuite.pak')
    Write-Host ("Installateur prêt dans {0}" -f $dist) -ForegroundColor Green
    Write-Host ("  NeoSuite.exe  {0:N0} Mo`n  NeoSuite.pak  {1:N0} Mo   (à garder dans le même dossier)" -f ((Get-Item $target).Length / 1MB), ((Get-Item (Join-Path $dist 'NeoSuite.pak')).Length / 1MB))
}

# NeoTorrent ajouté au catalogue
