#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw 'STA icon renderer failed.' }
    exit 0
}
Add-Type -AssemblyName WindowsBase, PresentationCore, PresentationFramework
$kairosRoot = Split-Path -Parent $PSScriptRoot
$kairosIconPath = Join-Path $kairosRoot 'src/KairosCapture/Icons.xaml'
$kairosGlassPath = Join-Path $kairosRoot 'src/KairosCapture/Glass.xaml'
$kairosIcoPath = Join-Path $kairosRoot 'src/KairosCapture/KairosCapture.ico'
$kairosPreviewPath = Join-Path $kairosRoot 'test-results/icons-preview.png'
$kairosIcons = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText($kairosIconPath))
$kairosGlass = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText($kairosGlassPath))
$kairosSizes = @(16,20,24,32,40,48,64,256)
$kairosNames = @('Capture','Fullscreen','Timer','Ocr','Select','Rectangle','Ellipse','Arrow','Pen','Text','Highlight','Redact','Undo','Redo','Delete','Copy','Save','Close','Settings','Folder','History','Exit','Account','Check','Keyboard','Pin','Download')

function Brush([string]$Color) { return [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($Color) }
function Rect([double]$X,[double]$Y,[double]$Width,[double]$Height) { return [Windows.Rect]::new($X,$Y,$Width,$Height) }
function Text($Context,[string]$Value,[double]$X,[double]$Y,[double]$Size=13,[string]$Color='#F4F2FA') {
    $formatted = [Windows.Media.FormattedText]::new($Value,[Globalization.CultureInfo]::InvariantCulture,[Windows.FlowDirection]::LeftToRight,[Windows.Media.Typeface]::new('Segoe UI'),$Size,(Brush $Color),1.0)
    $Context.DrawText($formatted,[Windows.Point]::new($X,$Y))
}
function Render-Visual($Visual,[int]$Width,[int]$Height) {
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Width,$Height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($Visual)
    $bitmap.Freeze()
    return $bitmap
}
function Render-Element($Element,[int]$Width,[int]$Height) {
    $Element.Measure([Windows.Size]::new($Width,$Height))
    $Element.Arrange((Rect 0 0 $Width $Height))
    $Element.UpdateLayout()
    return Render-Visual $Element $Width $Height
}
function Render-AppIcon([int]$Size) {
    $key = if ($Size -le 32) { 'AppIconSmall' } else { 'AppIcon' }
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.DrawImage($kairosIcons[$key],(Rect 0 0 $Size $Size))
    $context.Close()
    return Render-Visual $visual $Size $Size
}
function Render-Glyph([string]$Name,[int]$Size) {
    $path = [Windows.Shapes.Path]::new()
    $path.Style = $kairosGlass['GlassIcon']
    $path.Data = $kairosIcons["Glyph.$Name"]
    $path.Width = $path.Height = $Size
    [Windows.Documents.TextElement]::SetForeground($path,(Brush '#F4F2FA'))
    return Render-Element $path $Size $Size
}
function Png-Bytes($Bitmap) {
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
        $encoder.Save($stream)
        return ,$stream.ToArray()
    } finally { $stream.Dispose() }
}
function Ico-Payload($Bitmap,[int]$Size) {
    if ($Size -eq 256) { return [pscustomobject]@{ Size=$Size; Format='PNG'; Bytes=(Png-Bytes $Bitmap) } }
    # DIB pixels are straight BGRA, bottom-up; the 1-bit AND mask follows the pixels.
    $converted = [Windows.Media.Imaging.FormatConvertedBitmap]::new($Bitmap,[Windows.Media.PixelFormats]::Bgra32,$null,0)
    $stride = $Size * 4
    $pixels = [byte[]]::new($stride * $Size)
    $converted.CopyPixels($pixels,$stride,0)
    $maskStride = [int]([Math]::Ceiling($Size / 32.0) * 4)
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint32]40); $writer.Write([int]$Size); $writer.Write([int]($Size*2))
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]0)
        $writer.Write([uint32](($stride+$maskStride)*$Size))
        1..4 | ForEach-Object { $writer.Write([uint32]0) }
        for ($y=$Size-1; $y -ge 0; $y--) { $writer.Write($pixels,($y*$stride),$stride) }
        for ($y=$Size-1; $y -ge 0; $y--) {
            $mask = [byte[]]::new($maskStride)
            for ($x=0; $x -lt $Size; $x++) {
                if ($pixels[$y*$stride+$x*4+3] -eq 0) {
                    $index = [int][Math]::Floor($x/8.0)
                    $mask[$index] = $mask[$index] -bor (128 -shr ($x%8))
                }
            }
            $writer.Write($mask)
        }
        return [pscustomobject]@{ Size=$Size; Format='DIB'; Bytes=$stream.ToArray() }
    } finally { $writer.Dispose(); $stream.Dispose() }
}

# Validate the public contract and real WPF bounds before writing any output.
foreach ($name in $kairosNames) {
    $geometry = $kairosIcons["Glyph.$name"]
    if ($null -eq $geometry -or $geometry.Bounds -ne (Rect 0 0 24 24)) { throw "Invalid viewport: Glyph.$name" }
    $bounds = $geometry.GetRenderBounds([Windows.Media.Pen]::new([Windows.Media.Brushes]::White,1.75))
    if ($bounds.Left -lt 0 -or $bounds.Top -lt 0 -or $bounds.Right -gt 24 -or $bounds.Bottom -gt 24) { throw "Stroke outside viewport: Glyph.$name" }
}
if (@($kairosIcons.Keys | Where-Object { $_ -like 'Glyph.*' }).Count -ne 27) { throw 'Expected exactly 27 Glyph.* keys.' }
foreach ($key in @('AppIcon','AppIconSmall')) {
    if ($kairosIcons[$key].Drawing.Bounds -ne (Rect 0 0 256 256)) { throw "Invalid drawing box: $key" }
}
foreach ($key in @('GlassFill','GlassStroke','GlassHighlight','GlassPanel','GlassIcon','GlassIconButton','GlassToolToggle','GlassSwatch')) {
    if (-not $kairosGlass.Contains($key)) { throw "Missing contract key: $key" }
}
$kairosProbe = [Windows.Shapes.Path]::new()
$kairosProbe.Style = $kairosGlass['GlassIcon']
if ($null -ne $kairosProbe.Fill -or $kairosProbe.StrokeThickness -ne 1.75) { throw 'GlassIcon must be stroke-only at 1.75 DIP.' }
Write-Host 'Contracts: 27 glyphs, 8 glass keys, 24x24 bounds, 256x256 drawing boxes, stroke-only 1.75 DIP: PASS'

$kairosBitmaps = @{}
$kairosPayloads = foreach ($size in $kairosSizes) {
    $kairosBitmaps[$size] = Render-AppIcon $size
    Ico-Payload $kairosBitmaps[$size] $size
}
$kairosStream = [IO.File]::Create($kairosIcoPath)
$kairosWriter = [IO.BinaryWriter]::new($kairosStream)
try {
    $kairosWriter.Write([uint16]0); $kairosWriter.Write([uint16]1); $kairosWriter.Write([uint16]8)
    $kairosOffset = 6 + 16*8
    foreach ($payload in $kairosPayloads) {
        $dimension = if ($payload.Size -eq 256) { 0 } else { $payload.Size }
        $kairosWriter.Write([byte]$dimension); $kairosWriter.Write([byte]$dimension)
        $kairosWriter.Write([byte]0); $kairosWriter.Write([byte]0)
        $kairosWriter.Write([uint16]1); $kairosWriter.Write([uint16]32)
        $kairosWriter.Write([uint32]$payload.Bytes.Length); $kairosWriter.Write([uint32]$kairosOffset)
        $kairosOffset += $payload.Bytes.Length
    }
    foreach ($payload in $kairosPayloads) { $kairosWriter.Write([byte[]]$payload.Bytes) }
} finally { $kairosWriter.Dispose(); $kairosStream.Dispose() }

# Read the saved directory, signature and every frame, independently of the writer.
$kairosReader = [IO.BinaryReader]::new([IO.File]::OpenRead($kairosIcoPath))
try {
    if ($kairosReader.ReadUInt16() -ne 0 -or $kairosReader.ReadUInt16() -ne 1 -or $kairosReader.ReadUInt16() -ne 8) { throw 'Invalid ICO header.' }
    $end = 6 + 16*8
    foreach ($size in $kairosSizes) {
        $width = [int]$kairosReader.ReadByte(); $height = [int]$kairosReader.ReadByte()
        if ($width -eq 0) { $width=256 }; if ($height -eq 0) { $height=256 }
        $colors = $kairosReader.ReadByte(); $reserved = $kairosReader.ReadByte()
        $planes = $kairosReader.ReadUInt16(); $bits = $kairosReader.ReadUInt16()
        $length = $kairosReader.ReadUInt32(); $offset = $kairosReader.ReadUInt32()
        if ($width -ne $size -or $height -ne $size -or $colors -ne 0 -or $reserved -ne 0 -or $planes -ne 1 -or $bits -ne 32 -or $offset -ne $end -or $length -le 0) { throw "Invalid ICO directory entry: $size" }
        $end = $offset+$length
        $format = if ($size -eq 256) { 'PNG' } else { 'DIB' }
        $source = if ($size -le 32) { 'AppIconSmall' } else { 'AppIcon' }
        Write-Host ("ICO {0,3}x{0,-3} 32-bit {1}  {2,6} bytes  {3}" -f $size,$format,$length,$source)
    }
    if ($end -ne $kairosReader.BaseStream.Length) { throw 'ICO payload length mismatch.' }
    $null = $kairosReader.BaseStream.Seek($offset,[IO.SeekOrigin]::Begin)
    if ([BitConverter]::ToString($kairosReader.ReadBytes(8)) -ne '89-50-4E-47-0D-0A-1A-0A') { throw '256px frame must be PNG compressed.' }
} finally { $kairosReader.Dispose() }
$kairosDecoder = [Windows.Media.Imaging.IconBitmapDecoder]::new([Uri]$kairosIcoPath,[Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,[Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
$kairosDecodedSizes = @($kairosDecoder.Frames | ForEach-Object { if ($_.PixelWidth -ne $_.PixelHeight) { throw 'Non-square ICO frame.' }; $_.PixelWidth } | Sort-Object)
if (($kairosDecodedSizes -join ',') -ne ($kairosSizes -join ',')) { throw 'WPF could not decode all eight ICO frames.' }
Write-Host 'ICO directory, PNG signature and WPF decode of all 8 sizes: PASS'

function Screenshot-Backdrop([bool]$Light) {
    $visual = [Windows.Media.DrawingVisual]::new(); $dc = $visual.RenderOpen()
    $base = if ($Light) { '#ECF0F6' } else { '#101824' }
    $panel = if ($Light) { '#FFFFFF' } else { '#253045' }
    $muted = if ($Light) { '#C7D2E1' } else { '#3A4860' }
    $dc.DrawRectangle((Brush $base),$null,(Rect 0 0 592 452))
    $dc.DrawRectangle((Brush $panel),$null,(Rect 0 0 110 452))
    Text $dc 'Capture studio' 135 18 20 $(if ($Light) { '#302B48' } else { '#DBD3EB' })
    foreach ($y in @(60,102,144,186,228,270,312,354)) { $dc.DrawRoundedRectangle((Brush $muted),$null,(Rect 18 $y 70 12),4,4) }
    foreach ($x in @(132,354)) {
        $dc.DrawRoundedRectangle((Brush $panel),$null,(Rect $x 66 208 157),12,12)
        $dc.DrawRoundedRectangle((Brush '#9778BF'),$null,(Rect ($x+14) 80 180 84),8,8)
        $dc.DrawRoundedRectangle((Brush $muted),$null,(Rect ($x+14) 184 126 12),4,4)
    }
    $dc.DrawRoundedRectangle((Brush $panel),$null,(Rect 132 244 430 164),12,12)
    foreach ($y in @(266,298,330,362)) { $dc.DrawRoundedRectangle((Brush $muted),$null,(Rect 150 $y 300 12),4,4) }
    $dc.Close()
    $image = [Windows.Controls.Image]::new(); $image.Source = Render-Visual $visual 592 452
    $image.Effect = [Windows.Media.Effects.BlurEffect]::new(); $image.Effect.Radius=10
    return Render-Element $image 592 452
}
function Style-Sample([bool]$Light) {
    $markup = @"
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="592" Height="452" TextElement.FontFamily="Segoe UI" TextElement.Foreground="#F4F2FA">
  <Grid.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceDictionary Source="@ICONS@" /><ResourceDictionary Source="@GLASS@" /></ResourceDictionary.MergedDictionaries><Style TargetType="TextBlock"><Setter Property="FontSize" Value="11" /><Setter Property="Foreground" Value="#D3C8E4" /></Style></ResourceDictionary></Grid.Resources>
  <Image x:Name="Backdrop" Stretch="Fill" />
  <Canvas>
    <TextBlock Text="@TITLE@" Canvas.Left="24" Canvas.Top="18" FontSize="18" FontWeight="SemiBold" Foreground="@TITLECOLOR@" />
    <Border Style="{StaticResource GlassPanel}" Canvas.Left="20" Canvas.Top="56" Width="552" Height="370" Padding="18">
      <Canvas>
        <TextBlock Text="GlassPanel · GlassIconButton" FontSize="14" Foreground="#F4F2FA" />
        <Button Style="{StaticResource GlassIconButton}" Canvas.Top="34"><Path Style="{StaticResource GlassIcon}" Data="{StaticResource Glyph.Capture}" /></Button>
        <Button x:Name="PreviewHover" Style="{StaticResource GlassIconButton}" Canvas.Left="82" Canvas.Top="34"><Path Style="{StaticResource GlassIcon}" Data="{StaticResource Glyph.Fullscreen}" /></Button>
        <Button x:Name="PreviewPressed" Style="{StaticResource GlassIconButton}" Canvas.Left="164" Canvas.Top="34"><Path Style="{StaticResource GlassIcon}" Data="{StaticResource Glyph.Copy}" /></Button>
        <Button x:Name="PreviewDisabled" Style="{StaticResource GlassIconButton}" Canvas.Left="246" Canvas.Top="34" IsEnabled="False"><Path Style="{StaticResource GlassIcon}" Data="{StaticResource Glyph.Delete}" /></Button>
        <Border BorderBrush="#B097E9" BorderThickness="2" CornerRadius="16" Width="44" Height="44" Canvas.Left="324" Canvas.Top="30"><Button Style="{StaticResource GlassIconButton}"><Path Style="{StaticResource GlassIcon}" Data="{StaticResource Glyph.Settings}" /></Button></Border>
        <TextBlock Text="Normal" Canvas.Top="78" /><TextBlock Text="Hover*" Canvas.Left="82" Canvas.Top="78" /><TextBlock Text="Pressed*" Canvas.Left="164" Canvas.Top="78" /><TextBlock Text="35 %" Canvas.Left="246" Canvas.Top="78" /><TextBlock Text="Fokus*" Canvas.Left="328" Canvas.Top="78" />
        <TextBlock Text="GlassToolToggle · RadioButton / ToggleButton" Canvas.Top="110" FontSize="14" Foreground="#F4F2FA" />
        <RadioButton GroupName="Tools" Style="{StaticResource GlassToolToggle}" Canvas.Top="142"><Path Style="{StaticResource GlassIcon}" Data="{StaticResource Glyph.Arrow}" /></RadioButton>
        <RadioButton x:Name="PreviewChecked" GroupName="Tools" Style="{StaticResource GlassToolToggle}" Canvas.Left="82" Canvas.Top="142" IsChecked="True"><Path Style="{StaticResource GlassIcon}" Data="{StaticResource Glyph.Pen}" /></RadioButton>
        <RadioButton GroupName="Other" Style="{StaticResource GlassToolToggle}" Canvas.Left="164" Canvas.Top="142" IsChecked="True" IsEnabled="False"><Path Style="{StaticResource GlassIcon}" Data="{StaticResource Glyph.Highlight}" /></RadioButton>
        <ToggleButton Style="{StaticResource GlassToolToggle}" Canvas.Left="246" Canvas.Top="142" IsChecked="True"><Path Style="{StaticResource GlassIcon}" Data="{StaticResource Glyph.Check}" /></ToggleButton>
        <TextBlock Text="Normal" Canvas.Top="186" /><TextBlock Text="Checked" Canvas.Left="82" Canvas.Top="186" /><TextBlock Text="Disabled" Canvas.Left="164" Canvas.Top="186" /><TextBlock Text="Toggle" Canvas.Left="246" Canvas.Top="186" />
        <Button Style="{StaticResource GlassPrimaryButton}" Canvas.Top="226"><StackPanel Orientation="Horizontal"><Path Style="{StaticResource GlassIcon}" Data="{StaticResource Glyph.Capture}" Margin="0,0,8,0" /><TextBlock Text="Bereich aufnehmen" FontWeight="SemiBold" FontSize="13" Foreground="#FFFFFF" VerticalAlignment="Center" /></StackPanel></Button>
        <TextBlock Text="GlassSwatch" Canvas.Left="280" Canvas.Top="214" />
        <RadioButton GroupName="Colors" Style="{StaticResource GlassSwatch}" Background="#D3B576" Canvas.Left="280" Canvas.Top="236" />
        <RadioButton x:Name="PreviewSwatch" GroupName="Colors" Style="{StaticResource GlassSwatch}" Background="#B097E9" IsChecked="True" Canvas.Left="324" Canvas.Top="236" />
        <RadioButton GroupName="DisabledColor" Style="{StaticResource GlassSwatch}" Background="#7D4BE3" IsEnabled="False" Canvas.Left="368" Canvas.Top="236" />
        <Border Style="{StaticResource GlassDivider}" Canvas.Top="280" Width="500" />
        <Path Style="{StaticResource GlassIcon}" Data="{StaticResource Glyph.Keyboard}" Canvas.Top="307" />
        <TextBlock Text="GlassIcon · 20 px" Canvas.Left="32" Canvas.Top="310" />
        <Border Style="{StaticResource GlassChip}" Canvas.Left="280" Canvas.Top="302"><TextBlock Text="GlassChip · Strg + Alt + A" /></Border>
      </Canvas>
    </Border>
  </Canvas>
</Grid>
"@
    $markup = $markup.Replace('@ICONS@',([Uri]$kairosIconPath).AbsoluteUri).Replace('@GLASS@',([Uri]$kairosGlassPath).AbsoluteUri)
    $markup = $markup.Replace('@TITLE@',$(if ($Light) { 'Heller Screenshot · Blur 10' } else { 'Dunkler Screenshot · Blur 10' })).Replace('@TITLECOLOR@',$(if ($Light) { '#241C37' } else { '#F4F2FA' }))
    $root = [Windows.Markup.XamlReader]::Parse($markup)
    $root.FindName('Backdrop').Source = Screenshot-Backdrop $Light
    $root.Measure([Windows.Size]::new(592,452)); $root.Arrange((Rect 0 0 592 452)); $root.UpdateLayout()
    foreach ($state in @(@('PreviewHover','HoverLight',-1),@('PreviewPressed','PressShade',1))) {
        $control = $root.FindName($state[0]); $null=$control.ApplyTemplate()
        $control.Template.FindName($state[1],$control).Opacity=1
        $control.Template.FindName('Surface',$control).RenderTransform=[Windows.Media.TranslateTransform]::new(0,$state[2])
    }
    if ($root.FindName('PreviewDisabled').Opacity -ne 0.35) { throw 'Disabled opacity must be 35%.' }
    if ($root.FindName('PreviewChecked').Foreground.ToString() -ne '#FFD8C8FA') { throw 'RadioButton checked style did not apply.' }
    if ($root.FindName('PreviewSwatch').Template.FindName('Ring',$root.FindName('PreviewSwatch')).Stroke.ToString() -ne '#FFFFFFFF') { throw 'Checked swatch ring must be white.' }
    return Render-Visual $root 592 452
}

$kairosContact = [Windows.Media.DrawingVisual]::new(); $kairosDc=$kairosContact.RenderOpen()
$kairosDc.DrawRectangle((Brush '#090E13'),$null,(Rect 0 0 1280 1792))
Text $kairosDc 'Kairos Capture / Glass & Icons' 32 22 27
Text $kairosDc '27 Originalglyphen · Stroke 1.75 DIP · je 16 / 24 / 32 px · dunkles Glas und Glas auf hellem Grund' 32 62 14 '#BFAEDB'
for ($i=0; $i -lt $kairosNames.Count; $i++) {
    $x=32+($i%5)*244; $y=104+[Math]::Floor($i/5)*132
    Text $kairosDc $kairosNames[$i] ($x+2) $y 14
    foreach ($row in @(0,1)) {
        $ry=$y+28+$row*43
        $background=if ($row -eq 0) { '#111824' } else { '#E9EDF5' }
        $kairosDc.DrawRoundedRectangle((Brush $background),$null,(Rect $x $ry 232 38),8,8)
        $kairosDc.DrawRoundedRectangle($kairosGlass['GlassFill'],[Windows.Media.Pen]::new($kairosGlass['GlassStroke'],1),(Rect ($x+3) ($ry+3) 226 32),7,7)
        Text $kairosDc $(if ($row -eq 0) { 'D' } else { 'H' }) ($x+13) ($ry+10) 10 '#BFAEDB'
        for ($s=0; $s -lt 3; $s++) {
            $size=@(16,24,32)[$s]
            $kairosDc.DrawImage((Render-Glyph $kairosNames[$i] $size),(Rect ($x+55+$s*64+(32-$size)/2) ($ry+(38-$size)/2) $size $size))
        }
    }
}
Text $kairosDc 'App-Icon / native Pixelgrößen' 32 918 21
$kairosPositions=@(48,136,224,312,412,524,652,912)
for ($i=0; $i -lt $kairosSizes.Count; $i++) {
    $size=$kairosSizes[$i]; $x=$kairosPositions[$i]
    $y=if ($size -eq 256) { 956 } else { 1040+(64-$size)/2 }
    $kairosDc.DrawImage($kairosBitmaps[$size],(Rect $x $y $size $size))
    Text $kairosDc ([string]$size) $x $(if ($size -eq 256) { 1220 } else { 1116 }) 13 '#C8B6E3'
}
Text $kairosDc 'AppIconSmall: 16 · 20 · 24 · 32' 48 1168 14 '#D3B576'
Text $kairosDc 'AppIcon: 40 · 48 · 64 · 256  /  256 als PNG, übrige Frames als 32-bit DIB' 48 1194 13 '#BFAEDB'
Text $kairosDc 'Material & Zustände / echte WPF-Templates' 32 1262 21
$kairosDc.DrawImage((Style-Sample $false),(Rect 32 1304 592 452))
$kairosDc.DrawImage((Style-Sample $true),(Rect 656 1304 592 452))
Text $kairosDc '* Hover, Pressed und Fokusring statisch simuliert. Checked und Disabled sind echte Control-Zustände. Hintergrund: synthetische Screenshot-Fixture.' 32 1765 11 '#BFAEDB'
$kairosDc.Close()
$kairosPreview=Render-Visual $kairosContact 1280 1792
$null=[IO.Directory]::CreateDirectory((Split-Path -Parent $kairosPreviewPath))
[IO.File]::WriteAllBytes($kairosPreviewPath,(Png-Bytes $kairosPreview))
Write-Host 'Style samples: Button, RadioButton, ToggleButton, checked, disabled, swatch ring: PASS'
Write-Host "Contact sheet: $kairosPreviewPath (1280x1792)"
Write-Host "Icon: $kairosIcoPath"
