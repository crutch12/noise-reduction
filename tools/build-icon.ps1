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
    g.Clear(Color.Transparent); g.SmoothingMode=SmoothingMode.AntiAlias; g.PixelOffsetMode=PixelOffsetMode.HighQuality; g.ScaleTransform(size/64f,size/64f);
    bool small=size<=24; float stroke=small?6.5f:5f;
    using(var tile=Round(1,1,62,62,small?12:14))
    using(var fill=new LinearGradientBrush(new PointF(0,0),new PointF(64,64),ColorTranslator.FromHtml("#3C9BF0"),ColorTranslator.FromHtml("#0B4FB3"))) {
     g.FillPath(fill,tile);
     if(!small) using(var shine=new LinearGradientBrush(new PointF(0,0),new PointF(0,64),Color.FromArgb(60,255,255,255),Color.FromArgb(0,255,255,255))) { shine.Blend=new Blend { Factors=new[]{0f,1f,1f}, Positions=new[]{0f,0.5f,1f} }; g.FillPath(shine,tile); }
     if(size>=32) using(var edge=new Pen(Color.FromArgb(48,0,20,60),1.2f)) g.DrawPath(edge,tile);
    }
    using(var white=new SolidBrush(Color.White)) using(var capsule=small?Round(22,6,20,30,10):Round(23,9,18,30,9)) g.FillPath(white,capsule);
    using(var bracket=new GraphicsPath()) using(var pen=new Pen(Color.White,stroke)) {
     pen.StartCap=pen.EndCap=LineCap.Round;
     if(small) { bracket.AddLine(12,26,12,31); bracket.AddArc(12,12,40,38,180,-180); bracket.AddLine(52,31,52,26); }
     else { bracket.AddLine(15,27,15,32); bracket.AddArc(15,15,34,34,180,-180); bracket.AddLine(49,32,49,27); }
     g.DrawPath(pen,bracket);
     if(small) g.DrawLine(pen,32,50,32,56); else { g.DrawLine(pen,32,50,32,55); g.DrawLine(pen,24,55.5f,40,55.5f); }
    }
   }
   using(var stream=new MemoryStream()) { bitmap.Save(stream,ImageFormat.Png); images.Add(stream.ToArray()); }
   bitmap.Save(Path.Combine(previews,size+".png"),ImageFormat.Png);
   if(size==64) bitmap.Save(Path.Combine(assets,"Microphone.png"),ImageFormat.Png);
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
