param([string]$Directory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\HiddenRecorder-win-x64'))
$ErrorActionPreference = 'Stop'
foreach ($name in @('HiddenRecorder.App.exe','HiddenRecorder.App.dll','HiddenRecorder.App.pri','HiddenRecorder.App.runtimeconfig.json','Microsoft.UI.Xaml.dll','Microsoft.UI.Xaml.Controls.pri','HiddenRecorder.Media.dll','gstreamer\bin\gstreamer-1.0-0.dll')) {
    $path = Join-Path $Directory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Missing application runtime file: $name" }
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
