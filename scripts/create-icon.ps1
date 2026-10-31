param(
    [string]$Source = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\HiddenRecorder.App\Assets\HiddenRecorder.png'),
    [string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\HiddenRecorder.App\Assets\HiddenRecorder.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
# Format conversion only: preserve the generated artwork and its alpha channel.
$sourceImage = [Drawing.Image]::FromFile($Source)
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [Collections.Generic.List[byte[]]]::new()
try {
    if ($sourceImage.Width -ne $sourceImage.Height) { throw 'The icon source must be square.' }
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $memory = [IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($sourceImage, 0, 0, $size, $size)
            $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($memory.ToArray())
        } finally { $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $sourceImage.Dispose() }
$writer = [IO.BinaryWriter]::new([IO.File]::Create($Destination))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
Write-Host "Created $Destination with sizes: $($sizes -join ', ')"
