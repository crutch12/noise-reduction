using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using NoiseReduction.Platform;

namespace NoiseReduction.Desktop
{
    internal static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        internal static readonly uint ActivateMessage = RegisterWindowMessage("NoiseReduction.ShowSettings.1");
        [STAThread]
        private static int Main(string[] args)
        {
            UiText.Language = Settings.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoiseReduction", "settings.json")).Language;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] != "--tray")
            {
                try
                {
                    switch (args[0])
                    {
                        case "--attach": if (args.Length != 2) throw new ArgumentException("Нужен ID микрофона"); AttachVerified(args[1]); break;
                        case "--detach": if (args.Length != 2) throw new ArgumentException("Нужен ID микрофона"); DetachAndRefresh(args[1]); break;
                        case "--register": Integration.RegisterEngine(AppDomain.CurrentDomain.BaseDirectory); break;
                        case "--unregister": Integration.UnregisterEngine(); break;
                        default: throw new ArgumentException("Неизвестная команда");
                    }
                    return 0;
                }
                catch (Exception error) { MessageBox.Show(UiText.Message(error.Message), "Noise Reduction", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\NoiseReduction.Desktop.1", out created))
            {
                if (!created) { PostMessage(new IntPtr(0xffff), ActivateMessage, IntPtr.Zero, IntPtr.Zero); return 0; }
                try { Application.Run(new MainWindow(args.Length == 1)); }
                catch (Exception error) { MessageBox.Show(UiText.Message(error.Message), "Noise Reduction", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
                finally { mutex.ReleaseMutex(); }
            }
            return 0;
        }
        private static void AttachVerified(string id)
        {
            using (var operation = new Integration.OperationLease())
            {
                var device = Integration.EnumerateMicrophones().Find(value => value.Id == id && value.Active);
                if (device == null) throw new InvalidOperationException("Микрофон недоступен");
                Exception verificationFailure = null;
                bool refreshOnRollback = false;
                try
                {
                    using (var control = new ControlFile(Integration.ControlRoot, device.Guid, true))
                    using (var verification = Integration.BeginVerifiedAttachment(id))
                    {
                        device = Integration.EnumerateMicrophones().Find(value => value.Id == id && value.Active);
                        if (device == null) throw new InvalidOperationException("Микрофон недоступен после восстановления подключения");
                        try
                        {
                            AttachmentTransaction.Run(control, delegate
                            {
                                Integration.Attach(id);
                                if (!device.Attached)
                                {
                                    refreshOnRollback = true;
                                    Integration.RefreshAudioGraph();
                                }
                            }, delegate
                            {
                                long initial = control.ModelFrames;
                                Comparison.VerifyCapture(id, delegate { return control.ModelFrames > initial; });
                            });
                            verification.Commit();
                            refreshOnRollback = false;
                        }
                        catch (Exception error) { verificationFailure = error; throw; }
                    }
                }
                catch (Exception error)
                {
                    if (refreshOnRollback)
                    {
                        try { Integration.RefreshAudioGraph(); }
                        catch (Exception restoreError) { throw new AggregateException("Подключение отменено, не удалось восстановить аудиограф Windows", error, restoreError); }
                    }
                    if (Object.ReferenceEquals(error, verificationFailure))
                        throw new InvalidOperationException(error.Message + (device.Attached ? "\nПрежнее подключение сохранено" : "\nНовое подключение отменено"), error);
                    throw;
                }
            }
        }
        private static void DetachAndRefresh(string id)
        {
            using (var operation = new Integration.OperationLease())
            {
                var device = Integration.EnumerateMicrophones().Find(value => value.Id == id);
                Integration.Detach(id);
                if (device != null && device.Attached) Integration.RefreshAudioGraph();
            }
        }
    }

    internal sealed class MainWindow : Form
    {
        private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoiseReduction", "settings.json");
        private readonly Settings settings;
        private readonly Dictionary<string, ControlFile> controls = new Dictionary<string, ControlFile>();
        private readonly Dictionary<string, Activity> activities = new Dictionary<string, Activity>();
        private readonly DesktopState state = new DesktopState();
        private readonly Theme theme = new Theme();
        private readonly TrayHealth trayHealth = new TrayHealth();
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 8000 };
        private readonly Dictionary<Control, string> originalTexts = new Dictionary<Control, string>();
        private readonly Dictionary<Control, string> originalNames = new Dictionary<Control, string>();
        private readonly ComboBox language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, AccessibleName = "Язык интерфейса" };
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly ListBox devices = new ListBox();
        private readonly CheckBox global = new FluentToggle { Text = "Шумоподавление включено", AutoSize = true };
        private readonly CheckBox enabled = new FluentToggle { Text = "Обрабатывать этот микрофон", AutoSize = true };
        private readonly CheckBox startup = new CheckBox { Text = "Запускать при входе в Windows", AutoSize = true };
        private readonly TrackBar attenuation = new TrackBar { Minimum = 0, Maximum = 60, TickFrequency = 10 };
        private readonly Label strength = new Label { AutoSize = true };
        private readonly Label status = new Label();
        private readonly Label deviceName = new Label();
        private readonly Button attach = new Button { Text = "Подключить" };
        private readonly Button detach = new Button { Text = "Отключить" };
        private readonly Button refresh = new Button { Text = "Обновить" };
        private readonly Button record = new Button { Text = "Записать 5 секунд" };
        private readonly Button dry = new Button { Text = "Слушать оригинал", Enabled = false };
        private readonly Button wet = new Button { Text = "Слушать обработку", Enabled = false };
        private readonly Button advanced = new Button();
        private readonly SurfacePanel advancedPanel = new SurfacePanel();
        private readonly Button reset = new Button();
        private readonly Button copyDiagnostics = new Button();
        private readonly System.Media.SoundPlayer player = new System.Media.SoundPlayer();
        private bool updating;
        private bool exiting;
        private bool busy { get { return !state.CanRefresh; } }
        private string comparisonId;
        private string recordingDirectory;
        private DateTime refreshAt;
        private string defaultMicrophoneId;
        private int trayState = -1;
        private readonly Dictionary<string, CaptureSessionSnapshot> usageSnapshots = new Dictionary<string, CaptureSessionSnapshot>();
        private ToolStripMenuItem trayMaster, trayDevices;
        private int menuGeneration;
        private bool usageQueryPending;
        private DeviceInfo Selected { get { return devices.SelectedItem as DeviceInfo; } }

        internal MainWindow(bool hidden)
        {
            HandleCreated += delegate { WindowAppearance.Apply(Handle); };
            settings = Settings.Load(settingsPath);
            UiText.Language = settings.Language;
            BuildLayout();
            RememberTexts(this);
            global.Checked = settings.Enabled;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) startup.Checked = key != null && key.GetValue("NoiseReduction") != null;
            global.CheckedChanged += delegate { if (!updating) { settings.Enabled = global.Checked; Persist(); } };
            enabled.CheckedChanged += delegate { ChangeProfile(); };
            attenuation.ValueChanged += delegate { ChangeProfile(); };
            devices.SelectedIndexChanged += delegate { ShowProfile(); };
            startup.CheckedChanged += delegate { Try(delegate { using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) { if (startup.Checked) key.SetValue("NoiseReduction", "\"" + Application.ExecutablePath + "\" --tray"); else key.DeleteValue("NoiseReduction", false); } }); };
            attach.Click += delegate { Integrate(true); };
            detach.Click += delegate { Integrate(false); };
            refresh.Click += delegate { RefreshDevices(); };
            record.Click += delegate { RecordComparison(); };
            dry.Click += delegate { Play("original.wav"); };
            wet.Click += delegate { Play("processed.wav"); };
            reset.Click += delegate { ResetProfile(); };
            copyDiagnostics.Click += delegate { Try(delegate { Clipboard.SetText(BuildDiagnostics()); }); };
            language.SelectedIndexChanged += delegate
            {
                if (updating || language.SelectedItem == null) return;
                settings.Language = ((LanguageOption)language.SelectedItem).Id;
                ApplyLanguage();
                Try(delegate { settings.Save(settingsPath); });
            };
            var menu = new ContextMenuStrip();
            menu.ShowItemToolTips = true;
            menu.Items.Add(new ToolStripMenuItem("Настройки", null, delegate { Reveal(); }) { Name = "TraySettings" });
            trayMaster = new ToolStripMenuItem();
            trayMaster.Click += delegate { if (!busy) { settings.Enabled = !settings.Enabled; SaveTrayProfile(); } };
            menu.Items.Add(trayMaster);
            trayDevices = new ToolStripMenuItem(); menu.Items.Add(trayDevices);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Выход", null, delegate { if (!busy) { exiting = true; Close(); } else Error("Дождитесь завершения операции"); }) { Name = "TrayExit" });
            menu.Opening += OpenTrayMenu;
            tray.Icon = theme.TrayIcon(0); tray.Text = "Noise Reduction"; tray.ContextMenuStrip = menu; tray.Visible = true;
            ApplyLanguage();
            tray.DoubleClick += delegate { Reveal(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing && !exiting) { e.Cancel = true; Hide(); return; }
                ApplyControls(true);
                foreach (var control in controls.Values) control.Dispose();
                timer.Stop(); tray.Visible = false; tray.Dispose(); player.Dispose();
                if (recordingDirectory != null) try { Directory.Delete(recordingDirectory, true); } catch (IOException) { }
            };
            FormClosed += delegate { tips.Dispose(); theme.Dispose(); };
            Shown += delegate { RefreshDevices(); if (hidden) Hide(); };
            timer.Interval = 1000;
            timer.Tick += delegate { if (!busy && DateTime.UtcNow >= refreshAt) RefreshDevices(); Poll(); };
            timer.Start();
        }
        private void BuildLayout()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Noise Reduction";
            Font = theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Sidebar;
            Icon = theme.AppIcon;
            Size = new Size(860, 650);
            MinimumSize = new Size(760, 600);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 3 };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            Controls.Add(shell);
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, TabIndex = 0 };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            header.Controls.Add(Label("Noise Reduction", theme.Title, Theme.Ink), 0, 0);
            global.Text = "Обработка всех микрофонов";
            global.AccessibleName = "Шумоподавление для всех микрофонов";
            global.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            global.Margin = new Padding(0, 3, 0, 0);
            global.TabIndex = 0;
            header.Controls.Add(global, 1, 0);
            shell.Controls.Add(header, 0, 0);

            var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, TabIndex = 1 };
            var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            viewport.Controls.Add(stack);
            shell.Controls.Add(viewport, 0, 1);

            var microphone = new SurfacePanel { TabIndex = 0 };
            var microphoneLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            microphoneLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            microphoneLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            microphone.Controls.Add(microphoneLayout);
            var selector = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, TabIndex = 0 };
            selector.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            selector.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var selectorHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, TabIndex = 1 };
            selectorHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            selectorHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            selectorHeading.Controls.Add(Label("Микрофоны", theme.Section, Theme.Ink), 0, 0);
            refresh.Text = "Обновить";
            Theme.Button(refresh, "Обновить список микрофонов", false);
            refresh.MinimumSize = new Size(62, 26); refresh.Padding = new Padding(5, 0, 5, 0); refresh.Margin = Padding.Empty;
            refresh.TabIndex = 1;
            selectorHeading.Controls.Add(refresh, 1, 0);
            selector.Controls.Add(selectorHeading, 0, 0);
            devices.DisplayMember = "Name";
            devices.Format += delegate(object sender, ListControlConvertEventArgs e) { e.Value = ((DeviceInfo)e.ListItem).Name; };
            devices.FormattingEnabled = true;
            devices.Dock = DockStyle.Fill;
            devices.Margin = Padding.Empty;
            devices.BorderStyle = BorderStyle.None;
            devices.BackColor = Color.White;
            devices.DrawMode = DrawMode.OwnerDrawFixed;
            devices.ItemHeight = 58;
            devices.IntegralHeight = false;
            devices.AccessibleName = "Микрофоны";
            devices.TabIndex = 0;
            devices.DrawItem += DrawDevice;
            selector.Controls.Add(devices, 0, 1);
            microphoneLayout.Controls.Add(selector, 0, 0);
            var profile = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(18, 0, 0, 0), TabIndex = 1 };
            profile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            profile.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            profile.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            profile.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            deviceName.Text = "Выберите микрофон";
            deviceName.Font = theme.Device; deviceName.AutoEllipsis = true; deviceName.Dock = DockStyle.Fill; deviceName.Margin = Padding.Empty;
            deviceName.AccessibleName = "Выбранный микрофон";
            profile.Controls.Add(deviceName, 0, 0);
            status.Dock = DockStyle.Fill; status.Font = theme.Small; status.ForeColor = Theme.Secondary; status.Margin = Padding.Empty;
            status.AccessibleName = "Состояние обработки";
            profile.Controls.Add(status, 0, 1);
            enabled.Text = "О&брабатывать этот микрофон"; enabled.AccessibleName = "Обрабатывать выбранный микрофон";
            enabled.Dock = DockStyle.Fill; enabled.Margin = Padding.Empty; enabled.TabIndex = 0;
            profile.Controls.Add(enabled, 0, 2);
            microphoneLayout.Controls.Add(profile, 1, 0);
            AddSection(stack, microphone, 144);

            var strengthSurface = new SurfacePanel { TabIndex = 1 };
            var level = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
            level.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); level.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            level.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); level.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); level.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            level.Controls.Add(Label("Сила подавления", theme.Section, Theme.Ink), 0, 0);
            strength.Font = theme.Section; strength.TextAlign = ContentAlignment.TopRight; strength.Dock = DockStyle.Fill; strength.Margin = Padding.Empty;
            level.Controls.Add(strength, 1, 0);
            attenuation.TickStyle = TickStyle.None; attenuation.AutoSize = false; attenuation.Dock = DockStyle.Fill; attenuation.Margin = Padding.Empty;
            attenuation.AccessibleName = "Сила подавления в децибелах"; attenuation.TabIndex = 0;
            level.Controls.Add(attenuation, 0, 1); level.SetColumnSpan(attenuation, 2);
            level.Controls.Add(Label("Мягче", theme.Small, Theme.Secondary), 0, 2);
            var stronger = Label("Сильнее", theme.Small, Theme.Secondary); stronger.Dock = DockStyle.Fill; stronger.TextAlign = ContentAlignment.TopRight;
            level.Controls.Add(stronger, 1, 2);
            strengthSurface.Controls.Add(level);
            AddSection(stack, strengthSurface, 106);

            var comparisonSurface = new SurfacePanel { TabIndex = 2 };
            var comparison = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            comparison.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            comparison.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); comparison.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); comparison.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            comparison.Controls.Add(Label("Сравните на своём голосе", theme.Section, Theme.Ink), 0, 0);
            comparison.Controls.Add(Label("Одна запись до и после локальной обработки RNNoise", theme.Small, Theme.Secondary), 0, 1);
            var playback = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            record.Text = "&Записать 5 сек"; dry.Text = "Д&о"; wet.Text = "&После";
            Theme.Button(record, "Записать пять секунд для сравнения", true); Theme.Button(dry, "Слушать оригинальную запись", false); Theme.Button(wet, "Слушать обработанную запись", false);
            var stop = new Button { Text = "Стоп" }; Theme.Button(stop, "Остановить воспроизведение", false);
            record.TabIndex = 0; dry.TabIndex = 1; wet.TabIndex = 2; stop.TabIndex = 3;
            playback.Controls.Add(record); playback.Controls.Add(dry); playback.Controls.Add(wet); playback.Controls.Add(stop);
            stop.Click += delegate { player.Stop(); };
            comparison.Controls.Add(playback, 0, 2); comparisonSurface.Controls.Add(comparison);
            AddSection(stack, comparisonSurface, 128);

            advanced.Text = "Дополнительно"; Theme.Button(advanced, "Дополнительные настройки", false); advanced.Dock = DockStyle.Left;
            advanced.TabIndex = 3;
            advanced.Click += delegate { advancedPanel.Visible = !advancedPanel.Visible; advanced.AccessibleDescription = UiText.Get(advancedPanel.Visible ? "Свернуть дополнительные настройки" : "Развернуть дополнительные настройки"); };
            AddSection(stack, advanced, 44);
            advancedPanel.Visible = false;
            var advancedLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            advancedLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); advancedLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); advancedLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); advancedLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            Theme.Button(attach, "Подключить системное шумоподавление", false); Theme.Button(detach, "Отключить системное шумоподавление", false);
            reset.Text = "Сбросить силу до 24 дБ"; Theme.Button(reset, "Сбросить силу до 24 дБ", false);
            attach.TabIndex = 0; detach.TabIndex = 1; reset.TabIndex = 2;
            actions.Controls.Add(attach); actions.Controls.Add(detach); actions.Controls.Add(reset);
            advancedLayout.Controls.Add(actions, 0, 0);
            advancedLayout.Controls.Add(Label("RNNoise · формат движка 48 кГц · задержка движка 30 мс", theme.Small, Theme.Secondary), 0, 1);
            copyDiagnostics.Text = "Скопировать диагностику"; Theme.Button(copyDiagnostics, "Скопировать диагностику", false); copyDiagnostics.Dock = DockStyle.Left; copyDiagnostics.TabIndex = 3;
            advancedLayout.Controls.Add(copyDiagnostics, 0, 2);
            advancedLayout.Controls.Add(Label("Подключение и отключение ненадолго прерывают звук Windows", theme.Small, Theme.Secondary), 0, 3);
            advancedPanel.Controls.Add(advancedLayout); advancedPanel.TabIndex = 4;
            AddSection(stack, advancedPanel, 178);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 8, 0, 0), TabIndex = 2 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            startup.Text = "Запускать при &входе в Windows"; startup.AccessibleName = "Запускать при входе в Windows"; startup.Margin = Padding.Empty; startup.TabIndex = 0;
            footer.Controls.Add(startup, 0, 0);
            var languageLabel = Label("Язык", theme.Small, Theme.Secondary); languageLabel.Margin = new Padding(0, 4, 8, 0);
            footer.Controls.Add(languageLabel, 1, 0); language.Margin = Padding.Empty; language.TabIndex = 1; footer.Controls.Add(language, 2, 0);
            var trayHelp = Label("Крестик оставляет программу в трее, Выход в меню трея отключает обработку", theme.Small, Theme.Secondary);
            footer.Controls.Add(trayHelp, 0, 1); footer.SetColumnSpan(trayHelp, 3);
            shell.Controls.Add(footer, 0, 2);
            tips.SetToolTip(global, "Общий выключатель сохраняет настройки каждого микрофона");
            tips.SetToolTip(attenuation, "Максимальное ослабление фонового шума от 0 до 60 дБ");

            ResumeLayout(true);
        }
        private static Label Label(string text, Font font, Color color)
        { return new Label { Text = text, Font = font, ForeColor = color, AutoSize = true, Margin = Padding.Empty, TabStop = false }; }
        private static void AddSection(TableLayoutPanel stack, Control section, int height)
        {
            int row = stack.Controls.Count;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            section.Dock = DockStyle.Top; section.Height = height - 10; section.Margin = new Padding(0, 0, 0, 10);
            stack.Controls.Add(section, 0, row);
        }
        private void DrawDevice(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= devices.Items.Count) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? Theme.Selection : Theme.Sidebar)) e.Graphics.FillRectangle(brush, e.Bounds);
            int inset = Math.Max(1, 9 * devices.DeviceDpi / 96);
            var area = Rectangle.Inflate(e.Bounds, -inset, -inset);
            TextRenderer.DrawText(e.Graphics, ((DeviceInfo)devices.Items[e.Index]).Name, theme.Body, area, Theme.Ink, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -2, -2), Theme.Ink, selected ? Theme.Selection : Theme.Sidebar);
        }
        protected override void WndProc(ref Message message) { if ((uint)message.Msg == Program.ActivateMessage) Reveal(); base.WndProc(ref message); }
        private void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
        private void RememberTexts(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control != deviceName && control != status && control != strength && !(control is ComboBox) && !String.IsNullOrEmpty(control.Text)) originalTexts[control] = control.Text;
                if (control != deviceName && !String.IsNullOrEmpty(control.AccessibleName)) originalNames[control] = control.AccessibleName;
                RememberTexts(control);
            }
        }
        private void ApplyLanguage()
        {
            UiText.Language = settings.Language;
            bool previous = updating; updating = true;
            SuspendLayout();
            try
            {
                foreach (var pair in originalTexts) pair.Key.Text = UiText.Get(pair.Value);
                foreach (var pair in originalNames) pair.Key.AccessibleName = UiText.Get(pair.Value);
                language.Items.Clear();
                language.Items.Add(new LanguageOption("system"));
                foreach (string id in Languages.Ids) language.Items.Add(new LanguageOption(id));
                language.SelectedIndex = Array.IndexOf(Languages.Ids, settings.Language) + 1;
                if (tray.ContextMenuStrip != null)
                {
                    tray.ContextMenuStrip.Items["TraySettings"].Text = UiText.Get("Настройки");
                    tray.ContextMenuStrip.Items["TrayExit"].Text = UiText.Get("Выход");
                }
                advanced.AccessibleDescription = UiText.Get(advancedPanel.Visible ? "Свернуть дополнительные настройки" : "Развернуть дополнительные настройки");
                tips.SetToolTip(global, UiText.Get("Общий выключатель сохраняет настройки каждого микрофона"));
                tips.SetToolTip(attenuation, UiText.Get("Максимальное ослабление фонового шума от 0 до 60 дБ"));
            }
            finally { updating = previous; ResumeLayout(true); }
            ShowProfile(); UpdateTray(); RebuildTrayDevices();
        }
        private void Error(string message) { MessageBox.Show(this, UiText.Message(message), "Noise Reduction", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        private void Try(Action action) { try { action(); } catch (Exception error) { Error(error.Message); } }
        private void RefreshDevices()
        {
            if (!state.CanRefresh) return;
            refreshAt = DateTime.UtcNow.AddSeconds(2);
            try
            {
                string selected = Selected == null ? null : Selected.Id;
                var list = Integration.EnumerateMicrophones().FindAll(device => device.Active);
                defaultMicrophoneId = Integration.GetDefaultMicrophoneId();
                updating = true;
                try
                {
                    devices.Items.Clear();
                    foreach (var device in list)
                    {
                        devices.Items.Add(device);
                        if (!controls.ContainsKey(device.Id))
                        { controls.Add(device.Id, new ControlFile(Integration.ControlRoot, device.Guid)); activities.Add(device.Id, new Activity()); }
                        if (device.Id == selected) devices.SelectedItem = device;
                    }
                    if (devices.SelectedIndex < 0) foreach (DeviceInfo device in devices.Items) if (device.Id == defaultMicrophoneId) { devices.SelectedItem = device; break; }
                    if (devices.SelectedIndex < 0 && devices.Items.Count > 0) devices.SelectedIndex = 0;
                    state.SetPresent(list.ConvertAll(device => device.Id));
                    ApplyControls(false);
                }
                finally { updating = false; }
                ShowProfile();
                UpdateTray();
                if (devices.Items.Count == 0) status.Text = UiText.Get("Доступные микрофоны не найдены");
            }
            catch (Exception error) { state.RefreshError = "Не удалось обновить микрофоны: " + error.Message; RenderStatus(); UpdateTray(); }
        }
        private void ShowProfile()
        {
            if (updating) return;
            updating = true;
            var device = Selected;
            deviceName.Text = device == null ? UiText.Get("Выберите микрофон") : device.Name;
            deviceName.AccessibleName = deviceName.Text;
            tips.SetToolTip(deviceName, deviceName.Text);
            devices.Enabled = global.Enabled = startup.Enabled = language.Enabled = !busy;
            reset.Enabled = copyDiagnostics.Enabled = device != null && !busy;
            refresh.Enabled = !busy;
            enabled.Enabled = attenuation.Enabled = attach.Enabled = detach.Enabled = record.Enabled = device != null && !busy;
            if (device != null)
            {
                var profile = settings.For(device.Id); enabled.Checked = profile.Enabled; attenuation.Value = profile.Attenuation;
                strength.Text = profile.Attenuation + " " + UiText.Get("дБ");
                attach.Enabled = !device.Attached && !busy; detach.Enabled = device.Attached && !busy;
            }
            dry.Enabled = wet.Enabled = !busy && device != null && device.Id == comparisonId;
            updating = false;
            RenderStatus();
        }
        private void ChangeProfile()
        {
            if (updating || Selected == null) return;
            var profile = settings.For(Selected.Id); profile.Enabled = enabled.Checked; profile.Attenuation = attenuation.Value;
            strength.Text = profile.Attenuation + " " + UiText.Get("дБ"); Persist();
        }
        private void ResetProfile()
        {
            if (Selected == null || busy) return;
            settings.For(Selected.Id).Attenuation = 24;
            Persist(); ShowProfile();
        }
        private string BuildDiagnostics()
        {
            var device = Selected;
            return UiText.Get("Диагностика") + "\n" + (device == null ? UiText.Get("Микрофон не выбран") : device.Name + "\n" + device.Id) + "\nRNNoise\n" + UiText.Get("Формат движка: 48 kHz") + "\n" + UiText.Get("Задержка движка: 30 ms") + "\n" + UiText.Get(device != null && device.Attached ? "Подключён к микрофону" : "Системная обработка не подключена") + "\n" + UiText.Get(device != null && device.EnhancementsEnabled ? "Улучшения звука Windows включены" : "Улучшения звука Windows отключены");
        }
        private void Persist()
        {
            Try(delegate
            {
                settings.Save(settingsPath);
                ApplyControls(false);
                UpdateTray();
            });
        }
        private void ApplyControls(bool forceBypass)
        {
            foreach (var pair in controls)
            {
                var profile = settings.For(pair.Key);
                pair.Value.Set(state.Enabled(pair.Key, settings.Enabled && !forceBypass, profile.Enabled), profile.Attenuation);
            }
        }
        private void Poll()
        {
            foreach (var pair in controls)
            {
                long models = pair.Value.ModelFrames;
                activities[pair.Key].Update(pair.Value.Frames, models);
                trayHealth.Observe(pair.Key, pair.Value.Errors, models);
            }
            RenderStatus();
            UpdateTray();
        }
        private void UpdateTray()
        {
            DeviceInfo current = null;
            foreach (DeviceInfo device in devices.Items) if (device.Id == defaultMicrophoneId) { current = device; break; }
            int next = 0;
            string reason = "Основной микрофон не найден";
            if (state.RefreshError != null) { next = 2; reason = "Ошибка списка микрофонов"; }
            else if (current != null)
            {
                bool requested = settings.Enabled && settings.For(current.Id).Enabled;
                if (!requested) reason = "Шумоподавление выключено";
                else if (!current.Attached) reason = "Эффект не подключён";
                else if (!current.EnhancementsEnabled) { next = 2; reason = "Улучшения звука Windows выключены"; }
                else if (trayHealth.Failed(current.Id)) { next = 2; reason = "Ошибка обработки"; }
                else { next = 1; reason = "Шумоподавление включено"; }
            }
            if (next != trayState) { tray.Icon = theme.TrayIcon(next); trayState = next; }
            string text = UiText.Get(reason) + (current == null ? "" : "\n" + current.Name);
            tray.Text = text.Length > 63 ? text.Substring(0, 62) + "…" : text;
        }
        private async void OpenTrayMenu(object sender, CancelEventArgs args)
        {
            int generation = ++menuGeneration;
            var microphones = new List<DeviceInfo>();
            foreach (DeviceInfo device in devices.Items) microphones.Add(device);
            usageQueryPending = true; usageSnapshots.Clear(); RebuildTrayDevices();
            Dictionary<string, CaptureSessionSnapshot> results;
            try
            {
                results = await System.Threading.Tasks.Task.Run(delegate
                {
                    var snapshots = new Dictionary<string, CaptureSessionSnapshot>();
                    foreach (var device in microphones) snapshots[device.Id] = CaptureSessions.TryGetSnapshot(device.Id);
                    return snapshots;
                });
            }
            catch { results = new Dictionary<string, CaptureSessionSnapshot>(); }
            if (generation != menuGeneration || IsDisposed) return;
            usageQueryPending = false;
            usageSnapshots.Clear(); foreach (var pair in results) usageSnapshots[pair.Key] = pair.Value;
            if (tray.ContextMenuStrip.Visible) RebuildTrayDevices();
        }
        private void RebuildTrayDevices()
        {
            if (trayMaster == null || trayDevices == null) return;
            trayMaster.Text = UiText.Get(settings.Enabled ? "Приостановить обработку" : "Возобновить обработку");
            trayMaster.Checked = settings.Enabled; trayMaster.Enabled = !busy;
            trayDevices.Text = UiText.Get("Микрофоны");
            while (trayDevices.DropDownItems.Count > 0) { var old = trayDevices.DropDownItems[0]; trayDevices.DropDownItems.RemoveAt(0); old.Dispose(); }
            foreach (DeviceInfo device in devices.Items)
            {
                string id = device.Id;
                CaptureSessionSnapshot snapshot;
                usageSnapshots.TryGetValue(id, out snapshot);
                string usage = usageQueryPending ? "Проверка приложений" : snapshot == null || !snapshot.Available ? "Активность неизвестна" : snapshot.Sessions.Count > 0 ? "Используется" : "Не используется";
                string name = device.Name + (id == defaultMicrophoneId ? " (" + UiText.Get("По умолчанию") + ")" : "") + " · " + UiText.Get(usage);
                var item = new ToolStripMenuItem(name) { Checked = settings.Enabled && settings.For(id).Enabled, Enabled = !busy, Tag = id };
                var applications = new List<string>();
                if (snapshot != null && snapshot.Available) foreach (var session in snapshot.Sessions) if (!applications.Contains(session.ApplicationName)) applications.Add(session.ApplicationName);
                item.ToolTipText = applications.Count > 0 ? UiText.Get("Приложения") + ": " + String.Join(", ", applications.ToArray()) : UiText.Get(usage);
                item.Click += delegate
                {
                    if (busy) return;
                    var profile = settings.For(id);
                    bool turnOn = !(settings.Enabled && profile.Enabled);
                    profile.Enabled = turnOn;
                    if (turnOn) settings.Enabled = true;
                    SaveTrayProfile();
                };
                trayDevices.DropDownItems.Add(item);
            }
            if (trayDevices.DropDownItems.Count == 0) trayDevices.DropDownItems.Add(new ToolStripMenuItem(UiText.Get("Доступные микрофоны не найдены")) { Enabled = false });
        }
        private void SaveTrayProfile()
        {
            bool wasUpdating = updating; updating = true; global.Checked = settings.Enabled; updating = wasUpdating;
            Persist(); ShowProfile(); RebuildTrayDevices();
        }
        private void RenderStatus()
        {
            if (state.RefreshError != null) { status.Text = UiText.Message(state.Status(null)); return; }
            if (Selected == null || busy) return;
            ControlFile control;
            if (!controls.TryGetValue(Selected.Id, out control)) return;
            bool active = activities[Selected.Id].Active;
            var profile = settings.For(Selected.Id);
            string processing = !settings.Enabled || !profile.Enabled ? "Обработка выключена" : active ? "Обработка активна" : "Ожидание звука, начните запись в приложении";
            status.ForeColor = active && settings.Enabled && profile.Enabled ? Theme.Accent : Theme.Secondary;
            status.Text = UiText.Message((Selected.Attached ? "Подключён к микрофону" : "Системная обработка не подключена") + "\n" + processing + (Selected.EnhancementsEnabled ? "" : "\nУлучшения звука Windows отключены") + (trayHealth.Failed(Selected.Id) ? "\nОбработка сообщила об ошибке" : ""));
        }
        private async void Integrate(bool install)
        {
            if (Selected == null || busy) return;
            string id = Selected.Id;
            bool succeeded = false;
            state.Integrating = true;
            ShowProfile();
            status.Text = UiText.Get(install ? "Windows проверяет обработку, звук не сохраняется и не воспроизводится" : "Отключение эффекта");
            try
            {
                if (id.IndexOf('"') >= 0) throw new InvalidOperationException("Некорректный ID микрофона");
                using (var probe = install ? new ProbeConfiguration(controls[id]) : null)
                using (var process = Process.Start(new ProcessStartInfo(Application.ExecutablePath, (install ? "--attach" : "--detach") + " \"" + id + "\"") { UseShellExecute = true, Verb = "runas" }))
                {
                    await System.Threading.Tasks.Task.Run(delegate { process.WaitForExit(); });
                    if (process.ExitCode != 0) throw new InvalidOperationException("Команда не завершена, проверьте сообщение проверки подключения");
                }
                succeeded = true;
            }
            catch (Exception error) { Error(error.Message); }
            finally
            {
                state.Integrating = false;
                ShowProfile();
                RefreshDevices();
                if (succeeded) status.Text = UiText.Get(install ? "Работа модели подтверждена, подключение сохранено" : "Эффект отключён");
            }
        }
        private async void RecordComparison()
        {
            var device = Selected;
            if (device == null || busy) return;
            player.Stop(); state.CaptureEndpoint = device.Id; comparisonId = null;
            ShowProfile();
            string directory = Path.Combine(Path.GetTempPath(), "NoiseReduction-" + Guid.NewGuid().ToString("N"));
            var profile = settings.For(device.Id);
            try
            {
                Directory.CreateDirectory(directory);
                ApplyControls(false);
                status.Text = UiText.Get("Говорите в выбранный микрофон, запись 5 секунд");
                await System.Threading.Tasks.Task.Run(delegate { Comparison.Create(device.Id, directory, profile.Attenuation); });
                if (recordingDirectory != null) try { Directory.Delete(recordingDirectory, true); } catch (IOException) { }
                recordingDirectory = directory; comparisonId = device.Id;
                status.Text = UiText.Get("Готово: оригинал и локальная обработка RNNoise, системный эффект здесь не проверяется");
            }
            catch (Exception error) { Error(error.Message); try { Directory.Delete(directory, true); } catch (IOException) { } }
            finally
            {
                state.CaptureEndpoint = null;
                ApplyControls(false);
                ShowProfile();
            }
        }
        private void Play(string file) { Try(delegate { player.Stop(); player.SoundLocation = Path.Combine(recordingDirectory, file); player.Load(); player.Play(); }); }
    }
}
