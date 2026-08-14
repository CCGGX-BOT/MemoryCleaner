# Generates app.ico (16/32/48/256) for embedding into the exe
Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [float]($size / 256.0)

    # dark blue gradient rounded background
    $bx = [float](6 * $s); $by = [float](6 * $s); $bw = [float](244 * $s); $bh = [float](244 * $s)
    $bgRect = [System.Drawing.RectangleF]::new($bx, $by, $bw, $bh)
    $bgPath = New-RoundedRectPath $bx $by $bw $bh ([float](48 * $s))
    $c1 = [System.Drawing.Color]::FromArgb(255, 30, 58, 138)
    $c2 = [System.Drawing.Color]::FromArgb(255, 59, 130, 246)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($bgRect, $c1, $c2, 45.0)
    $g.FillPath($brush, $bgPath)
    $brush.Dispose()

    # RAM stick (white rounded bar)
    $sx = [float](52 * $s); $sy = [float](64 * $s); $sw = [float](152 * $s); $sh = [float](128 * $s)
    $stickPath = New-RoundedRectPath $sx $sy $sw $sh ([float](22 * $s))
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 245, 247, 250))
    $g.FillPath($white, $stickPath)
    $white.Dispose()

    # memory chips (4 green squares)
    $chipBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 34, 197, 94))
    for ($i = 0; $i -lt 4; $i++) {
        $cx = [float](68 * $s + $i * (36 * $s))
        $g.FillRectangle($chipBrush, $cx, [float](92 * $s), [float](28 * $s), [float](32 * $s))
    }
    $chipBrush.Dispose()

    # gold contacts at bottom
    $gold = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 245, 158, 11))
    for ($i = 0; $i -lt 7; $i++) {
        $cx = [float](62 * $s + $i * (24 * $s))
        $g.FillRectangle($gold, $cx, [float](168 * $s), [float](19 * $s), [float](12 * $s))
    }
    $gold.Dispose()

    # speed accent: three white lines top-right
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(220, 255, 255, 255), [float](8 * $s))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, [float](150 * $s), [float](40 * $s), [float](176 * $s), [float](40 * $s))
    $g.DrawLine($pen, [float](156 * $s), [float](52 * $s), [float](182 * $s), [float](52 * $s))
    $g.DrawLine($pen, [float](162 * $s), [float](64 * $s), [float](188 * $s), [float](64 * $s))
    $pen.Dispose()

    $g.Dispose()
    return $bmp
}

function ConvertTo-Dib([System.Drawing.Bitmap]$bmp) {
    # Full classic ICO DIB: BITMAPINFOHEADER + XOR (BGRA, bottom-up) + AND mask (1bpp, padded)
    $w = $bmp.Width; $h = $bmp.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $xOrBytes = [byte[]]::new($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $xOrBytes, 0, $xOrBytes.Length)
    $bmp.UnlockBits($data)

    $andStride = [int]((($w + 31) / 32) * 4)
    $andBytes = [byte[]]::new($andStride * $h)   # all zeros: transparency comes from alpha

    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([int]40)
    $bw.Write([int]$w)
    $bw.Write([int]($h * 2))
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int]0)
    $bw.Write([int]0)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        $bw.Write($xOrBytes, $y * $stride, $stride)
    }
    $bw.Write($andBytes)
    $bw.Flush()
    $out = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    Write-Output -NoEnumerate $out
}

$sizes = @(16, 32, 48, 256)
$entries = @()
$blobs = @()
$offset = 6 + 16 * $sizes.Count

foreach ($sz in $sizes) {
    $bmp = Draw-Icon $sz
    $blob = ConvertTo-Dib $bmp
    $bmp.Dispose()
    $entries += ,@($sz, $blob.Length, $offset)
    $blobs += ,$blob
    $offset += $blob.Length
}

$outMs = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($outMs)
$bw.Write([int16]0)
$bw.Write([int16]1)
$bw.Write([int16]$sizes.Count)
foreach ($e in $entries) {
    $w = 0; $h = 0
    if ($e[0] -lt 256) { $w = $e[0]; $h = $e[0] }
    $bw.Write([byte]$w)
    $bw.Write([byte]$h)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([uint32]$e[1])
    $bw.Write([uint32]$e[2])
}
foreach ($b in $blobs) { $bw.Write($b) }
$bw.Flush()
[System.IO.File]::WriteAllBytes("I:\deepseek\MemoryCleaner\src\app.ico", $outMs.ToArray())
$bw.Dispose(); $outMs.Dispose()
Write-Output ("app.ico generated: {0} bytes" -f (Get-Item "I:\deepseek\MemoryCleaner\src\app.ico").Length)
