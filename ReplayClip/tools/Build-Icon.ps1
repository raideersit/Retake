<#
.SYNOPSIS
    Genera ReplayClip.ico (16 a 256 px) a partir del logo pixel-art en PNG.

.DESCRIPTION
    Escala con "vecino más cercano" para que los píxeles queden nítidos en todos los tamaños y guarda
    cada tamaño como PNG dentro del .ico (formato admitido desde Windows Vista).
    Volver a ejecutarlo si cambia el logo.

.EXAMPLE
    .\tools\Build-Icon.ps1
#>
param(
    [string] $Source = "$PSScriptRoot\..\src\ReplayClip\Assets\logo-zorro.png",
    [string] $Output = "$PSScriptRoot\..\src\ReplayClip\Assets\ReplayClip.ico"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$logo = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
$images = @()
try {
    foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap $size, $size
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        $graphics.DrawImage($logo, 0, 0, $size, $size)
        $graphics.Dispose()
        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        $images += , @{ Size = $size; Bytes = $stream.ToArray() }
    }
}
finally {
    $logo.Dispose()
}

# Formato ICO: ICONDIR (6 bytes) + una entrada de 16 bytes por imagen + los PNG.
$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0)                # reservado
$writer.Write([uint16]1)                # tipo: icono
$writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }   # 0 significa 256
    $writer.Write([byte]$dimension)     # ancho
    $writer.Write([byte]$dimension)     # alto
    $writer.Write([byte]0)              # colores de paleta
    $writer.Write([byte]0)              # reservado
    $writer.Write([uint16]1)            # planos
    $writer.Write([uint16]32)           # bits por píxel
    $writer.Write([uint32]$image.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) { $writer.Write($image.Bytes) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path (Resolve-Path (Split-Path $Output)) (Split-Path $Output -Leaf)), $file.ToArray())
Write-Host "Icono generado: $Output ($($images.Count) tamaños, $($file.Length) bytes)"
