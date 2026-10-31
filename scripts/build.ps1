param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { throw 'Run scripts/bootstrap.ps1 first.' }
cmake -S native -B build/native -A x64
if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed' }
cmake --build build/native --config Release
if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
& $dotnet build src/HiddenRecorder.App/HiddenRecorder.App.csproj -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed' }
if (-not $SkipTests) {
    & $dotnet test tests/HiddenRecorder.Tests/HiddenRecorder.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Managed tests failed' }
    $env:PATH = "$root\.tools\gstreamer\bin;" + $env:PATH
    $env:GST_PLUGIN_PATH = "$root\.tools\gstreamer\lib\gstreamer-1.0"
    & .\build\native\Release\ring-tests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Native ring tests failed' }
}
