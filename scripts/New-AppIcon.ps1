param([string]$IconDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets/icons'))
$ErrorActionPreference = 'Stop'
$IconDirectory = (Resolve-Path -LiteralPath $IconDirectory).ProviderPath
$frames = @(
    foreach ($size in @(16, 20, 24, 32, 48, 64, 128, 256)) {
        $path = Join-Path $IconDirectory "rockwell_plc_hmi_icon_$($size)x$size.png"
        $bytes = [IO.File]::ReadAllBytes($path)
        if ($bytes.Length -lt 33 -or [BitConverter]::ToString($bytes, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A' -or
            [Text.Encoding]::ASCII.GetString($bytes, 12, 4) -ne 'IHDR') { throw "Invalid PNG frame: $path" }
        $width = $bytes[16] * 16777216 + $bytes[17] * 65536 + $bytes[18] * 256 + $bytes[19]
        $height = $bytes[20] * 16777216 + $bytes[21] * 65536 + $bytes[22] * 256 + $bytes[23]
        if ($width -ne $size -or $height -ne $size -or $bytes[24] -ne 8 -or $bytes[25] -ne 6) {
            throw "Expected $($size)x$size pixels in an 8-bit RGBA PNG frame: $path"
        }
        [pscustomobject]@{ Size = $size; Bytes = $bytes }
    }
)

# ICO entries reference the original PNG bytes; no resizing or recompression.
$stream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    $writer.Flush()
    $iconBytes = $stream.ToArray()
} finally { $writer.Dispose(); $stream.Dispose() }

$destination = Join-Path $IconDirectory 'RockwellMCPServer.ico'
$unchanged = $false
if (Test-Path -LiteralPath $destination) {
    $existing = [IO.File]::ReadAllBytes($destination)
    $unchanged = [Convert]::ToBase64String($existing) -eq [Convert]::ToBase64String($iconBytes)
}
if (!$unchanged) { [IO.File]::WriteAllBytes($destination, $iconBytes) }
Write-Output "Application icon: $destination ($($frames.Count) exact-size frames)"
