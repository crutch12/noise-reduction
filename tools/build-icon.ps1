$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$assetPath = Join-Path $repoPath 'app/assets'
$previewPath = Join-Path $repoPath '.local/icon'
New-Item -ItemType Directory -Force $assetPath, $previewPath | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;
public static class MicrophoneIconBuilder {
 static GraphicsPath Round(float x,float y,float w,float h,float r) {
  var p=new GraphicsPath(); float d=r*2;
  p.AddArc(x,y,d,d,180,90); p.AddArc(x+w-d,y,d,d,270,90);
  p.AddArc(x+w-d,y+h-d,d,d,0,90); p.AddArc(x,y+h-d,d,d,90,90); p.CloseFigure(); return p;
 }
 public static void Build(string assets,string previews) {
  int[] sizes={16,20,24,32,40,48,64,128,256}; var images=new List<byte[]>();
  foreach(int size in sizes) using(var bitmap=new Bitmap(size,size,PixelFormat.Format32bppArgb)) {
   using(var g=Graphics.FromImage(bitmap)) {
    g.Clear(Color.Transparent); g.SmoothingMode=SmoothingMode.AntiAlias; g.ScaleTransform(size/64f,size/64f);
    using(var ink=new SolidBrush(ColorTranslator.FromHtml("#203a35")))
    using(var white=new SolidBrush(ColorTranslator.FromHtml("#f4f7f3")))
    using(var tile=Round(2,2,60,60,15)) using(var capsule=Round(23,11,18,31,9)) {
     g.FillPath(ink,tile); g.FillPath(white,capsule);
    }
    using(var bracket=new GraphicsPath()) using(var sage=new Pen(ColorTranslator.FromHtml("#a7cabb"),4.5f)) {
     sage.StartCap=sage.EndCap=LineCap.Round;
     bracket.AddLine(16,29,16,34); bracket.AddArc(16,18,32,32,180,-180); bracket.AddLine(48,34,48,29);
     g.DrawPath(sage,bracket);
    }
    using(var white=new Pen(ColorTranslator.FromHtml("#f4f7f3"),4.5f)) {
     white.StartCap=white.EndCap=LineCap.Round; g.DrawLine(white,32,50,32,59); g.DrawLine(white,24,59,40,59);
    }
    if(size>=24) using(var ink=new Pen(ColorTranslator.FromHtml("#203a35"),2.5f)) {
     ink.StartCap=ink.EndCap=LineCap.Round; g.DrawLine(ink,29,21,35,21); g.DrawLine(ink,29,27,35,27);
    }
   }
   using(var stream=new MemoryStream()) { bitmap.Save(stream,ImageFormat.Png); images.Add(stream.ToArray()); }
   bitmap.Save(Path.Combine(previews,size+".png"),ImageFormat.Png);
  }
  using(var writer=new BinaryWriter(File.Create(Path.Combine(assets,"NoiseReduction.ico")))) {
   writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length); uint offset=(uint)(6+16*sizes.Length);
   for(int i=0;i<sizes.Length;i++) { writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write((uint)images[i].Length);writer.Write(offset);offset+=(uint)images[i].Length; }
   foreach(var png in images) writer.Write(png);
  }
 }
}
'@
[MicrophoneIconBuilder]::Build($assetPath, $previewPath)
Write-Output (Join-Path $assetPath 'NoiseReduction.ico')
