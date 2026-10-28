param([switch]$Test, [switch]$NativeTests, [switch]$Package)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 compiler was not found. Enable .NET Framework 4.8 or install its developer pack.' }
$framework = Split-Path $compiler
$output = Join-Path $projectRoot 'release\RugWindows'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$resources = Join-Path $projectRoot 'original\under-the-rug-main\Resources'
if (-not (Test-Path -LiteralPath $resources)) { $resources = Join-Path $projectRoot 'Resources' }
$buildDirectory = Join-Path $projectRoot 'build'
New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$image = New-Object System.Drawing.Bitmap (Join-Path $resources 'icon_1024.png')
$small = New-Object System.Drawing.Bitmap $image,64,64
$iconHandle = $small.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($iconHandle)
$iconFile = Join-Path $buildDirectory 'RugWindows.ico'
$iconStream = [IO.File]::Create($iconFile)
try { $icon.Save($iconStream) } finally { $iconStream.Dispose(); $icon.Dispose(); $small.Dispose(); $image.Dispose() }
if (-not ('RugBuildNative' -as [type])) { Add-Type 'public static class RugBuildNative { [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool DestroyIcon(System.IntPtr handle); }' }
[RugBuildNative]::DestroyIcon($iconHandle) | Out-Null
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.Serialization.dll','System.Xml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$arguments = @('/nologo','/target:winexe','/platform:x64','/langversion:5','/optimize+','/utf8output',('/win32icon:' + $iconFile),('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')),('/out:' + (Join-Path $output 'RugWindows.exe'))) + $references + $sources
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
$resourceOutput = Join-Path $output 'Resources'
New-Item -ItemType Directory -Path $resourceOutput -Force | Out-Null
Get-ChildItem -LiteralPath $resources | Where-Object { $_.PSIsContainer -or $_.Name -eq 'icon_1024.png' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $resourceOutput -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $projectRoot 'RugWindows.exe.config') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README-RU.md') -Destination (Join-Path $output 'ПРОЧИТАЙ МЕНЯ.txt') -Force
if ($Test -or $NativeTests) {
    $testOutput = Join-Path $projectRoot 'tests\ClothTests.exe'
    & $compiler /nologo /target:exe /platform:x64 /langversion:5 /optimize+ ('/out:' + $testOutput) @references (Join-Path $projectRoot 'src\Cloth.cs') (Join-Path $projectRoot 'tests\ClothTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $testOutput
    if ($LASTEXITCODE -ne 0) { throw 'Cloth tests failed.' }
    $desktopTestOutput = Join-Path $projectRoot 'tests\DesktopTests.exe'
    & $compiler /nologo /target:exe /platform:x64 /langversion:5 /optimize+ ('/out:' + $desktopTestOutput) @references (Join-Path $projectRoot 'src\Desktop.cs') (Join-Path $projectRoot 'tests\DesktopTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Desktop safety test compilation failed.' }
    if ($NativeTests) { & $desktopTestOutput (Join-Path $projectRoot 'tests') --native }
    else { & $desktopTestOutput (Join-Path $projectRoot 'tests') }
    if ($LASTEXITCODE -ne 0) { throw 'Desktop safety tests failed.' }
    $wallpaperTestOutput = Join-Path $projectRoot 'tests\WallpaperTests.exe'
    & $compiler /nologo /target:exe /platform:x64 /langversion:5 /optimize+ ('/out:' + $wallpaperTestOutput) @references (Join-Path $projectRoot 'src\State.cs') (Join-Path $projectRoot 'src\Wallpaper.cs') (Join-Path $projectRoot 'tests\WallpaperTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Wallpaper test compilation failed.' }
    & $wallpaperTestOutput (Join-Path $projectRoot 'tests')
    if ($LASTEXITCODE -ne 0) { throw 'Wallpaper tests failed.' }
    $startupTestOutput = Join-Path $projectRoot 'tests\StartupTests.exe'
    & $compiler /nologo /target:exe /platform:x64 /langversion:5 /optimize+ ('/out:' + $startupTestOutput) @references (Join-Path $projectRoot 'src\StartupService.cs') (Join-Path $projectRoot 'tests\StartupTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Startup test compilation failed.' }
    & $startupTestOutput (Join-Path $projectRoot 'tests')
    if ($LASTEXITCODE -ne 0) { throw 'Startup tests failed.' }
}
if ($Package) {
    $zip = Join-Path $projectRoot 'release\RugWindows-1.1.0-win-x64.zip'
    Compress-Archive -LiteralPath $output -DestinationPath $zip -Force
    Write-Host ('Package: ' + $zip)
    & (Join-Path $projectRoot 'package-source.ps1')
}
Write-Host ('Built: ' + (Join-Path $output 'RugWindows.exe'))
