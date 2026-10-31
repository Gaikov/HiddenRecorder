param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
New-Item -ItemType Directory -Force .tools | Out-Null
if (-not (Test-Path .tools\dotnet\dotnet.exe)) {
    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile .tools\dotnet-install.ps1
    & .\.tools\dotnet-install.ps1 -Version 10.0.403 -InstallDir "$root\.tools\dotnet" -NoPath
    if ($LASTEXITCODE -ne 0) { throw 'dotnet installation failed' }
}
$packages = @{
    'gstreamer-1.0-msvc-x86_64-1.26.11.msi' = '31cbc21fa0950b5c1e79c80959b2799805cb05a7a35953a13a9f790776137605'
    'gstreamer-1.0-devel-msvc-x86_64-1.26.11.msi' = 'af0a0720692052aaf5fb0cf2847ef34a87016ef0f914328d516f049e6c86f587'
}
if (-not (Test-Path .tools\gstreamer\.extracted-1.26.11)) {
    foreach ($name in $packages.Keys | Sort-Object -Descending) {
        $archive = Join-Path .tools $name
        if (-not (Test-Path $archive)) { Invoke-WebRequest "https://gstreamer.freedesktop.org/data/pkg/windows/1.26.11/msvc/$name" -OutFile $archive }
        if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $packages[$name]) { throw "Checksum mismatch: $name" }
        python scripts\extract_msi.py $archive .tools\gstreamer
        if ($LASTEXITCODE -ne 0) { throw "Extraction failed: $name" }
    }
    Set-Content .tools\gstreamer\.extracted-1.26.11 '1.26.11'
}
Write-Host 'Local SDKs ready. Visual Studio C++ Build Tools and Windows SDK are required to compile.'
