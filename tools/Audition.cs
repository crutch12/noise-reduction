using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal sealed class WavePlayer : IDisposable
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder result, int size, IntPtr callback);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool mciGetErrorString(int error, StringBuilder text, int length);
    private bool opened;

    private string Send(string command)
    {
        var result = new StringBuilder(256);
        int error = mciSendString(command, result, result.Capacity, IntPtr.Zero);
        if (error != 0)
        {
            mciGetErrorString(error, result, result.Capacity);
            throw new InvalidOperationException(result.ToString());
        }
        return result.ToString();
    }

    public void Open(string file)
    {
        Dispose();
        if (!File.Exists(file)) throw new FileNotFoundException("Не найдена запись", file);
        Send("open \"" + Path.GetFullPath(file) + "\" type waveaudio alias comparison");
        opened = true;
        Send("set comparison time format milliseconds");
    }

    public int Length { get { return int.Parse(Send("status comparison length")); } }
    public int Position { get { return int.Parse(Send("status comparison position")); } }
    public bool Playing { get { return Send("status comparison mode") == "playing"; } }
    public void Play() { Send("play comparison"); }
    public void Pause() { Send("pause comparison"); }
    public void Seek(int value) { Send("seek comparison to " + Math.Max(0, Math.Min(value, Length))); }
    public void Dispose()
    {
        if (opened) { mciSendString("close comparison", null, 0, IntPtr.Zero); opened = false; }
    }
}

internal sealed class Audition : Form
{
    private static readonly string[] Files = { "original.wav", "rnnoise-24db.wav", "rnnoise-full.wav", "deepfilter-24db.wav", "deepfilter-100db.wav" };
    private static readonly string[] Labels = { "Оригинал", "RNNoise / мягче", "RNNoise / полное", "DeepFilterNet / мягче", "DeepFilterNet / полное" };
    private readonly WavePlayer player = new WavePlayer();
    private readonly TrackBar position = new TrackBar();
    private readonly Label clock = new Label();
    private readonly Button play = new Button();
    private readonly Timer timer = new Timer();
    private readonly string directory;
    private bool loaded;
    private bool dragging;
    private bool resumeAfterDrag;

    public Audition(string folder)
    {
        directory = folder;
        Text = "Noise Reduction | Сравнение";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(660, 410);
        MinimumSize = Size;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        var title = new Label { Text = "Сравнение на твоей записи", AutoSize = true, Location = new Point(24, 22), Font = new Font("Segoe UI", 16, FontStyle.Bold) };
        Controls.Add(title);
        Controls.Add(new Label { Text = "Переключай вариант во время воспроизведения", AutoSize = true, Location = new Point(25, 64) });
        for (int i = 0; i < Files.Length; i++)
        {
            int index = i;
            var choice = new RadioButton { Text = Labels[i], AutoSize = true, Location = new Point(28, 104 + i * 32) };
            choice.CheckedChanged += delegate { if (choice.Checked) Switch(index); };
            Controls.Add(choice);
            if (i == 0) Shown += delegate { choice.Checked = true; };
        }
        position.SetBounds(22, 272, 610, 40);
        position.TickStyle = TickStyle.None;
        position.MouseDown += delegate { dragging = true; resumeAfterDrag = loaded && player.Playing; if (resumeAfterDrag) player.Pause(); };
        position.MouseUp += delegate { if (loaded) { player.Seek(position.Value); if (resumeAfterDrag) player.Play(); } dragging = false; };
        position.KeyDown += delegate { if (!dragging) { dragging = true; resumeAfterDrag = loaded && player.Playing; if (resumeAfterDrag) player.Pause(); } };
        position.KeyUp += delegate { if (loaded) { player.Seek(position.Value); if (resumeAfterDrag) player.Play(); } dragging = false; };
        Controls.Add(position);
        play.Text = "Слушать";
        play.SetBounds(26, 322, 130, 36);
        play.Click += delegate
        {
            if (!loaded) return;
            if (player.Playing) player.Pause();
            else { if (player.Position >= player.Length) player.Seek(0); player.Play(); }
        };
        Controls.Add(play);
        clock.SetBounds(177, 329, 240, 30);
        Controls.Add(clock);
        Controls.Add(new Label { Text = "Локальное сравнение файлов, системная обработка пока не установлена", AutoSize = true, Location = new Point(25, 377), ForeColor = SystemColors.GrayText });
        timer.Interval = 100;
        timer.Tick += delegate
        {
            if (!loaded || dragging) return;
            position.Value = Math.Min(position.Maximum, player.Position);
            clock.Text = TimeSpan.FromMilliseconds(position.Value).ToString(@"mm\:ss") + " / " + TimeSpan.FromMilliseconds(player.Length).ToString(@"mm\:ss");
            play.Text = player.Playing ? "Пауза" : "Слушать";
        };
        timer.Start();
        FormClosed += delegate { timer.Dispose(); player.Dispose(); };
    }

    private void Switch(int index)
    {
        try
        {
            int time = loaded ? player.Position : 0;
            bool running = loaded && player.Playing;
            loaded = false;
            player.Open(Path.Combine(directory, Files[index]));
            position.Maximum = player.Length;
            player.Seek(time);
            loaded = true;
            if (running) player.Play();
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Не удалось открыть запись", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio");
        if (args.Length == 1 && args[0] == "--self-test")
        {
            try
            {
                using (var test = new WavePlayer())
                {
                    int expected = 0;
                    foreach (string name in Files)
                    {
                        test.Open(Path.Combine(folder, name));
                        if (expected == 0) expected = test.Length;
                        if (test.Length != expected || test.Length <= 0) return 2;
                        test.Seek(Math.Min(1000, expected));
                        if (Math.Abs(test.Position - Math.Min(1000, expected)) > 1) return 3;
                    }
                }
                return 0;
            }
            catch { return 1; }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Audition(folder));
        return 0;
    }
}
