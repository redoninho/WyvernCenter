# Turns the wyvern icon into block-character art with the name underneath (docs/wyvern-ascii.txt).
# Run: powershell -ExecutionPolicy Bypass -File tools/art/ascii-art.ps1 -Image tools/art/icon_native.png -Out docs/wyvern-ascii.txt
param([string]$Image, [string]$Out, [int]$FontPx = 17, [string]$FontName = "Arial Black")
Add-Type -AssemblyName System.Drawing

function Opaque($bmp, $x, $y) { $x -ge 0 -and $y -ge 0 -and $x -lt $bmp.Width -and $y -lt $bmp.Height -and $bmp.GetPixel($x, $y).A -gt 100 }
function Lum($p) { (0.299 * $p.R + 0.587 * $p.G + 0.114 * $p.B) / 255.0 }

# Two image pixels per character (top / bottom half): exact pixel-art shape. Full cells shaded by brightness.
function Blocks($bmp, [bool]$shade) {
    $minX = $bmp.Width; $maxX = -1; $minY = $bmp.Height; $maxY = -1
    for ($y = 0; $y -lt $bmp.Height; $y++) { for ($x = 0; $x -lt $bmp.Width; $x++) { if (Opaque $bmp $x $y) {
        if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }; if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y } } } }
    $out = New-Object System.Collections.Generic.List[string]
    for ($y = $minY; $y -le $maxY; $y += 2) {
        $sb = New-Object System.Text.StringBuilder
        for ($x = $minX; $x -le $maxX; $x++) {
            $t = Opaque $bmp $x $y; $b = Opaque $bmp $x ($y + 1)
            if ($t -and $b) {
                if (-not $shade) { [void]$sb.Append('█'); continue }
                $l = ((Lum $bmp.GetPixel($x, $y)) + (Lum $bmp.GetPixel($x, $y + 1))) / 2
                [void]$sb.Append($(if ($l -gt 0.36) { '█' } elseif ($l -gt 0.24) { '▓' } else { '▒' }))
            }
            elseif ($t) { [void]$sb.Append('▀') } elseif ($b) { [void]$sb.Append('▄') } else { [void]$sb.Append(' ') }
        }
        $out.Add($sb.ToString().TrimEnd())
    }
    return ,$out
}

function TextBitmap([string]$text) {
    $font = New-Object System.Drawing.Font($FontName, $FontPx, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $probe = [System.Drawing.Graphics]::FromImage((New-Object System.Drawing.Bitmap 1, 1))
    $sz = $probe.MeasureString($text, $font)
    $bmp = New-Object System.Drawing.Bitmap ([int]$sz.Width + 4), ([int]$sz.Height + 4)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::Transparent)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::SingleBitPerPixelGridFit
    $g.DrawString($text, $font, [System.Drawing.Brushes]::White, 2, 2)
    return $bmp
}

$icon = Blocks ([System.Drawing.Bitmap]::FromFile($Image)) $true
$word1 = Blocks (TextBitmap "WYVERN") $false
$word2 = Blocks (TextBitmap "CENTER") $false

$width = (@($icon) + @($word1) + @($word2) | Measure-Object -Property Length -Maximum).Maximum
function Center($s) { (' ' * [Math]::Max(0, [int](($width - $s.Length) / 2))) + $s }
$all = @(); $all += $icon | ForEach-Object { Center $_ }; $all += ''
$all += $word1 | ForEach-Object { Center $_ }; $all += $word2 | ForEach-Object { Center $_ }
$all += ''; $all += Center 'fans · profiles · GPU · overlay  for MSI laptops'
[IO.File]::WriteAllText($Out, ($all -join "`n") + "`n", (New-Object System.Text.UTF8Encoding $false))
"wrote $Out ($($all.Count) lines, $width wide)"
