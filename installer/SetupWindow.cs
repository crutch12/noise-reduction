using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using NoiseReduction.Desktop;

// Окно установки: подтверждение, шаги с прогрессом и итог, вместо системных MessageBox
sealed class SetupWindow : Form {
 readonly Label title=new Label { AutoSize=true, Font=new Font("Segoe UI Semibold",12f) };
 readonly Label text=new Label { AutoSize=true, MaximumSize=new Size(380,0) };
 readonly CheckBox option=new CheckBox { AutoSize=true, Margin=new Padding(0,14,0,0) };
 readonly ProgressBar progress=new ProgressBar { Width=380, Height=6, Maximum=100, Visible=false };
 readonly Label step=new Label { AutoSize=true, MaximumSize=new Size(380,0), ForeColor=SystemColors.GrayText, Visible=false };
 readonly Button primary=new Button { AutoSize=true, MinimumSize=new Size(96,0), Padding=new Padding(8,2,8,2) };
 readonly Button secondary=new Button { AutoSize=true, MinimumSize=new Size(96,0), Padding=new Padding(8,2,8,2) };
 readonly Action<Action<int,string>> work;
 readonly string doneText;
 bool running;
 internal bool Succeeded { get; private set; }
 // выбор галочки, работа читает его уже после нажатия кнопки
 internal static bool OptionChecked { get; private set; }

 static string T(string source) { return UiText.Get(source); }

 // work получает функцию для отчёта о шаге: процент и текст
 SetupWindow(string heading,string question,string action,bool confirm,string done,string optionText,bool optionDefault,Action<Action<int,string>> work) {
  this.work=work; doneText=done;
  Text="Noise Reduction"; Font=new Font("Segoe UI",9f); AutoScaleMode=AutoScaleMode.Dpi; AutoScaleDimensions=new SizeF(96f,96f);
  FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=false; MinimizeBox=false; StartPosition=FormStartPosition.CenterScreen; AutoSize=true; AutoSizeMode=AutoSizeMode.GrowAndShrink;
  try { Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch(Exception) { }
  if(UiText.RightToLeft) { RightToLeft=RightToLeft.Yes; RightToLeftLayout=true; }
  option.Text=optionText ?? ""; option.Checked=optionDefault; option.Visible=optionText!=null; OptionChecked=optionDefault && optionText!=null;
  title.Text=heading; text.Text=question; primary.Text=action; secondary.Text=T("Отмена");
  var layout=new TableLayoutPanel { AutoSize=true, ColumnCount=1, Padding=new Padding(20,16,20,16) };
  var buttons=new FlowLayoutPanel { AutoSize=true, FlowDirection=FlowDirection.RightToLeft, Dock=DockStyle.Fill, Margin=new Padding(0,16,0,0) };
  buttons.Controls.Add(secondary); buttons.Controls.Add(primary);
  title.Margin=new Padding(0,0,0,8); progress.Margin=new Padding(0,16,0,6);
  layout.Controls.Add(title); layout.Controls.Add(text); layout.Controls.Add(option); layout.Controls.Add(progress); layout.Controls.Add(step); layout.Controls.Add(buttons);
  Controls.Add(layout);
  AcceptButton=primary; CancelButton=secondary;
  primary.Click+=delegate { if(Succeeded || primary.Tag!=null) Close(); else Start(); };
  secondary.Click+=delegate { if(!running) Close(); };
  FormClosing+=(sender,args) => { if(running) args.Cancel=true; };
  if(!confirm) Shown+=delegate { Start(); };
 }

 void Start() {
  OptionChecked=option.Visible && option.Checked; option.Visible=false;
  running=true; primary.Visible=secondary.Visible=false; progress.Visible=step.Visible=true; ControlBox=false;
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
  progress.Value=Math.Max(progress.Value,Math.Min(100,Math.Max(0,percent))); step.Text=message;
 }

 void Finish(Exception failure) {
  running=false; ControlBox=true; Succeeded=failure==null;
  if(Succeeded && doneText==null) { Close(); return; }
  progress.Visible=failure==null; step.Visible=false;
  if(failure==null) { progress.Value=100; title.Text=doneText; text.Text=""; }
  else { title.Text=T("Не получилось"); text.Text=failure.Message; text.ForeColor=Color.FromArgb(196,43,28); }
  primary.Text=T("Закрыть"); primary.Tag="close"; primary.Visible=true;
 }

 // true, если работа выполнена; без подтверждения окно сразу показывает прогресс
 internal static bool Run(string heading,string question,string action,bool confirm,string done,Action<Action<int,string>> work) { return Run(heading,question,action,confirm,done,null,false,work); }
 internal static bool Run(string heading,string question,string action,bool confirm,string done,string optionText,bool optionDefault,Action<Action<int,string>> work) {
  Application.EnableVisualStyles();
  using(var window=new SetupWindow(heading,question,action,confirm,done,optionText,optionDefault,work)) { Application.Run(window); return window.Succeeded; }
 }
}
