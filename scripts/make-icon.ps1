# アプリアイコン（App/AppIcon.ico）を生成する。macOS 版の make-icon.swift と同じ図柄。
#   ./scripts/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$root = Split-Path -Parent $PSScriptRoot

function Render([int]$px) {
    $s = [double]$px
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $inset = $s * 0.1
    $rect = New-Object System.Windows.Rect($inset, $inset, ($s - $inset * 2), ($s - $inset * 2))
    $radius = $rect.Width * 0.225
    $gradient = New-Object System.Windows.Media.LinearGradientBrush(
        [System.Windows.Media.Color]::FromRgb(51, 92, 191), [System.Windows.Media.Color]::FromRgb(26, 46, 107), 90.0)
    $dc.DrawRoundedRectangle($gradient, $null, $rect, $radius, $radius)

    # 波形の棒
    $heights = @(0.22, 0.42, 0.64, 0.46, 0.30, 0.52, 0.36)
    $barW = $rect.Width * 0.07
    $gap = $rect.Width * 0.045
    $total = $heights.Count * $barW + ($heights.Count - 1) * $gap
    $x = ($rect.Left + $rect.Width / 2) - $total / 2
    $white = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(242, 255, 255, 255))
    $midY = $rect.Top + $rect.Height / 2
    foreach ($h in $heights) {
        $bh = $rect.Height * $h
        $bar = New-Object System.Windows.Rect($x, ($midY - $bh / 2 - $rect.Height * 0.06), $barW, $bh)
        $dc.DrawRoundedRectangle($white, $null, $bar, $barW / 2, $barW / 2)
        $x += $barW + $gap
    }
    # テキスト行
    $faint = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(140, 255, 255, 255))
    $widths = @(0.56, 0.40)
    for ($i = 0; $i -lt $widths.Count; $i++) {
        $w = $widths[$i]
        $lineH = $rect.Height * 0.045
        $y = $rect.Bottom - $rect.Height * (0.17 - $i * 0.08) - $lineH
        $line = New-Object System.Windows.Rect(($rect.Left + $rect.Width / 2 - $rect.Width * $w / 2), $y, ($rect.Width * $w), $lineH)
        $dc.DrawRoundedRectangle($faint, $null, $line, $lineH / 2, $lineH / 2)
    }
    $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($px, $px, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
    return , $ms.ToArray()
}

# ICO ファイル（各サイズの PNG をそのまま格納する）
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = @($sizes | ForEach-Object { , (Render $_) })
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $dim = if ($size -ge 256) { 0 } else { $size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
$dest = Join-Path $root 'App\AppIcon.ico'
[System.IO.File]::WriteAllBytes($dest, $out.ToArray())
Write-Host "wrote $dest"
