param([switch]$SkipBuild, [switch]$NoArchive)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if (-not $SkipBuild) { & "$PSScriptRoot\build.ps1" }
$destination = Join-Path $root 'artifacts\HiddenRecorder-win-x64'
& .\.tools\dotnet\dotnet.exe publish src/HiddenRecorder.App/HiddenRecorder.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
if (-not (Test-Path (Join-Path $destination 'HiddenRecorder.App.pri'))) { throw 'WinUI application resource index was not published' }
$gstSource = Join-Path $root '.tools\gstreamer'
$gstTarget = Join-Path $destination 'gstreamer'
foreach ($subdir in @('bin','lib\gstreamer-1.0','libexec\gstreamer-1.0','share')) { New-Item -ItemType Directory -Force (Join-Path $gstTarget $subdir) | Out-Null }
# Keep the runtime library set intact; the plugin set is limited to the implemented pipeline.
Get-ChildItem -LiteralPath "$gstSource\bin" -Filter '*.dll' | Copy-Item -Destination "$gstTarget\bin"
$plugins = @('coreelements','app','typefindfunctions','playback','videoconvertscale','videorate','audioconvert','audioresample','level','mediafoundation','wasapi2','wasapi','d3d11','openh264','videoparsersbad','audioparsers','isomp4','multifile','jpeg','autodetect')
foreach ($plugin in $plugins) {
    $source = Join-Path $gstSource "lib\gstreamer-1.0\gst$plugin.dll"
    if (-not (Test-Path $source)) { throw "Missing plugin: $plugin" }
    Copy-Item -LiteralPath $source -Destination "$gstTarget\lib\gstreamer-1.0"
}
Copy-Item -LiteralPath "$gstSource\libexec\gstreamer-1.0\gst-plugin-scanner.exe" -Destination "$gstTarget\libexec\gstreamer-1.0"
Copy-Item -LiteralPath "$gstSource\share\licenses" -Destination "$gstTarget\share" -Recurse -Force
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$crt = Get-ChildItem -Path (Join-Path $vs 'VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT') -Directory | Select-Object -Last 1
if (-not $crt) { throw 'VC143 redistributable DLLs not found' }
Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' | Copy-Item -Destination $destination
Copy-Item README.md,DEVELOPMENT.md,THIRD-PARTY-NOTICES.md,VALIDATION.md -Destination $destination
$noticeTarget = Join-Path $destination 'licenses'
New-Item -ItemType Directory -Force $noticeTarget | Out-Null
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
foreach ($package in @('communitytoolkit.mvvm\8.4.2','microsoft.windowsappsdk\1.8.260921001','microsoft.windowsappsdk.winui\1.8.260803003')) {
    $packageRoot = Join-Path $nugetRoot $package
    Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match 'license|notice' } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $noticeTarget (($package -replace '\\','-') + '-' + $_.Name))
    }
}
& "$PSScriptRoot\check-publish.ps1" -Directory $destination
if ($NoArchive) {
    Write-Host (Join-Path $destination 'HiddenRecorder.App.exe')
} else {
    $archive = Join-Path $root 'artifacts\HiddenRecorder-win-x64.zip'
    Compress-Archive -Path "$destination\*" -DestinationPath $archive -Force
    Get-FileHash $archive -Algorithm SHA256 | Format-List
    Write-Host $archive
}
