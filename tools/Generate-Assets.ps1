#Requires -Version 5.1
<#
.SYNOPSIS
  Generates every HDRSnip brand asset from one vector definition.

.DESCRIPTION
  The mark is an aperture bracket frame around a core that has been cut on the
  diagonal: the muted half is clipped SDR, the luminous half is the HDR range
  that survives. Geometry lives in $Mark below and is the single source for the
  app icon, the transparent logo, the README/web favicons and every MSIX tile,
  so nothing can drift.

  Rendering needs WPF, which needs an STA thread. pwsh 7 is MTA by default, so
  the work runs in an STA runspace.

.EXAMPLE
  .\tools\Generate-Assets.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
if (-not $OutputRoot) { $OutputRoot = Split-Path $PSScriptRoot -Parent }

$Work = {
    param([string]$Root)

    $ErrorActionPreference = 'Stop'
    Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

    # ---------------------------------------------------------------- palette
    $Palette = @{
        Cyan   = '#FF4CC2FF'   # ramp start / accent
        Violet = '#FFA855F7'   # ramp end
        Muted  = '#FF41506E'   # clipped SDR, dark stop
        Muted2 = '#FF5C6E92'   # clipped SDR, light stop
    }

    function ConvertTo-Color([string]$Hex) {
        return [System.Windows.Media.ColorConverter]::ConvertFromString($Hex)
    }

    # ------------------------------------------------------------- geometry
    # Two optical sizes: hairline brackets vanish below ~48px, so small icons
    # get a tighter frame with heavier strokes and a larger core.
    $Mark = @{
        full = @{
            Inset  = 128.0; Stroke = 60.0; Arm = 224.0; Radius = 96.0
            Core   = 348.0; CoreTo = 676.0; CoreRadius = 68.0; Gap = 40.0
        }
        compact = @{
            Inset  = 92.0;  Stroke = 100.0; Arm = 236.0; Radius = 104.0
            Core   = 328.0; CoreTo = 696.0; CoreRadius = 80.0; Gap = 62.0
        }
    }

    # Four L-shaped corner brackets, as SVG path mini-language on a 1024 canvas.
    function Get-BracketPaths($M) {
        $a = $M.Inset
        $b = 1024.0 - $M.Inset
        $r = $M.Radius
        $arm = $M.Arm
        return @(
            "M $($a + $arm),$a L $($a + $r),$a A $r,$r 0 0 0 $a,$($a + $r) L $a,$($a + $arm)"
            "M $($b - $arm),$a L $($b - $r),$a A $r,$r 0 0 1 $b,$($a + $r) L $b,$($a + $arm)"
            "M $b,$($b - $arm) L $b,$($b - $r) A $r,$r 0 0 1 $($b - $r),$b L $($b - $arm),$b"
            "M $($a + $arm),$b L $($a + $r),$b A $r,$r 0 0 1 $a,$($b - $r) L $a,$($b - $arm)"
        )
    }

    # Half-plane triangle on one side of the 45-degree cut through the centre.
    # $Side is 1 for the upper-right piece, -1 for the lower-left piece.
    function New-HalfPlane([double]$Gap, [int]$Side) {
        $o = ($Gap / 2.0) / [Math]::Sqrt(2.0)
        $cx = 512.0 + ($Side * $o)
        $cy = 512.0 - ($Side * $o)
        $far = 3000.0
        $p1 = New-Object System.Windows.Point(($cx - $far), ($cy - $far))
        $p2 = New-Object System.Windows.Point(($cx + $far), ($cy + $far))
        $p3 = if ($Side -eq 1) {
            New-Object System.Windows.Point(($cx + $far), ($cy - $far))
        } else {
            New-Object System.Windows.Point(($cx - $far), ($cy + $far))
        }

        $fig = New-Object System.Windows.Media.PathFigure
        $fig.StartPoint = $p1
        $fig.IsClosed = $true
        $fig.IsFilled = $true
        $fig.Segments.Add((New-Object System.Windows.Media.LineSegment($p2, $false)))
        $fig.Segments.Add((New-Object System.Windows.Media.LineSegment($p3, $false)))
        $geo = New-Object System.Windows.Media.PathGeometry
        $geo.Figures.Add($fig)
        return $geo
    }

    function New-LinearBrush([string]$From, [string]$To, [double]$X1, [double]$Y1, [double]$X2, [double]$Y2) {
        $brush = New-Object System.Windows.Media.LinearGradientBrush
        $brush.MappingMode = [System.Windows.Media.BrushMappingMode]::Absolute
        $brush.StartPoint = New-Object System.Windows.Point($X1, $Y1)
        $brush.EndPoint = New-Object System.Windows.Point($X2, $Y2)
        $brush.GradientStops.Add((New-Object System.Windows.Media.GradientStop((ConvertTo-Color $From), 0.0)))
        $brush.GradientStops.Add((New-Object System.Windows.Media.GradientStop((ConvertTo-Color $To), 1.0)))
        $brush.Freeze()
        return $brush
    }

    # Builds the whole mark as a frozen DrawingGroup in 1024x1024 space.
    function New-MarkDrawing([string]$Variant) {
        $M = $Mark[$Variant]
        $group = New-Object System.Windows.Media.DrawingGroup

        $frameBrush = New-LinearBrush $Palette.Cyan $Palette.Violet $M.Inset $M.Inset (1024 - $M.Inset) (1024 - $M.Inset)
        $pen = New-Object System.Windows.Media.Pen($frameBrush, $M.Stroke)
        $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
        $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
        $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
        $pen.Freeze()

        foreach ($path in (Get-BracketPaths $M)) {
            $geo = [System.Windows.Media.Geometry]::Parse($path)
            $geo.Freeze()
            $drawing = New-Object System.Windows.Media.GeometryDrawing($null, $pen, $geo)
            $drawing.Freeze()
            $group.Children.Add($drawing)
        }

        $core = New-Object System.Windows.Media.RectangleGeometry(
            (New-Object System.Windows.Rect($M.Core, $M.Core, ($M.CoreTo - $M.Core), ($M.CoreTo - $M.Core))),
            $M.CoreRadius, $M.CoreRadius)

        # Luminous half: the HDR range that survives tone mapping.
        $hdr = New-Object System.Windows.Media.CombinedGeometry(
            [System.Windows.Media.GeometryCombineMode]::Intersect, $core, (New-HalfPlane $M.Gap 1))
        $hdr.Freeze()
        $hdrBrush = New-LinearBrush $Palette.Cyan $Palette.Violet $M.Core $M.Core $M.CoreTo $M.CoreTo
        $hdrDrawing = New-Object System.Windows.Media.GeometryDrawing($hdrBrush, $null, $hdr)
        $hdrDrawing.Freeze()

        # Muted half: what an SDR capture clips away.
        $sdr = New-Object System.Windows.Media.CombinedGeometry(
            [System.Windows.Media.GeometryCombineMode]::Intersect, $core, (New-HalfPlane $M.Gap -1))
        $sdr.Freeze()
        $sdrBrush = New-LinearBrush $Palette.Muted $Palette.Muted2 $M.Core $M.CoreTo $M.CoreTo $M.Core
        $sdrDrawing = New-Object System.Windows.Media.GeometryDrawing($sdrBrush, $null, $sdr)
        $sdrDrawing.Freeze()

        $group.Children.Add($sdrDrawing)
        $group.Children.Add($hdrDrawing)
        $group.Freeze()
        return $group
    }

    $DrawingFull = New-MarkDrawing 'full'
    $DrawingCompact = New-MarkDrawing 'compact'

    # -------------------------------------------------------------- rendering
    # Renders the mark centred on a $Width x $Height transparent canvas.
    # $Cover is the fraction of the shorter edge the mark occupies.
    function New-MarkBitmap([int]$Width, [int]$Height, [double]$Cover = 1.0) {
        $drawing = if ([Math]::Min($Width, $Height) -lt 48) { $DrawingCompact } else { $DrawingFull }
        $size = [Math]::Min($Width, $Height) * $Cover
        $scale = $size / 1024.0

        $visual = New-Object System.Windows.Media.DrawingVisual
        $dc = $visual.RenderOpen()
        $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform(
            (($Width - $size) / 2.0), (($Height - $size) / 2.0))))
        $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
        $dc.DrawDrawing($drawing)
        $dc.Pop()
        $dc.Pop()
        $dc.Close()

        $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
            $Width, $Height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
        $rtb.Render($visual)
        $rtb.Freeze()
        return $rtb
    }

    function Get-PngBytes($Bitmap) {
        $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
        $stream = New-Object System.IO.MemoryStream
        $encoder.Save($stream)
        # Comma operator: a bare return would unroll the array into the pipeline
        return ,$stream.ToArray()
    }

    function Save-Png($Bitmap, [string]$Path) {
        $dir = Split-Path $Path -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        [System.IO.File]::WriteAllBytes($Path, (Get-PngBytes $Bitmap))
        Write-Host "    $([System.IO.Path]::GetRelativePath($Root, $Path))" -ForegroundColor DarkGray
    }

    # 32bpp bottom-up DIB with an empty AND mask - the format every Windows
    # shell surface reads. PNG-compressed entries are used for 128px and up.
    function Get-DibBytes($Bitmap) {
        $w = $Bitmap.PixelWidth
        $h = $Bitmap.PixelHeight
        $stride = $w * 4
        $pixels = New-Object byte[] ($stride * $h)
        $Bitmap.CopyPixels($pixels, $stride, 0)

        $maskStride = [Math]::Floor(($w + 31) / 32) * 4
        $out = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($out)
        $bw.Write([int]40)            # biSize
        $bw.Write([int]$w)            # biWidth
        $bw.Write([int]($h * 2))      # biHeight - colour + mask
        $bw.Write([int16]1)           # biPlanes
        $bw.Write([int16]32)          # biBitCount
        $bw.Write([int]0)             # biCompression BI_RGB
        $bw.Write([int]($stride * $h + $maskStride * $h))
        $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $stride, $stride) }
        $bw.Write((New-Object byte[] ($maskStride * $h)))
        $bw.Flush()
        return ,$out.ToArray()
    }

    function Save-Ico([int[]]$Sizes, [string]$Path) {
        $entries = foreach ($size in $Sizes) {
            $bmp = New-MarkBitmap $size $size 1.0
            [pscustomobject]@{
                Size  = $size
                Bytes = if ($size -ge 128) { Get-PngBytes $bmp } else { Get-DibBytes $bmp }
            }
        }

        $dir = Split-Path $Path -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

        $out = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($out)
        $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$entries.Count)
        $offset = 6 + (16 * $entries.Count)
        foreach ($e in $entries) {
            $bw.Write([byte]($(if ($e.Size -ge 256) { 0 } else { $e.Size })))
            $bw.Write([byte]($(if ($e.Size -ge 256) { 0 } else { $e.Size })))
            $bw.Write([byte]0)          # palette count
            $bw.Write([byte]0)          # reserved
            $bw.Write([int16]1)         # planes
            $bw.Write([int16]32)        # bit count
            $bw.Write([int]$e.Bytes.Length)
            $bw.Write([int]$offset)
            $offset += $e.Bytes.Length
        }
        foreach ($e in $entries) { $bw.Write([byte[]]$e.Bytes) }
        $bw.Flush()
        [System.IO.File]::WriteAllBytes($Path, $out.ToArray())
        Write-Host "    $([System.IO.Path]::GetRelativePath($Root, $Path))" -ForegroundColor DarkGray
    }

    # ------------------------------------------------------------------- SVG
    # Same numbers as the WPF geometry, emitted for README and web use.
    function Save-Svg([string]$Path) {
        $M = $Mark.full
        $a = $M.Inset; $b = 1024.0 - $M.Inset
        $o = ($M.Gap / 2.0) / [Math]::Sqrt(2.0)
        $brackets = (Get-BracketPaths $M | ForEach-Object {
            "  <path d=`"$_`" />"
        }) -join "`n"

        $hdrClip = "M $(512 + $o),$(512 - $o) L $(512 + $o + 1200),$(512 - $o + 1200) L $(512 + $o + 1200),$(512 - $o - 1200) Z"
        $sdrClip = "M $(512 - $o),$(512 + $o) L $(512 - $o + 1200),$(512 + $o + 1200) L $(512 - $o - 1200),$(512 + $o + 1200) Z"
        $c = $M.Core; $cs = $M.CoreTo - $M.Core; $cr = $M.CoreRadius

        $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" width="1024" height="1024" role="img" aria-label="HDRSnip">
  <defs>
    <linearGradient id="ramp" gradientUnits="userSpaceOnUse" x1="$a" y1="$a" x2="$b" y2="$b">
      <stop offset="0" stop-color="$($Palette.Cyan.Substring(3))" />
      <stop offset="1" stop-color="$($Palette.Violet.Substring(3))" />
    </linearGradient>
    <linearGradient id="core" gradientUnits="userSpaceOnUse" x1="$c" y1="$c" x2="$($M.CoreTo)" y2="$($M.CoreTo)">
      <stop offset="0" stop-color="$($Palette.Cyan.Substring(3))" />
      <stop offset="1" stop-color="$($Palette.Violet.Substring(3))" />
    </linearGradient>
    <linearGradient id="clipped" gradientUnits="userSpaceOnUse" x1="$c" y1="$($M.CoreTo)" x2="$($M.CoreTo)" y2="$c">
      <stop offset="0" stop-color="$($Palette.Muted.Substring(3))" />
      <stop offset="1" stop-color="$($Palette.Muted2.Substring(3))" />
    </linearGradient>
    <clipPath id="hdrHalf"><path d="$hdrClip" /></clipPath>
    <clipPath id="sdrHalf"><path d="$sdrClip" /></clipPath>
  </defs>
  <g fill="none" stroke="url(#ramp)" stroke-width="$($M.Stroke)" stroke-linecap="round" stroke-linejoin="round">
$brackets
  </g>
  <rect x="$c" y="$c" width="$cs" height="$cs" rx="$cr" ry="$cr" fill="url(#clipped)" clip-path="url(#sdrHalf)" />
  <rect x="$c" y="$c" width="$cs" height="$cs" rx="$cr" ry="$cr" fill="url(#core)" clip-path="url(#hdrHalf)" />
</svg>
"@
        $dir = Split-Path $Path -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        [System.IO.File]::WriteAllText($Path, $svg, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "    $([System.IO.Path]::GetRelativePath($Root, $Path))" -ForegroundColor DarkGray
    }

    # ------------------------------------------------------- Store listing art
    # Partner Center's poster, box and hero slots reject transparency, so these
    # are composited on the app's own dark surface rather than left cut out.

    function New-ListingBackground($dc, [double]$W, [double]$H) {
        $bg = New-Object System.Windows.Media.LinearGradientBrush
        $bg.StartPoint = New-Object System.Windows.Point(0, 0)
        $bg.EndPoint = New-Object System.Windows.Point(1, 1)
        $bg.GradientStops.Add((New-Object System.Windows.Media.GradientStop((ConvertTo-Color '#FF141418'), 0.0)))
        $bg.GradientStops.Add((New-Object System.Windows.Media.GradientStop((ConvertTo-Color '#FF1F1F28'), 1.0)))
        $dc.DrawRectangle($bg, $null, (New-Object System.Windows.Rect(0, 0, $W, $H)))

        # A soft accent bloom keeps a flat panel from looking dead.
        $glow = New-Object System.Windows.Media.RadialGradientBrush
        $glow.GradientOrigin = New-Object System.Windows.Point(0.82, 0.16)
        $glow.Center = New-Object System.Windows.Point(0.82, 0.16)
        $glow.RadiusX = 0.75
        $glow.RadiusY = 0.65
        $glow.GradientStops.Add((New-Object System.Windows.Media.GradientStop((ConvertTo-Color '#2E4CC2FF'), 0.0)))
        $glow.GradientStops.Add((New-Object System.Windows.Media.GradientStop((ConvertTo-Color '#00000000'), 1.0)))
        $dc.DrawRectangle($glow, $null, (New-Object System.Windows.Rect(0, 0, $W, $H)))
    }

    function New-Text([string]$Text, [double]$Size, [string]$Weight, [string]$Hex) {
        $weightValue = [System.Windows.FontWeights]::$Weight
        $typeface = New-Object System.Windows.Media.Typeface(
            (New-Object System.Windows.Media.FontFamily('Segoe UI Variable Display, Segoe UI')),
            [System.Windows.FontStyles]::Normal, $weightValue, [System.Windows.FontStretches]::Normal)
        $brush = New-Object System.Windows.Media.SolidColorBrush((ConvertTo-Color $Hex))
        return New-Object System.Windows.Media.FormattedText(
            $Text, [System.Globalization.CultureInfo]::GetCultureInfo('en-US'),
            [System.Windows.FlowDirection]::LeftToRight, $typeface, $Size, $brush, 1.0)
    }

    # $Title is omitted for hero art, which Partner Center requires to carry no product name.
    function Save-ListingArt([string]$Path, [int]$W, [int]$H, [double]$MarkCover, [double]$MarkCentreY,
                             [string]$Title, [string]$Tagline) {
        $visual = New-Object System.Windows.Media.DrawingVisual
        $dc = $visual.RenderOpen()
        New-ListingBackground $dc $W $H

        $size = [Math]::Min($W, $H) * $MarkCover
        $scale = $size / 1024.0
        $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform(
            (($W - $size) / 2.0), ($H * $MarkCentreY - $size / 2.0))))
        $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
        $dc.DrawDrawing($DrawingFull)
        $dc.Pop(); $dc.Pop()

        $cursor = $H * $MarkCentreY + $size / 2.0 + $H * 0.06
        if ($Title) {
            $text = New-Text $Title ($H * 0.062) 'SemiBold' '#FFF4F5F7'
            $dc.DrawText($text, (New-Object System.Windows.Point((($W - $text.Width) / 2.0), $cursor)))
            $cursor += $text.Height + $H * 0.018
        }
        if ($Tagline) {
            $text = New-Text $Tagline ($H * 0.030) 'Normal' '#FFA6A9B4'
            $dc.DrawText($text, (New-Object System.Windows.Point((($W - $text.Width) / 2.0), $cursor)))
        }
        $dc.Close()

        $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
            $W, $H, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
        $rtb.Render($visual)
        $rtb.Freeze()
        Save-Png $rtb $Path
    }

    function Save-ListingTile([string]$Path, [int]$Size) {
        $visual = New-Object System.Windows.Media.DrawingVisual
        $dc = $visual.RenderOpen()
        New-ListingBackground $dc $Size $Size
        $mark = $Size * 0.66
        $scale = $mark / 1024.0
        $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform(
            (($Size - $mark) / 2.0), (($Size - $mark) / 2.0))))
        $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
        $dc.DrawDrawing($(if ($Size -lt 96) { $DrawingCompact } else { $DrawingFull }))
        $dc.Pop(); $dc.Pop()
        $dc.Close()

        $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
            $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
        $rtb.Render($visual)
        $rtb.Freeze()
        Save-Png $rtb $Path
    }

    # ------------------------------------------------------------------ emit
    # ------------------------------------------------------------------ emit
    $assets = Join-Path $Root 'HDRSnip\Assets'
    $images = Join-Path $Root 'packaging\Images'
    $web = Join-Path $Root 'docs\assets'

    Write-Host '==> App assets' -ForegroundColor Cyan
    Save-Png (New-MarkBitmap 1024 1024 1.0) (Join-Path $assets 'logo.png')
    Save-Ico @(16, 20, 24, 32, 40, 48, 64, 128, 256) (Join-Path $assets 'app.ico')

    Write-Host '==> Web / README assets' -ForegroundColor Cyan
    Save-Svg (Join-Path $web 'logo.svg')
    Save-Png (New-MarkBitmap 512 512 1.0) (Join-Path $web 'logo.png')
    Save-Png (New-MarkBitmap 180 180 0.86) (Join-Path $web 'favicon.png')
    Save-Ico @(16, 32, 48) (Join-Path $web 'favicon.ico')

    Write-Host '==> MSIX tiles' -ForegroundColor Cyan
    # Windows insets tile content itself, so the mark covers ~0.7 of the tile.
    $tiles = @(
        @{ Name = 'Square44x44Logo';  W = 44;  H = 44;  Cover = 0.90 }
        @{ Name = 'Square71x71Logo';  W = 71;  H = 71;  Cover = 0.68 }
        @{ Name = 'Square150x150Logo'; W = 150; H = 150; Cover = 0.62 }
        @{ Name = 'Square310x310Logo'; W = 310; H = 310; Cover = 0.56 }
        @{ Name = 'Wide310x150Logo';  W = 310; H = 150; Cover = 0.62 }
        @{ Name = 'StoreLogo';        W = 50;  H = 50;  Cover = 0.90 }
        @{ Name = 'SplashScreen';     W = 620; H = 300; Cover = 0.52 }
    )
    foreach ($tile in $tiles) {
        foreach ($scale in 100, 125, 150, 200, 400) {
            $w = [int][Math]::Round($tile.W * $scale / 100.0)
            $h = [int][Math]::Round($tile.H * $scale / 100.0)
            $suffix = if ($scale -eq 100) { '' } else { ".scale-$scale" }
            Save-Png (New-MarkBitmap $w $h $tile.Cover) (Join-Path $images "$($tile.Name)$suffix.png")
        }
    }

    # Taskbar / Alt-Tab / Start list pull these unplated target sizes.
    foreach ($target in 16, 24, 32, 48, 256) {
        Save-Png (New-MarkBitmap $target $target 1.0) `
            (Join-Path $images "Square44x44Logo.targetsize-$target.png")
        Save-Png (New-MarkBitmap $target $target 1.0) `
            (Join-Path $images "Square44x44Logo.targetsize-$target`_altform-unplated.png")
    }

    # Partner Center asks for a 300x300 listing logo.
    Save-Png (New-MarkBitmap 300 300 0.78) (Join-Path $images 'StoreLogo300.png')

    Write-Host '==> Store listing art' -ForegroundColor Cyan
    $listing = Join-Path $Root 'packaging\Listing'
    $tagline = 'Screenshots that stay sharp when HDR is on'

    Save-ListingArt (Join-Path $listing 'poster-9x16-720x1080.png')    720 1080 0.52 0.36 'HDRSnip' $tagline
    Save-ListingArt (Join-Path $listing 'poster-9x16-1440x2160.png')  1440 2160 0.52 0.36 'HDRSnip' $tagline
    Save-ListingArt (Join-Path $listing 'boxart-1x1-1080.png')        1080 1080 0.44 0.40 'HDRSnip' ''
    Save-ListingArt (Join-Path $listing 'boxart-1x1-2160.png')        2160 2160 0.44 0.40 'HDRSnip' ''
    # Hero art must carry no product title, so it gets the tagline only.
    Save-ListingArt (Join-Path $listing 'superhero-16x9-1920x1080.png') 1920 1080 0.44 0.40 '' $tagline
    Save-ListingArt (Join-Path $listing 'superhero-16x9-3840x2160.png') 3840 2160 0.44 0.40 '' $tagline

    foreach ($tile in 300, 150, 71) {
        Save-ListingTile (Join-Path $listing "tile-$tile.png") $tile
    }
}

# WPF rendering requires STA; pwsh 7 runs MTA by default.
if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -eq 'STA') {
    & $Work $OutputRoot
} else {
    $runspace = [runspacefactory]::CreateRunspace()
    $runspace.ApartmentState = 'STA'
    $runspace.ThreadOptions = 'ReuseThread'
    $runspace.Open()
    $shell = [powershell]::Create()
    $shell.Runspace = $runspace
    [void]$shell.AddScript($Work.ToString()).AddArgument($OutputRoot)
    try {
        $shell.Invoke() | ForEach-Object { $_ }
        foreach ($record in $shell.Streams.Information) { Write-Host $record.MessageData }
        if ($shell.Streams.Error.Count -gt 0) { throw $shell.Streams.Error[0] }
    } finally {
        $shell.Dispose()
        $runspace.Dispose()
    }
}

Write-Host ''
Write-Host 'Brand assets regenerated.' -ForegroundColor Green
