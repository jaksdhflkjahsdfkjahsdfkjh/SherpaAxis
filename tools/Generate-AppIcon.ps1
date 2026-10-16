param([string]$PreviewPath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$assetDirectory = Join-Path $PSScriptRoot '../src/SherpaAxis.App/Assets'
[xml]$iconSource = Get-Content -LiteralPath (Join-Path $assetDirectory 'SherpaAxis.svg') -Raw
$background = $iconSource.svg.rect
$mark = $iconSource.svg.path
$images = @()
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)

foreach ($size in $sizes) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try {
        $drawing.PushTransform([System.Windows.Media.ScaleTransform]::new($size / 64.0, $size / 64.0))
        $backgroundBrush = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString($background.fill))
        $drawing.DrawRoundedRectangle($backgroundBrush, $null, [System.Windows.Rect]::new([double]$background.x, [double]$background.y, [double]$background.width, [double]$background.height), [double]$background.rx, [double]$background.rx)
        $markBrush = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString($mark.stroke))
        $pen = [System.Windows.Media.Pen]::new($markBrush, [double]$mark.'stroke-width')
        $pen.StartLineCap = $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
        $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
        $drawing.DrawGeometry($null, $pen, [System.Windows.Media.Geometry]::Parse($mark.d))
        $drawing.Pop()
    }
    finally { $drawing.Close() }
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        $images += ,$stream.ToArray()
        if ($PreviewPath -and $size -eq 256) { [System.IO.File]::WriteAllBytes($PreviewPath, $stream.ToArray()) }
    }
    finally { $stream.Dispose() }
}

$iconStream = [System.IO.File]::Create((Join-Path $assetDirectory 'SherpaAxis.ico'))
$writer = [System.IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
}
finally { $writer.Dispose() }
