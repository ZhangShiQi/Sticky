# The SVG is the source of truth. Uses Windows' WPF renderer; no image packages required.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Sticky.App\Assets'))
[xml]$svg = Get-Content -LiteralPath (Join-Path $assets 'Sticky.svg') -Raw
$group = New-Object Windows.Media.DrawingGroup
foreach ($node in $svg.DocumentElement.ChildNodes) {
    if ($node.LocalName -in @('title', 'desc', '#comment')) { continue }
    switch ($node.LocalName) {
        'rect' {
            $geometry = New-Object Windows.Media.RectangleGeometry
            $geometry.Rect = New-Object Windows.Rect ([double]$node.x), ([double]$node.y), ([double]$node.width), ([double]$node.height)
            $geometry.RadiusX = [double]$node.rx; $geometry.RadiusY = [double]$node.rx
        }
        'path' { $geometry = [Windows.Media.Geometry]::Parse($node.d) }
        default { throw "Unsupported SVG element: $($node.LocalName)" }
    }
    $brush = $null; $pen = $null
    if ($node.HasAttribute('fill') -and $node.fill -ne 'none') { $brush = [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($node.fill) }
    if ($node.HasAttribute('stroke')) {
        $pen = New-Object Windows.Media.Pen ([Windows.Media.BrushConverter]::new().ConvertFromInvariantString($node.stroke)), ([double]$node.'stroke-width')
        if ($node.'stroke-linecap' -eq 'round') { $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round' }
    }
    $group.Children.Add((New-Object Windows.Media.GeometryDrawing $brush, $pen, $geometry))
}
$group.Freeze()
$image = New-Object Windows.Media.DrawingImage $group
$xaml = [Windows.Markup.XamlWriter]::Save($image).Replace('<DrawingImage ', '<DrawingImage x:Key="StickyLogo" ')
$dictionary = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' + $xaml + '</ResourceDictionary>'
[IO.File]::WriteAllText((Join-Path $assets 'StickyIcon.xaml'), $dictionary, [Text.UTF8Encoding]::new($false))

$frames = @()
foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.PushTransform((New-Object Windows.Media.ScaleTransform ($size / 512.0), ($size / 512.0)))
    $context.DrawDrawing($group); $context.Pop(); $context.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    $bytes = $stream.ToArray(); $stream.Dispose()
    $frames += ,@{ Size = $size; Bytes = $bytes }
    if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assets 'Sticky.preview.png'), $bytes) }
}
$output = [IO.File]::Create((Join-Path $assets 'Sticky.ico'))
$writer = New-Object IO.BinaryWriter $output
try {
    $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([UInt16]1); $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frame.Bytes.Length); $writer.Write([UInt32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $output.Dispose() }
Write-Output 'Generated vector WPF logo, 9-size Windows ICO, and preview from Sticky.svg.'
