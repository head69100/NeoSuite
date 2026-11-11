<#
    Construit l'installateur de NeoSuite, en UN seul fichier : dist\NeoSuite.exe.

    Il ne contient que NeoSuite (le panneau) : les applis (NeoDriver, NeoSoft…) ne sont pas dedans. NeoSuite les
    télécharge depuis la dernière release GitHub au moment où l'utilisateur clique sur « Installer »
    (catalog.json + <App>.zip, voir NeoSuite\Services\AppCatalog.cs). L'installateur reste ainsi léger, et Windows,
    qui analyse en entier un .exe inconnu à son premier lancement, l'ouvre vite.

    1. Reconstruit les paquets des applis (packages\*.zip, pour la release ; sauf avec -SkipPackages)
    2. Publie NeoSuite (Release, autonome)
    3. Rassemble NeoSuite\ et info.json (version et nouveautés) dans une archive ZIP
    4. Publie le programme d'installation (Setup\NeoSetup)
    5. Colle l'archive à la fin du programme : dist\NeoSuite.exe (voir Shared\Neo.Shared\InstallerPackage.cs)

    Utilisation :  powershell -ExecutionPolicy Bypass -File tools\build-setup.ps1 [-SkipPackages]
#>
param([switch]$SkipPackages)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
$apps = 'NeoDriver', 'NeoUninstaller', 'NeoBoost', 'NeoSoft', 'NeoRar', 'NeoMedia', 'NeoSound', 'NeoTorrent'
$work = Join-Path $env:TEMP 'NeoSetupBuild'
$dist = Join-Path $root 'dist'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work, $dist | Out-Null

# 1. Paquets des applis (publiés à part dans la release, pas dans l'installateur)
if (-not $SkipPackages) {
    foreach ($app in $apps) { & (Join-Path $PSScriptRoot 'build-packages.ps1') -Apps $app }
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

# 3. Archive : NeoSuite + info.json
Write-Host 'Assemblage des fichiers à installer...' -ForegroundColor Cyan
$payload = Join-Path $work 'payload.zip'
$zip = [System.IO.Compression.ZipFile]::Open($payload, 'Create')
try {
    Get-ChildItem $suiteOut -Recurse -File | ForEach-Object {
        $name = 'NeoSuite/' + $_.FullName.Substring($suiteOut.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $name, 'Optimal')
    }
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $infoPath, 'info.json', 'Optimal')
}
finally { $zip.Dispose() }

# 4. Programme d'installation
Write-Host "Publication de l'installateur..." -ForegroundColor Cyan
$setupOut = Join-Path $work 'setup'
dotnet publish (Join-Path $root 'Setup\NeoSetup\NeoSetup.csproj') -c Release -o $setupOut -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Échec de la publication de l'installateur" }
$stub = Join-Path $setupOut 'NeoSuite.exe'

# 5. dist\NeoSuite.exe = programme + archive + 16 octets (position de l'archive, puis « NEOSETUP »).
#    Les anciens fichiers (NeoSuite.pak, NeoSuite-Setup.exe) sont retirés.
Get-ChildItem $dist -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'NeoSuite*.exe' -or $_.Name -like 'NeoSuite*.pak' } | Remove-Item -Force
$target = Join-Path $dist 'NeoSuite.exe'
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
Write-Host ("Installateur prêt : {0} ({1:N0} Mo, fichier unique)" -f $target, ((Get-Item $target).Length / 1MB)) -ForegroundColor Green
