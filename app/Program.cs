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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] != "--tray")
            {
                try
                {
                    switch (args[0])
                    {
                        case "--attach": if (args.Length != 2) throw new ArgumentException("Нужен ID микрофона"); AttachVerified(args[1]); break;
                        case "--detach": if (args.Length != 2) throw new ArgumentException("Нужен ID микрофона"); Integration.Detach(args[1]); break;
                        case "--register": Integration.RegisterEngine(AppDomain.CurrentDomain.BaseDirectory); break;
                        case "--unregister": Integration.UnregisterEngine(); break;
                        default: throw new ArgumentException("Неизвестная команда");
                    }
                    return 0;
                }
                catch (Exception error) { MessageBox.Show(error.Message, "Noise Reduction", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\NoiseReduction.Desktop.1", out created))
            {
                if (!created) { PostMessage(new IntPtr(0xffff), ActivateMessage, IntPtr.Zero, IntPtr.Zero); return 0; }
                try { Application.Run(new MainWindow(args.Length == 1)); }
                catch (Exception error) { MessageBox.Show(error.Message, "Noise Reduction", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
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
                try
                {
                    using (var control = new ControlFile(Integration.ControlRoot, device.Guid, true))
                    using (var verification = Integration.BeginVerifiedAttachment(id))
                    {
                        try
                        {
                            AttachmentTransaction.Run(control, delegate { Integration.Attach(id); }, delegate
                            {
                                long initial = control.ModelFrames;
                                Comparison.VerifyCapture(id, delegate { return control.ModelFrames > initial; });
                            });
                            verification.Commit();
                        }
                        catch (Exception error) { verificationFailure = error; throw; }
                    }
                }
                catch (Exception error)
                {
                    if (Object.ReferenceEquals(error, verificationFailure))
                        throw new InvalidOperationException(error.Message + (device.Attached ? "\nПрежнее подключение сохранено" : "\nНовое подключение отменено"), error);
                    throw;
                }
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
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly ListBox devices = new ListBox();
        private readonly CheckBox global = new CheckBox { Text = "Шумоподавление включено", AutoSize = true };
        private readonly CheckBox enabled = new CheckBox { Text = "Обрабатывать этот микрофон", AutoSize = true };
        private readonly CheckBox startup = new CheckBox { Text = "Запускать при входе в Windows", AutoSize = true };
        private readonly TrackBar attenuation = new TrackBar { Minimum = 0, Maximum = 60, TickFrequency = 10 };
        private readonly Label strength = new Label { AutoSize = true };
        private readonly Label status = new Label();
        private readonly Button attach = new Button { Text = "Подключить" };
        private readonly Button detach = new Button { Text = "Отключить" };
        private readonly Button refresh = new Button { Text = "Обновить" };
        private readonly Button record = new Button { Text = "Записать 5 секунд" };
        private readonly Button dry = new Button { Text = "Слушать оригинал", Enabled = false };
        private readonly Button wet = new Button { Text = "Слушать обработку", Enabled = false };
        private readonly System.Media.SoundPlayer player = new System.Media.SoundPlayer();
        private bool updating;
        private bool exiting;
        private bool busy { get { return !state.CanRefresh; } }
        private string comparisonId;
        private string recordingDirectory;
        private DateTime refreshAt;
        private DeviceInfo Selected { get { return devices.SelectedItem as DeviceInfo; } }

        internal MainWindow(bool hidden)
        {
            settings = Settings.Load(settingsPath);
            Text = "Noise Reduction";
            Font = new Font("Segoe UI", 10);
            ClientSize = new Size(740, 620);
            MinimumSize = new Size(640, 660);
            StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 11 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "Микрофоны", Font = new Font(Font, FontStyle.Bold), AutoSize = true });
            layout.Controls.Add(global);
            devices.DisplayMember = "Name";
            devices.Format += delegate(object sender, ListControlConvertEventArgs e) { var d = (DeviceInfo)e.ListItem; e.Value = d.Name; };
            devices.FormattingEnabled = true;
            devices.Dock = DockStyle.Fill;
            layout.Controls.Add(devices);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(enabled);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            var level = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill };
            level.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); level.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            attenuation.Dock = DockStyle.Fill;
            level.Controls.Add(attenuation); level.Controls.Add(strength);
            layout.Controls.Add(level); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            AddButton(actions, attach); AddButton(actions, detach);
            AddButton(actions, refresh);
            layout.Controls.Add(actions); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            status.Dock = DockStyle.Fill; layout.Controls.Add(status); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.Controls.Add(new Label { Text = "Сравнение: запись выбранного микрофона без нашего эффекта, затем локальная обработка RNNoise", AutoSize = true, MaximumSize = new Size(680, 0) });
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            var comparison = new FlowLayoutPanel { Dock = DockStyle.Fill };
            AddButton(comparison, record); AddButton(comparison, dry); AddButton(comparison, wet);
            var stop = new Button { Text = "Стоп" }; AddButton(comparison, stop);
            layout.Controls.Add(comparison); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            layout.Controls.Add(startup); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label { Text = "Крестик сворачивает в трей • Выход в меню трея отключает обработку", AutoSize = true, ForeColor = SystemColors.GrayText });
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
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
            stop.Click += delegate { player.Stop(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Настройки", null, delegate { Reveal(); });
            menu.Items.Add("Выход", null, delegate { if (!busy) { exiting = true; Close(); } else Error("Дождитесь завершения операции"); });
            tray.Icon = SystemIcons.Application; tray.Text = "Noise Reduction"; tray.ContextMenuStrip = menu; tray.Visible = true;
            tray.DoubleClick += delegate { Reveal(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing && !exiting) { e.Cancel = true; Hide(); return; }
                ApplyControls(true);
                foreach (var control in controls.Values) control.Dispose();
                timer.Stop(); tray.Visible = false; tray.Dispose(); player.Dispose();
                if (recordingDirectory != null) try { Directory.Delete(recordingDirectory, true); } catch (IOException) { }
            };
            Shown += delegate { RefreshDevices(); if (hidden) Hide(); };
            timer.Interval = 1000;
            timer.Tick += delegate { if (!busy && DateTime.UtcNow >= refreshAt) RefreshDevices(); Poll(); };
            timer.Start();
        }
        private static void AddButton(Control parent, Button button) { button.AutoSize = true; button.Height = 32; parent.Controls.Add(button); }
        protected override void WndProc(ref Message message) { if ((uint)message.Msg == Program.ActivateMessage) Reveal(); base.WndProc(ref message); }
        private void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
        private void Error(string message) { MessageBox.Show(this, message, "Noise Reduction", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        private void Try(Action action) { try { action(); } catch (Exception error) { Error(error.Message); } }
        private void RefreshDevices()
        {
            if (!state.CanRefresh) return;
            refreshAt = DateTime.UtcNow.AddSeconds(5);
            try
            {
                string selected = Selected == null ? null : Selected.Id;
                var list = Integration.EnumerateMicrophones().FindAll(device => device.Active);
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
                    if (devices.SelectedIndex < 0 && devices.Items.Count > 0) devices.SelectedIndex = 0;
                    state.SetPresent(list.ConvertAll(device => device.Id));
                    ApplyControls(false);
                }
                finally { updating = false; }
                ShowProfile();
                if (devices.Items.Count == 0) status.Text = "Доступные микрофоны не найдены";
            }
            catch (Exception error) { state.RefreshError = "Не удалось обновить микрофоны: " + error.Message; RenderStatus(); }
        }
        private void ShowProfile()
        {
            if (updating) return;
            updating = true;
            var device = Selected;
            devices.Enabled = global.Enabled = startup.Enabled = !busy;
            refresh.Enabled = !busy;
            enabled.Enabled = attenuation.Enabled = attach.Enabled = detach.Enabled = record.Enabled = device != null && !busy;
            if (device != null)
            {
                var profile = settings.For(device.Id); enabled.Checked = profile.Enabled; attenuation.Value = profile.Attenuation;
                strength.Text = "Ослабление: " + profile.Attenuation + " дБ";
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
            strength.Text = "Ослабление: " + profile.Attenuation + " дБ"; Persist();
        }
        private void Persist()
        {
            Try(delegate
            {
                settings.Save(settingsPath);
                ApplyControls(false);
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
            foreach (var pair in controls) activities[pair.Key].Update(pair.Value.Frames, pair.Value.ModelFrames);
            RenderStatus();
        }
        private void RenderStatus()
        {
            if (state.RefreshError != null) { status.Text = state.Status(null); return; }
            if (Selected == null || busy) return;
            ControlFile control;
            if (!controls.TryGetValue(Selected.Id, out control)) return;
            bool active = activities[Selected.Id].Active;
            var profile = settings.For(Selected.Id);
            string processing = active ? (settings.Enabled && profile.Enabled ? "Обработка активна" : "Поток активен, наш эффект выключен") : "Обработка не подтверждена: откройте запись в приложении";
            status.Text = (Selected.Attached ? "Эффект подключён к микрофону" : "Эффект не подключён") + "\n" + processing + (Selected.EnhancementsEnabled ? "" : "\nУлучшения звука Windows отключены") + (control.Errors > 0 ? "\nОшибок движка: " + control.Errors : "");
        }
        private async void Integrate(bool install)
        {
            if (Selected == null || busy) return;
            string id = Selected.Id;
            bool succeeded = false;
            state.Integrating = true;
            ShowProfile();
            status.Text = install ? "Windows проверяет обработку, звук не сохраняется и не воспроизводится" : "Отключение эффекта";
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
                if (succeeded) status.Text = install ? "Работа модели подтверждена, подключение сохранено" : "Эффект отключён, перезапустите запись в использующем микрофон приложении";
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
                status.Text = "Говорите в выбранный микрофон, запись 5 секунд";
                await System.Threading.Tasks.Task.Run(delegate { Comparison.Create(device.Id, directory, profile.Attenuation); });
                if (recordingDirectory != null) try { Directory.Delete(recordingDirectory, true); } catch (IOException) { }
                recordingDirectory = directory; comparisonId = device.Id;
                status.Text = "Готово: оригинал и локальная обработка RNNoise, системный эффект здесь не проверяется";
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
