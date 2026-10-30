using System;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;
using NoiseReduction.Platform;

namespace NoiseReduction.Desktop
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--keep-raw-off") { ServiceBase.Run(new RawKeeper()); return 0; }
            try
            {
                UiText.Language = Settings.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoiseReduction", "settings.json")).Language;
                if (args.Length == 0) throw new ArgumentException("Неизвестная команда");
                switch (args[0])
                {
                    case "--attach": if (args.Length != 2) throw new ArgumentException("Нужен ID микрофона"); AttachVerified(args[1]); break;
                    case "--detach": if (args.Length != 2) throw new ArgumentException("Нужен ID микрофона"); DetachAndRefresh(args[1]); break;
                    case "--register": if (args.Length != 1) throw new ArgumentException("Неизвестная команда"); Integration.RegisterEngine(AppDomain.CurrentDomain.BaseDirectory); break;
                    case "--unregister": if (args.Length != 1) throw new ArgumentException("Неизвестная команда"); Integration.UnregisterEngine(); break;
                    default: throw new ArgumentException("Неизвестная команда");
                }
                return 0;
            }
            catch (Exception error)
            {
                // полный текст с кодами ошибок нужен для разбора, пользователь видит короткий
                try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NoiseReduction", "engine-errors.log"), DateTime.UtcNow.ToString("o") + " " + String.Join(" ", args) + Environment.NewLine + error + Environment.NewLine); } catch { }
                MessageBox.Show(UiText.Message(error.Message), "Noise Reduction", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
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
                    using (var verification = Integration.BeginVerifiedAttachment(id, device.EffectPack))
                    {
                        device = Integration.EnumerateMicrophones().Find(value => value.Id == id && value.Active);
                        if (device == null) throw new InvalidOperationException("Микрофон недоступен после восстановления подключения");
                        try
                        {
                            AttachmentTransaction.Run(control, delegate
                            {
                                // микрофоны под пакетами Windows получают наш пакет эффектов, остальные эффект в цепочке драйвера
                                if (device.EffectPack) Integration.SelectEffectPack(id); else Integration.Attach(id);
                                if (!device.Attached)
                                {
                                    refreshOnRollback = true;
                                    Integration.RefreshAudioGraph();
                                }
                            }, delegate
                            {
                                long initial = control.ModelFrames;
                                // драйвер может ещё перезапускать звук после подключения, даём ему пару попыток
                                for (int attempt = 0; ; attempt++)
                                {
                                    try { Comparison.VerifyCapture(id, delegate { return control.ModelFrames > initial; }); break; }
                                    catch (InvalidOperationException error)
                                    {
                                        if (attempt >= 2 || !(error.InnerException is System.Runtime.InteropServices.COMException)) throw;
                                        System.Threading.Thread.Sleep(1500);
                                    }
                                }
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

    // служба запускается по просьбе приложения, делает одно дело и останавливается; окон в ней показывать нельзя
    internal sealed class RawKeeper : ServiceBase
    {
        public RawKeeper() { ServiceName = Integration.RawKeeperService; }
        protected override void OnStart(string[] args)
        {
            new Thread(() =>
            {
                try { Integration.KeepRawOff(); }
                catch (Exception error)
                {
                    try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NoiseReduction", "engine-errors.log"), DateTime.UtcNow.ToString("o") + " --keep-raw-off" + Environment.NewLine + error + Environment.NewLine); } catch { }
                }
                Stop();
            }) { IsBackground = true }.Start();
        }
    }

}
