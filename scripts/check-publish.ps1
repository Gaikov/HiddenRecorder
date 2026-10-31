param([string]$Directory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\HiddenRecorder-win-x64'))
$ErrorActionPreference = 'Stop'
foreach ($name in @('HiddenRecorder.App.exe','HiddenRecorder.App.dll','HiddenRecorder.App.pri','HiddenRecorder.App.runtimeconfig.json','Assets\HiddenRecorder.ico','Microsoft.UI.Xaml.dll','Microsoft.UI.Xaml.Controls.pri','Microsoft.WindowsAppRuntime.dll','DWriteCore.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll','HiddenRecorder.Media.dll','gstreamer\bin\gstreamer-1.0-0.dll','gstreamer\dependencies.json')) {
    $path = Join-Path $Directory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Missing application runtime file: $name" }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $Directory 'HiddenRecorder.App.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.framework -or $runtimeConfig.runtimeOptions.frameworks) { throw 'The package must include .NET instead of requiring an installed shared framework.' }
foreach ($name in @('onnxruntime.dll','DirectML.dll','Microsoft.Windows.AI.dll','Microsoft.Windows.Widgets.dll')) {
    if (Test-Path -LiteralPath (Join-Path $Directory $name)) { throw "Unexpected unused Windows App SDK component: $name" }
}
$gst = Get-Content -LiteralPath (Join-Path $Directory 'gstreamer\dependencies.json') -Raw | ConvertFrom-Json
foreach ($name in $gst.runtime_dlls) {
    if (-not (Test-Path -LiteralPath (Join-Path $Directory "gstreamer\bin\$name"))) { throw "Missing GStreamer runtime DLL: $name" }
}
$stream = [IO.File]::OpenRead((Join-Path $Directory 'HiddenRecorder.App.exe'))
$reader = [IO.BinaryReader]::new($stream)
try {
    $stream.Position = 0x3c
    $peOffset = $reader.ReadInt32()
    $stream.Position = $peOffset + 24 + 68
    if ($reader.ReadUInt16() -ne 2) { throw 'The application must use the Windows GUI subsystem, not the console subsystem.' }
} finally { $reader.Dispose() }
Write-Host 'Publish check passed: GUI executable, WinUI application resources and runtime dependencies are present.'
