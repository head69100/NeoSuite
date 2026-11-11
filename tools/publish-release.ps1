<#
    Publie une version de NeoSuite sur GitHub (dépôt head69100/NeoSuite).

    Une release « vX.Y.Z » (X.Y.Z = version de NeoSuite) contient :
        NeoSuite.exe + NeoSuite.pak   l'installateur complet (dist\), pour un nouvel utilisateur
        <App>.zip + catalog.json      le paquet de chaque appli (packages\), pour les mises à jour appli par appli
    NeoSuite interroge la dernière release (NeoSuite\Services\GitHubReleases.cs) : nouvelle version de la suite
    proposée dans son panneau, paquets plus récents proposés sur les tuiles des applis.

    Étapes :
        1. Vérifie que dist\ et packages\ correspondent bien à la version du code (sinon : build-setup.ps1)
        2. Met à jour la version et la taille affichées sur le site (NeoSite), puis commit + push
        3. Crée le tag et la release, notes tirées du premier bloc de NeoSuite\CHANGELOG.md
        4. Envoie les fichiers (environ 1,6 Go : compter plusieurs minutes)

    Utilisation :  powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 [-Draft]
    Prérequis    :  gh (connecté : gh auth status) et git, dépôt déjà poussé sur GitHub.
#>
param([switch]$Draft)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$repo = 'head69100/NeoSuite'
$utf8 = New-Object System.Text.UTF8Encoding $true
Set-Location $root

# --- 1. Version et fichiers -------------------------------------------------------------------------
$version = ([xml](Get-Content (Join-Path $root 'NeoSuite\NeoSuite.csproj') -Raw -Encoding UTF8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"
$exe = Join-Path $root 'dist\NeoSuite.exe'
$pak = Join-Path $root 'dist\NeoSuite.pak'
$catalog = Join-Path $root 'packages\catalog.json'
foreach ($f in $exe, $pak, $catalog) {
    if (-not (Test-Path $f)) { throw "Fichier manquant : $f (lancez d'abord tools\build-setup.ps1)" }
}

# L'installateur doit contenir CETTE version de NeoSuite (info.json dans le .pak)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($pak)
try {
    $reader = New-Object System.IO.StreamReader($zip.GetEntry('info.json').Open())
    $info = $reader.ReadToEnd() | ConvertFrom-Json
    $reader.Dispose()
}
finally { $zip.Dispose() }
if ($info.suiteVersion -ne $version) {
    throw "dist\NeoSuite.pak contient NeoSuite $($info.suiteVersion), le code est en $version : relancez tools\build-setup.ps1 -SkipPackages"
}

$apps = (Get-Content $catalog -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties
$assets = @($exe, $pak, $catalog) + ($apps | ForEach-Object { Join-Path $root "packages\$($_.Name).zip" })
foreach ($a in $assets) { if (-not (Test-Path $a)) { throw "Paquet manquant : $a" } }
$totalMb = [math]::Round(((Get-Item $exe).Length + (Get-Item $pak).Length) / 1MB)

$existing = gh release view $tag --repo $repo --json tagName 2>$null
if ($LASTEXITCODE -eq 0 -and $existing) { throw "La release $tag existe déjà sur GitHub. Montez la version de NeoSuite (CLAUDE.md) avant de republier." }

# --- 2. Site : version et taille affichées ----------------------------------------------------------
$site = Join-Path $root 'NeoSite\index.html'
$siteJs = Join-Path $root 'NeoSite\assets\js\core.js'
$html = [System.IO.File]::ReadAllText($site, [System.Text.Encoding]::UTF8)
if ($html -match '"softwareVersion":\s*"([^"]+)"' -and $Matches[1] -ne $version) {
    $old = $Matches[1]
    Write-Host "Site : $old -> $version" -ForegroundColor Cyan
    foreach ($file in $site, $siteJs) {
        $text = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)
        # Taille : seulement la fiche « Taille » et la commande « Télécharger » (le reste du site a d'autres « Mo »)
        $text = $text.Replace($old, $version) `
            -replace '(<span>Taille</span><b>)\d+ Mo', "`${1}$totalMb Mo" `
            -replace 'hint: "\d+ Mo · Windows"', "hint: `"$totalMb Mo · Windows`""
        [System.IO.File]::WriteAllText($file, $text, $utf8)
    }
}
git add NeoSite
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) {
    git commit -m "Site : NeoSuite $version" | Out-Null
}
git push origin HEAD
if ($LASTEXITCODE -ne 0) { throw 'git push a échoué : poussez vos changements puis relancez.' }

# --- 3. Notes de version ----------------------------------------------------------------------------
$lines = @()
$inFirst = $false
foreach ($line in (Get-Content (Join-Path $root 'NeoSuite\CHANGELOG.md') -Encoding UTF8)) {
    if ($line -match '^## ') { if ($inFirst) { break } else { $inFirst = $true; continue } }
    if ($inFirst) { $lines += $line }
}
$table = ($apps | ForEach-Object { "| $($_.Name) | $($_.Value.version) |" }) -join "`n"
$notes = @"
$(($lines -join "`n").Trim())

### Installer NeoSuite
1. Téléchargez **NeoSuite.exe** et **NeoSuite.pak** ci-dessous, dans le **même dossier** ($totalMb Mo au total).
2. Lancez **NeoSuite.exe** et acceptez la confirmation de Windows.
3. Ouvrez le panneau avec **Ctrl + Alt + N**.

Déjà installé ? NeoSuite propose cette version tout seul, en haut de son panneau.

### Versions des applications
| Appli | Version |
|---|---|
$table
"@
$notesFile = Join-Path $env:TEMP "neosuite-release-$version.md"
[System.IO.File]::WriteAllText($notesFile, $notes, (New-Object System.Text.UTF8Encoding $false))

# --- 4. Release + fichiers --------------------------------------------------------------------------
Write-Host "Création de la release $tag et envoi de $($assets.Count) fichiers..." -ForegroundColor Cyan
$ghArgs = @('release', 'create', $tag) + $assets + @('--repo', $repo, '--title', "NeoSuite $version", '--notes-file', $notesFile, '--target', (git rev-parse --abbrev-ref HEAD))
if ($Draft) { $ghArgs += '--draft' }
gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw 'La création de la release a échoué (voir le message de gh ci-dessus).' }
Write-Host "Release publiée : https://github.com/$repo/releases/tag/$tag" -ForegroundColor Green
