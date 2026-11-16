# Gera o icone do Softphone (GDI+, sem nada externo): softphone.ico (BMP ate 128, PNG em 256) + previa. So ASCII.
param([string]$Saida = (Join-Path $PSScriptRoot '..\src\Softphone.App\Assets'))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory $Saida -Force | Out-Null

function Cor($hex, $alpha = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($hex); [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B) }

function Caminho-Arredondado([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# fonte de icones do Windows (Fluent no 11, MDL2 no 10); glifo E717 = telefone
$familias = [System.Drawing.Text.InstalledFontCollection]::new().Families | ForEach-Object Name
$fonteIcone = if ($familias -contains 'Segoe Fluent Icons') { 'Segoe Fluent Icons' } else { 'Segoe MDL2 Assets' }

function Desenhar([bool]$simples) {
    $bmp = New-Object System.Drawing.Bitmap 1024, 1024, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform(4, 4)

    $fundo = Caminho-Arredondado 6 6 244 244 54
    $deg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(256, 256)), (Cor '#3D8BFF'), (Cor '#173F86'))
    $g.FillPath($deg, $fundo)
    $brilho = New-Object System.Drawing.Drawing2D.GraphicsPath; $brilho.AddEllipse(-40, -150, 336, 260)
    $clip = $g.Clip; $g.SetClip($fundo)
    $g.FillPath((New-Object System.Drawing.SolidBrush((Cor '#FFFFFF' 34))), $brilho)
    $g.Clip = $clip
    $g.DrawPath((New-Object System.Drawing.Pen((Cor '#0B2A63' 120), 2.5)), $fundo)

    # fone (glifo) em branco
    $tam = if ($simples) { 170 } else { 150 }
    $glifo = New-Object System.Drawing.Drawing2D.GraphicsPath
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $ox = if ($simples) { 128 } else { 116 }; $oy = if ($simples) { 132 } else { 140 }
    $glifo.AddString([string][char]0xE717, (New-Object System.Drawing.FontFamily($fonteIcone)), 0, $tam, (New-Object System.Drawing.RectangleF(($ox - 128), ($oy - 128), 256, 256)), $fmt)
    foreach ($k in 1..5) {
        $m = New-Object System.Drawing.Drawing2D.Matrix; $m.Translate(($k * 0.5), (2 + $k * 1.2))
        $s = $glifo.Clone(); $s.Transform($m)
        $g.FillPath((New-Object System.Drawing.SolidBrush((Cor '#06173D' 22))), $s)
    }
    $g.FillPath((New-Object System.Drawing.SolidBrush((Cor '#FFFFFF'))), $glifo)

    # ondas de som (so no desenho grande)
    if (-not $simples) {
        $pen = New-Object System.Drawing.Pen((Cor '#FFFFFF'), 11); $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawArc($pen, 132, 42, 78, 78, 300, 90)
        $pen2 = New-Object System.Drawing.Pen((Cor '#FFFFFF' 170), 11); $pen2.StartCap = 'Round'; $pen2.EndCap = 'Round'
        $g.DrawArc($pen2, 118, 20, 122, 122, 300, 90)
    }
    $g.Dispose()
    return $bmp
}

function Reduzir($origem, [int]$tam) {
    $dst = New-Object System.Drawing.Bitmap $tam, $tam, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($origem, (New-Object System.Drawing.Rectangle(0, 0, $tam, $tam)), 0, 0, $origem.Width, $origem.Height, 'Pixel')
    $g.Dispose()
    return $dst
}

$completo = Desenhar $false
$simples = Desenhar $true
$tamanhos = 256, 128, 64, 48, 32, 24, 16
$imagens = @{}
foreach ($t in $tamanhos) {
    $fonte = if ($t -le 32) { $simples } else { $completo }
    $imagens[$t] = Reduzir $fonte $t
}
$imagens[256].Save((Join-Path $Saida 'softphone-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)

$entradas = @()
foreach ($t in ($tamanhos | Sort-Object)) {
    $bm = $imagens[$t]
    if ($t -ge 256) {
        $ms = New-Object IO.MemoryStream; $bm.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $dados = $ms.ToArray()
    } else {
        $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter($ms)
        $bw.Write([int]40); $bw.Write([int]$t); $bw.Write([int]($t * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $t - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $t; $x++) { $c = $bm.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
        $linhaMascara = [int]([Math]::Ceiling($t / 32.0) * 4)
        $bw.Write((New-Object byte[] ($linhaMascara * $t)))
        $bw.Flush(); $dados = $ms.ToArray()
    }
    $entradas += [pscustomobject]@{ Tam = $t; Dados = $dados }
}
$ico = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter($ico)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entradas.Count)
$offset = 6 + 16 * $entradas.Count
foreach ($e in $entradas) {
    $dim = if ($e.Tam -ge 256) { 0 } else { $e.Tam }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$e.Dados.Length); $w.Write([int]$offset); $offset += $e.Dados.Length
}
foreach ($e in $entradas) { $w.Write($e.Dados) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $Saida 'softphone.ico'), $ico.ToArray())

$prev = New-Object System.Drawing.Bitmap 520, 200
$g = [System.Drawing.Graphics]::FromImage($prev); $g.SmoothingMode = 'AntiAlias'
$g.FillRectangle((New-Object System.Drawing.SolidBrush((Cor '#F3F5F8'))), 0, 0, 260, 200)
$g.FillRectangle((New-Object System.Drawing.SolidBrush((Cor '#12161C'))), 260, 0, 260, 200)
foreach ($lado in 0, 260) {
    $g.DrawImage($imagens[128], ($lado + 16), 36, 128, 128)
    $g.DrawImage($imagens[48], ($lado + 160), 36, 48, 48)
    $g.DrawImage($imagens[32], ($lado + 160), 100, 32, 32)
    $g.DrawImage($imagens[16], ($lado + 210), 108, 16, 16)
}
$g.Dispose()
$prev.Save((Join-Path $Saida 'softphone-previa.png'), [System.Drawing.Imaging.ImageFormat]::Png)
"ico: " + (Get-Item (Join-Path $Saida 'softphone.ico')).Length + " bytes ($fonteIcone)"
