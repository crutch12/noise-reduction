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
    using(var ink=new SolidBrush(ColorTranslator.FromHtml("#0067c0")))
    using(var white=new SolidBrush(Color.White))
    using(var tile=Round(1,1,62,62,14)) using(var capsule=Round(22,9,20,31,10)) {
     g.FillPath(ink,tile); g.FillPath(white,capsule);
    }
    using(var bracket=new GraphicsPath()) using(var sage=new Pen(Color.White,5f)) {
     sage.StartCap=sage.EndCap=LineCap.Round;
     bracket.AddLine(15,28,15,33); bracket.AddArc(15,16,34,34,180,-180); bracket.AddLine(49,33,49,28);
     g.DrawPath(sage,bracket);
    }
    using(var white=new Pen(Color.White,5f)) {
     white.StartCap=white.EndCap=LineCap.Round; g.DrawLine(white,32,50,32,56); g.DrawLine(white,24,56,40,56);
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
