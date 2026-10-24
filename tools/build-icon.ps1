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
    bool small=size<=24; Color top=ColorTranslator.FromHtml("#17C3B2"), bottom=ColorTranslator.FromHtml("#2D46D6");
    using(var tile=Round(1,1,62,62,small?13:15))
    using(var fill=new LinearGradientBrush(new PointF(8,0),new PointF(56,64),top,bottom)) {
     g.FillPath(fill,tile);
     if(size>=32) using(var shine=new LinearGradientBrush(new PointF(0,0),new PointF(0,64),Color.FromArgb(70,255,255,255),Color.FromArgb(0,255,255,255))) { shine.Blend=new Blend { Factors=new[]{0f,1f,1f}, Positions=new[]{0f,0.45f,1f} }; g.FillPath(shine,tile); }
     if(size>=32) using(var edge=new Pen(Color.FromArgb(60,10,20,80),1.2f)) g.DrawPath(edge,tile);
    }
    // тот же знак, что в трее: контур микрофона и волна внутри
    float stroke=small?6.5f:4.6f;
    using(var pen=new Pen(Color.White,stroke){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round}) {
     using(var capsule=Round(21,6,22,33,11)) g.DrawPath(pen,capsule);
     using(var stand=new GraphicsPath()) { stand.AddLine(12,28,12,32); stand.AddArc(12,14,40,37,180,-180); stand.AddLine(52,32,52,28); g.DrawPath(pen,stand); }
     g.DrawLine(pen,32,51,32,57); if(!small) g.DrawLine(pen,23,57.5f,41,57.5f);
    }
    if(size>=20) using(var pen=new Pen(Color.White,small?5f:4.6f){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round}) {
     var points=new PointF[25]; for(int i=0;i<points.Length;i++){ float t=i/24f; points[i]=new PointF(25.5f+13*t,22.5f-(float)Math.Sin(t*Math.PI*2)*(small?5f:6.5f)); }
     g.DrawLines(pen,points);
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
