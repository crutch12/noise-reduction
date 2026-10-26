using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using NoiseReduction.Desktop;

// Окно установки в духе диалогов Windows 11: подтверждение, шаги с прогрессом и итог
sealed class SetupWindow : Form {
 [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
 [DllImport("user32.dll")] static extern IntPtr GetSystemMenu(IntPtr window,bool revert);
 [DllImport("user32.dll")] static extern bool EnableMenuItem(IntPtr menu,uint item,uint flags);

 // тонкая полоса прогресса цвета акцента, как в Windows 11
 sealed class AccentBar : Control {
  int value; internal Color Fill, Track;
  internal int Value { get { return value; } set { this.value=Math.Max(0,Math.Min(100,value)); Invalidate(); } }
  internal AccentBar() { SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint,true); }
  protected override void OnPaint(PaintEventArgs e) {
   e.Graphics.Clear(BackColor);
   int y=(Height-3)/2;
   using(var track=new SolidBrush(Track)) e.Graphics.FillRectangle(track,0,y+1,Width,1);
   using(var fill=new SolidBrush(Fill)) e.Graphics.FillRectangle(fill,0,y,Width*value/100,3);
  }
 }

 readonly bool dark=Dark();
 readonly Label title=new Label { AutoSize=true };
 readonly Label text=new Label { AutoSize=true, MaximumSize=new Size(392,0), Margin=new Padding(0,10,0,0) };
 readonly CheckBox option=new CheckBox { AutoSize=true, Margin=new Padding(0,16,0,0) };
 readonly AccentBar progress=new AccentBar { Width=392, Height=8, Margin=new Padding(0,20,0,0), Visible=false };
 readonly Label step=new Label { AutoSize=true, MaximumSize=new Size(392,0), Margin=new Padding(0,8,0,0), Visible=false };
 readonly Button primary=new Button { AutoSize=true, MinimumSize=new Size(110,32) };
 readonly Button secondary=new Button { AutoSize=true, MinimumSize=new Size(110,32), Margin=new Padding(8,0,0,0) };
 readonly Panel footer=new Panel();
 readonly TableLayoutPanel content=new TableLayoutPanel { AutoSize=true, ColumnCount=1, Padding=new Padding(24,22,24,24), Location=Point.Empty };
 readonly FlowLayoutPanel buttons=new FlowLayoutPanel { AutoSize=true, FlowDirection=FlowDirection.LeftToRight, WrapContents=false };
 readonly Action<Action<int,string>> work;
 readonly string doneText;
 bool running;
 internal bool Succeeded { get; private set; }
 // выбор галочки, работа читает его уже после нажатия кнопки
 internal static bool OptionChecked { get; private set; }

 static string T(string source) { return UiText.Get(source); }
 static bool Dark() { try { using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) { object light=key==null?null:key.GetValue("AppsUseLightTheme"); return light is int && (int)light==0; } } catch(Exception) { return false; } }
 static Color Accent() {
  try { using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM")) { object value=key==null?null:key.GetValue("AccentColor"); if(value is int) { int abgr=(int)value; return Color.FromArgb(abgr&0xFF,(abgr>>8)&0xFF,(abgr>>16)&0xFF); } } } catch(Exception) { }
  return Color.FromArgb(0,103,192);
 }

 // work получает функцию для отчёта о шаге: процент и текст
 SetupWindow(string heading,string question,string action,bool confirm,string done,string optionText,bool optionDefault,Action<Action<int,string>> work) {
  this.work=work; doneText=done;
  Color surface=dark?Color.FromArgb(39,39,39):Color.White, band=dark?Color.FromArgb(32,32,32):Color.FromArgb(243,243,243);
  Color ink=dark?Color.White:Color.FromArgb(26,26,26), muted=dark?Color.FromArgb(200,200,200):Color.FromArgb(96,96,96), accent=Accent();
  Text="Noise Reduction"; Font=new Font("Segoe UI",9f); AutoScaleMode=AutoScaleMode.Dpi; AutoScaleDimensions=new SizeF(96f,96f);
  FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=false; MinimizeBox=false; StartPosition=FormStartPosition.CenterScreen;
  BackColor=surface; ForeColor=ink;
  Icon appIcon=null; try { appIcon=Icon.ExtractAssociatedIcon(Application.ExecutablePath); Icon=appIcon; } catch(Exception) { }
  if(UiText.RightToLeft) { RightToLeft=RightToLeft.Yes; RightToLeftLayout=true; }

  title.Text=heading; title.ForeColor=ink; title.Font=new Font("Segoe UI Semibold",14f); title.Anchor=AnchorStyles.Left; title.Margin=new Padding(10,0,0,0);
  text.Text=question; text.ForeColor=ink;
  step.ForeColor=muted; option.ForeColor=ink;
  option.Text=optionText ?? ""; option.Checked=optionDefault; option.Visible=optionText!=null; OptionChecked=optionDefault && optionText!=null;
  progress.BackColor=surface; progress.Fill=accent; progress.Track=dark?Color.FromArgb(80,80,80):Color.FromArgb(200,200,200);

  var header=new FlowLayoutPanel { AutoSize=true, WrapContents=false, Margin=new Padding(0) };
  var picture=new PictureBox { Size=new Size(32,32), SizeMode=PictureBoxSizeMode.Zoom, Margin=new Padding(0), Anchor=AnchorStyles.Left };
  if(appIcon!=null) try { picture.Image=new Icon(appIcon,64,64).ToBitmap(); } catch(Exception) { }
  header.Controls.Add(picture); header.Controls.Add(title);
  content.Controls.Add(header); content.Controls.Add(text); content.Controls.Add(option); content.Controls.Add(progress); content.Controls.Add(step);

  Style(primary,accent,Color.White,accent); Style(secondary,dark?Color.FromArgb(55,55,55):Color.FromArgb(251,251,251),ink,dark?Color.FromArgb(70,70,70):Color.FromArgb(214,214,214));
  primary.Text=action; secondary.Text=T("Отмена");
  footer.BackColor=band;
  buttons.BackColor=band;
  buttons.Controls.Add(primary); buttons.Controls.Add(secondary);
  footer.Controls.Add(buttons);
  Controls.Add(content); Controls.Add(footer);
  Load+=delegate { Arrange(); };

  AcceptButton=primary; CancelButton=secondary;
  primary.Click+=delegate { if(Succeeded || primary.Tag!=null) Close(); else Start(); };
  secondary.Click+=delegate { if(!running) Close(); };
  FormClosing+=(sender,args) => { if(running) args.Cancel=true; };
  HandleCreated+=delegate { int on=dark?1:0; DwmSetWindowAttribute(Handle,20,ref on,4); };
  if(!confirm) Shown+=delegate { Start(); };
 }

 // размеры считаем сами: полоса с кнопками на всю ширину под содержимым
 void Arrange() {
  content.PerformLayout(); buttons.PerformLayout();
  int width=Math.Max(content.PreferredSize.Width,LogicalToDeviceUnits(460));
  int band=footer.Visible?LogicalToDeviceUnits(68):0;
  content.Width=width;
  footer.Bounds=new Rectangle(0,content.PreferredSize.Height,width,band);
  buttons.Location=new Point(width-buttons.PreferredSize.Width-LogicalToDeviceUnits(24),(band-buttons.PreferredSize.Height)/2);
  ClientSize=new Size(width,content.PreferredSize.Height+band);
 }

 static void Style(Button button,Color back,Color fore,Color border) {
  button.FlatStyle=FlatStyle.Flat; button.BackColor=back; button.ForeColor=fore; button.UseVisualStyleBackColor=false;
  button.FlatAppearance.BorderColor=border; button.FlatAppearance.BorderSize=1;
  button.FlatAppearance.MouseOverBackColor=ControlPaint.Light(back,0.1f); button.FlatAppearance.MouseDownBackColor=ControlPaint.Dark(back,0.05f);
  button.Padding=new Padding(12,0,12,0); button.Cursor=Cursors.Hand;
 }

 // крестик на время работы неактивен, значок и заголовок остаются на месте
 void CloseButton(bool enabled) { if(IsHandleCreated) EnableMenuItem(GetSystemMenu(Handle,false),0xF060,enabled?0u:1u); }

 void Start() {
  OptionChecked=option.Visible && option.Checked; option.Visible=false;
  running=true; footer.Visible=false; progress.Visible=step.Visible=true; CloseButton(false); Arrange();
  Report(0,T("Подготовка"));
  var thread=new Thread(() => {
   Exception failure=null;
   try { work(Report); } catch(Exception error) { failure=error; }
   BeginInvoke((Action)(() => Finish(failure)));
  });
  // работа с микрофонами идёт через COM, ему нужен STA поток
  thread.SetApartmentState(ApartmentState.STA); thread.IsBackground=true; thread.Start();
 }

 void Report(int percent,string message) {
  if(InvokeRequired) { BeginInvoke((Action)(() => Report(percent,message))); return; }
  progress.Value=Math.Max(progress.Value,percent); step.Text=message; Arrange();
 }

 void Finish(Exception failure) {
  running=false; CloseButton(true); Succeeded=failure==null;
  if(Succeeded && doneText==null) { Close(); return; }
  progress.Visible=step.Visible=false;
  if(failure==null) { title.Text=doneText; text.Text=""; }
  else { title.Text=T("Не получилось"); text.Text=failure.Message; text.ForeColor=dark?Color.FromArgb(255,153,164):Color.FromArgb(196,43,28); }
  secondary.Visible=false; primary.Text=T("Закрыть"); primary.Tag="close"; footer.Visible=true; Arrange();
 }

 // true, если работа выполнена; без подтверждения окно сразу показывает прогресс
 internal static bool Run(string heading,string question,string action,bool confirm,string done,Action<Action<int,string>> work) { return Run(heading,question,action,confirm,done,null,false,work); }
 internal static bool Run(string heading,string question,string action,bool confirm,string done,string optionText,bool optionDefault,Action<Action<int,string>> work) {
  Application.EnableVisualStyles();
  using(var window=new SetupWindow(heading,question,action,confirm,done,optionText,optionDefault,work)) { Application.Run(window); return window.Succeeded; }
 }
}
