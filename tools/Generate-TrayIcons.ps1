$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$destination=Join-Path $PSScriptRoot '../systemwide/WindowsSourceSwitcher.Tray/Assets'
[IO.Directory]::CreateDirectory($destination)|Out-Null
foreach($variant in @('White','Black')) {
 $frames=[Collections.Generic.List[byte[]]]::new()
 $sizes=@(16,20,24,32,40,48,64,256)
 foreach($size in $sizes) {
  $bitmap=[Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $graphics=[Drawing.Graphics]::FromImage($bitmap)
  try {
   $graphics.Clear([Drawing.Color]::Transparent)
   $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
   $graphics.ScaleTransform(($size/16.0),($size/16.0))
   $color=if($variant -eq 'White'){[Drawing.Color]::FromArgb(245,245,245)}else{[Drawing.Color]::FromArgb(24,24,24)}
   $track=[Drawing.Pen]::new($color,1.2)
   $knob=[Drawing.Pen]::new($color,2.5)
   try {
    $track.StartCap=$track.EndCap=[Drawing.Drawing2D.LineCap]::Round
    $knob.StartCap=$knob.EndCap=[Drawing.Drawing2D.LineCap]::Round
    foreach($fader in @(@(3.5,5),@(8,10.5),@(12.5,7))) {
     $x=[single]$fader[0];$y=[single]$fader[1]
     $graphics.DrawLine($track,$x,[single]2,$x,[single]14)
     $graphics.DrawLine($knob,($x-0.65),$y,($x+0.65),$y)
    }
   } finally {$track.Dispose();$knob.Dispose()}
   $memory=[IO.MemoryStream]::new()
   try {$bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png);$frames.Add($memory.ToArray())}finally{$memory.Dispose()}
  }finally{$graphics.Dispose();$bitmap.Dispose()}
 }
 $path=Join-Path $destination ('TrayMixer'+$variant+'.ico')
 $stream=[IO.File]::Open($path,[IO.FileMode]::Create)
 $writer=[IO.BinaryWriter]::new($stream)
 try {
  $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
  $offset=6+16*$sizes.Count
  for($index=0;$index -lt $sizes.Count;$index++) {
   $sizeByte=if($sizes[$index] -eq 256){0}else{$sizes[$index]}
   $writer.Write([byte]$sizeByte);$writer.Write([byte]$sizeByte);$writer.Write([byte]0);$writer.Write([byte]0)
   $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frames[$index].Length);$writer.Write([uint32]$offset)
   $offset+=$frames[$index].Length
  }
  foreach($frame in $frames){$writer.Write($frame)}
 }finally{$writer.Dispose();$stream.Dispose()}
}
