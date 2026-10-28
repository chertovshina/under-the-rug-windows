$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$stage = Join-Path $projectRoot ('build\source-' + [Guid]::NewGuid().ToString('N') + '\under-the-rug-windows')
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($name in @('src','docs')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $stage -Recurse }
$tests = Join-Path $stage 'tests'
New-Item -ItemType Directory -Path $tests | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $tests }
foreach ($name in @('build.ps1','package-source.ps1','app.manifest','RugWindows.csproj','RugWindows.exe.config','LICENSE','README.md','README-RU.md','CHANGELOG.md','.gitignore')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $stage
}
$resources = Join-Path $projectRoot 'original\under-the-rug-main\Resources'
if (-not (Test-Path -LiteralPath $resources)) { $resources = Join-Path $projectRoot 'Resources' }
$resourceOutput = Join-Path $stage 'Resources'
New-Item -ItemType Directory -Path $resourceOutput | Out-Null
foreach ($name in @('rugs','floors','icon_1024.png')) { Copy-Item -LiteralPath (Join-Path $resources $name) -Destination $resourceOutput -Recurse }
$release = Join-Path $projectRoot 'release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$zip = Join-Path $release 'RugWindows-1.1.0-source.zip'
Compress-Archive -LiteralPath $stage -DestinationPath $zip -Force
Write-Host ('Source: ' + $zip)
$githubZip = Join-Path $release 'RugWindows-1.1.0-github.zip'
Copy-Item -LiteralPath $zip -Destination $githubZip -Force
Write-Host ('GitHub: ' + $githubZip)
