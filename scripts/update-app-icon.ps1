[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$images = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../AudioPilot/Images'))
$magick = (Get-Command magick -ErrorAction Stop).Source
$frames = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('AudioPilot-Icon-' + [guid]::NewGuid().ToString('N') + '.png')
try {
    foreach ($size in $sizes) {
        $source = if ($size -le 20) { 'audiopilot-small.svg' } else { 'audiopilot.svg' }
        & $magick -background none -density 384 (Join-Path $images $source) -resize "${size}x${size}" -depth 8 "PNG32:$temporary"
        if ($LASTEXITCODE -ne 0) { throw "Icon frame rendering failed: $size" }
        $frames.Add([IO.File]::ReadAllBytes($temporary))
    }
    # Windows 10 and WPF support PNG-compressed ICO frames at every embedded size.
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + (16 * $sizes.Count)
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
        $writer.Flush()
        [IO.File]::WriteAllBytes((Join-Path $images 'audiopilot.ico'), $stream.ToArray())
    }
    finally { $writer.Dispose(); $stream.Dispose() }
    & $magick -background none -density 384 (Join-Path $images 'audiopilot.svg') -resize 1024x1024 -depth 8 (Join-Path $images 'audiopilot.png')
    if ($LASTEXITCODE -ne 0) { throw 'Icon PNG rendering failed.' }
}
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
