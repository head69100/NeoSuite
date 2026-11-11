<#
    Construit les paquets d'installation des applis Neo, que NeoSuite télécharge et installe.
    Pour chaque appli : publication Release autonome (.NET + Windows App SDK inclus) puis zip
    dans packages\<App>.zip, et mise à jour de packages\catalog.json (version, taille).

    Utilisation :  powershell -ExecutionPolicy Bypass -File tools\build-packages.ps1
#>
param([string[]]$Apps = @('NeoDriver'))

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$packages = Join-Path $root 'packages'
New-Item -ItemType Directory -Force $packages | Out-Null

$catalogPath = Join-Path $packages 'catalog.json'
$catalog = @{}
if (Test-Path $catalogPath) {
    (Get-Content $catalogPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $catalog[$_.Name] = $_.Value }
}

foreach ($app in $Apps) {
    $project = Join-Path $root "$app\src\$app\$app.csproj"
    $publishDir = Join-Path $env:TEMP "NeoPublish\$app"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    Write-Host "Publication de $app..." -ForegroundColor Cyan
    dotnet publish $project -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $publishDir -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Échec de la publication de $app" }

    $exe = Join-Path $publishDir "$app.exe"
    $version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''

    $zip = Join-Path $packages "$app.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Write-Host "Compression de $app $version..." -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -CompressionLevel Optimal

    $catalog[$app] = [ordered]@{
        version = $version
        size    = (Get-Item $zip).Length
        sha256  = (Get-FileHash $zip -Algorithm SHA256).Hash
        built   = (Get-Date).ToString('s')
    }
    Write-Host ("{0} {1} : {2:N1} Mo" -f $app, $version, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
}

$catalog | ConvertTo-Json -Depth 3 | Set-Content $catalogPath -Encoding utf8
Write-Host "Catalogue mis à jour : $catalogPath"
