# Run on Windows with PowerShell -STA. The SVG is the source for both app assets.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assets = Join-Path $PSScriptRoot '../Quota.Desktop/Assets'
[xml]$logo = Get-Content -LiteralPath (Join-Path $assets 'brand.svg') -Raw
$tile = $logo.svg.rect
$mark = $logo.svg.path
$geometry = [Windows.Media.Geometry]::Parse($mark.d)
$fill = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($tile.fill))
$ink = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($mark.stroke))
$pen = [Windows.Media.Pen]::new($ink, [double]$mark.'stroke-width')
$pen.StartLineCap = $pen.EndLineCap = $pen.LineJoin = 'Round'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $drawing = [Windows.Media.DrawingVisual]::new()
    $context = $drawing.RenderOpen()
    $context.PushTransform([Windows.Media.ScaleTransform]::new($size / 32, $size / 32))
    $context.DrawRoundedRectangle($fill, $null, [Windows.Rect]::new($tile.x, $tile.y, $tile.width, $tile.height), $tile.rx, $tile.rx)
    $context.DrawGeometry($null, $pen, $geometry)
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($drawing)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); $images.Add($stream.ToArray()) } finally { $stream.Dispose() }
}
[IO.File]::WriteAllBytes((Join-Path $assets 'brand.png'), $images[$images.Count - 1])
$writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $assets 'app.ico')))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $edge = [byte]($sizes[$i] % 256)
        $writer.Write($edge); $writer.Write($edge); $writer.Write([uint16]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write($bytes) }
} finally { $writer.Dispose() }
