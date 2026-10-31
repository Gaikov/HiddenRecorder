param([switch]$SkipBuild, [switch]$NoArchive, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Version.props') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid release version: $version" }
$releaseDirectory = Join-Path $root "releases\v$version"
$archive = Join-Path $releaseDirectory "HiddenRecorder-$version-win-x64.zip"
if (-not $NoArchive -and (Test-Path -LiteralPath $releaseDirectory)) { throw "Release directory already exists; do not overwrite a release: $releaseDirectory" }
$destination = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts\HiddenRecorder-win-x64' }
# Never publish over an old directory: removed NuGet dependencies would survive there.
if (Test-Path -LiteralPath $destination) { throw "Output directory already exists. Move the previous build aside, or supply a new -OutputDirectory: $destination" }
if (-not $SkipBuild) { & "$PSScriptRoot\build.ps1" }
& .\.tools\dotnet\dotnet.exe publish src/HiddenRecorder.App/HiddenRecorder.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
if (-not (Test-Path (Join-Path $destination 'HiddenRecorder.App.pri'))) { throw 'WinUI application resource index was not published' }
$gstSource = Join-Path $root '.tools\gstreamer'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$crt = Get-ChildItem -Path (Join-Path $vs 'VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT') -Directory | Select-Object -Last 1
if (-not $crt) { throw 'VC143 redistributable DLLs not found' }
Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' | Copy-Item -Destination $destination
python "$PSScriptRoot\package_gstreamer.py" --source $gstSource --destination $destination
if ($LASTEXITCODE -ne 0) { throw 'GStreamer dependency packaging failed' }
Copy-Item README.md,DEVELOPMENT.md,THIRD-PARTY-NOTICES.md,VALIDATION.md -Destination $destination
$noticeTarget = Join-Path $destination 'licenses'
New-Item -ItemType Directory -Force $noticeTarget | Out-Null
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
foreach ($package in @('communitytoolkit.mvvm\8.4.2','microsoft.windowsappsdk.winui\1.8.260803003','microsoft.windowsappsdk.dwrite\1.8.25122902','microsoft.windowsappsdk.base\1.8.251216001','microsoft.windowsappsdk.foundation\1.8.260803002','microsoft.windowsappsdk.interactiveexperiences\1.8.260708001','microsoft.web.webview2\1.0.3179.45')) {
    $packageRoot = Join-Path $nugetRoot $package
    Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match 'license|notice' } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $noticeTarget (($package -replace '\\','-') + '-' + $_.Name))
    }
}
& "$PSScriptRoot\check-publish.ps1" -Directory $destination
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $destination 'HiddenRecorder.App.exe')).ProductVersion
if (($productVersion -split '\+')[0] -ne $version) { throw "EXE version does not match Version.props: $productVersion" }
$commit = git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit' }
$dirty = [bool](git status --porcelain)
@{ version = $version; tag = "v$version"; commit = $commit; dirty = $dirty; runtime = 'win-x64' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'release.json') -Encoding utf8
if ($NoArchive) {
    Write-Host (Join-Path $destination 'HiddenRecorder.App.exe')
} else {
    if ($dirty) { throw 'Commit source changes before creating a release archive. The unpacked build is available for validation.' }
    New-Item -ItemType Directory -Path $releaseDirectory | Out-Null
    Compress-Archive -Path "$destination\*" -DestinationPath $archive -CompressionLevel Optimal
    $checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$checksum  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Write-Host $archive
    Write-Host "$archive.sha256"
}
