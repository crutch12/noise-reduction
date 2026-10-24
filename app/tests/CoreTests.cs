using System;
using System.IO;
using System.Threading;
using NoiseReduction.Desktop;
class CoreTests
{
    private sealed class Endpoint
    {
        public bool Present;
        public int Attaches, Detaches;
        public bool FailAttach, FailDetach;
        public bool Attached { get { return Present; } }
        public void Attach() { Attaches++; Present = true; if (FailAttach) throw new InvalidOperationException("Partial attach failure"); }
        public void Detach() { Detaches++; if (FailDetach) throw new InvalidOperationException("Rollback failure"); Present = false; }
    }
    private sealed class Verification : IDisposable
    {
        private readonly Endpoint endpoint;
        private readonly bool existing;
        private bool committed;
        public Verification(Endpoint endpoint) { this.endpoint = endpoint; existing = endpoint.Attached; }
        public void Commit() { committed = true; }
        public void Dispose() { if (!committed && !existing && endpoint.Attached) endpoint.Detach(); }
    }
    static void RunAttachment(Endpoint endpoint, ControlFile control, string root, string guid, Action verify)
    {
        using (var probe = new ProbeConfiguration(control))
        using (var readOnly = new ControlFile(root, guid, true))
        using (var scope = new Verification(endpoint))
        {
            AttachmentTransaction.Run(readOnly, delegate { if (!endpoint.Attached) endpoint.Attach(); }, verify);
            scope.Commit();
        }
    }
    static void ModelFrame(string root, string guid, long frames)
    { using (var file = new FileStream(Path.Combine(root, guid + ".bin"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { file.Position = 24; file.Write(BitConverter.GetBytes(frames), 0, 8); } }
    static InvalidOperationException ExpectFailure(Action action, string message)
    { try { action(); } catch (InvalidOperationException error) { return error; } throw new Exception(message); }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    static byte[] ReadShared(string path) { using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { var data = new byte[64]; file.Read(data, 0, 64); return data; } }
    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "nr-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            SecurityTests.Run(root);
            string path = Path.Combine(root, "settings.json");
            Settings settings = Settings.Load(path);
            Check(settings.Enabled && settings.For("mic-a").Attenuation == 24, "new profile defaults");
            settings.For("mic-a").Attenuation = 37;
            settings.For("mic-a").Enabled = false;
            settings.For("mic-b").Attenuation = 12;
            settings.Save(path);
            settings.Enabled = false;
            settings.Save(path);
            settings = Settings.Load(path);
            Check(!settings.Enabled && !settings.For("mic-a").Enabled && settings.For("mic-a").Attenuation == 37 && settings.For("mic-b").Attenuation == 12, "atomic persistence and endpoint identity");
            File.WriteAllText(path, "broken");
            Check(Settings.Load(path).For("mic-a").Attenuation == 24, "corrupt settings fallback");
            File.WriteAllText(path, "{\"Profiles\":[{\"Id\":\"mic-a\"}]}");
            settings = Settings.Load(path);
            Check(settings.Enabled && settings.For("mic-a").Enabled && settings.For("mic-a").Attenuation == 24, "missing persisted fields use safe defaults");
            string guid = "a2345678-1234-1234-1234-123456789abc";
            using (var control = new ControlFile(root, guid))
            {
                using (var file = new FileStream(Path.Combine(root, guid + ".bin"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                { file.Position = 16; file.Write(BitConverter.GetBytes(1234L), 0, 8); }
                control.Set(true, 24);
                byte[] bytes = ReadShared(Path.Combine(root, guid + ".bin"));
                Check(bytes.Length == 64 && BitConverter.ToUInt32(bytes, 0) == 0x3143524e && BitConverter.ToUInt32(bytes, 8) == 6145 && control.Frames == 1234, "protocol preserves native counters");
                control.Set(false, 90);
                Check(BitConverter.ToUInt32(ReadShared(Path.Combine(root, guid + ".bin")), 8) == 15360, "disable and attenuation clamp");
                string token = "b2345678-1234-1234-1234-123456789abc";
                string marker = guid + "." + token + ".notify";
                Check(ControlFile.NotificationName(marker) == "Global\\NoiseReduction.Changed." + guid + "." + token, "canonical notification name");
                Check(ControlFile.NotificationName(marker + ".txt") == null && ControlFile.NotificationName("../" + marker) == null && ControlFile.NotificationName("wrong." + token + ".notify") == null, "invalid notification names rejected");
                File.WriteAllText(Path.Combine(root, marker), "");
                using (var notification = new EventWaitHandle(false, EventResetMode.AutoReset, ControlFile.NotificationName(marker)))
                {
                    control.Set(true, 24);
                    Check(notification.WaitOne(1000), "changed control notifies APO");
                    control.Set(true, 24);
                    Check(!notification.WaitOne(0), "unchanged control does not notify APO");
                }
                control.Set(false, 24);
                Check(control.ModelFrames == 0 && control.Errors == 0, "native model and error counters read");
                control.SetConfiguration(0x10002500);
                uint original = control.Configuration;
                var endpoint = new Endpoint();
                using (var readOnly = new ControlFile(root, guid, true))
                {
                    ExpectFailure(delegate { readOnly.Set(true, 24); }, "readonly control must reject writes");
                    ExpectFailure(delegate { AttachmentTransaction.Run(readOnly, endpoint.Attach, delegate { }); }, "disabled readonly config must reject attach");
                    Check(endpoint.Attaches == 0 && control.Configuration == original, "elevated disabled probe leaves endpoint and config unchanged");
                }
                RunAttachment(endpoint, control, root, guid, delegate { Check((control.Configuration & 1) != 0 && endpoint.Attached, "verification temporarily enables attached endpoint"); ModelFrame(root, guid, 1); });
                Check(endpoint.Attaches == 1 && endpoint.Detaches == 0 && endpoint.Attached && control.Configuration == original, "verified attach succeeds and restores exact configuration");
                endpoint = new Endpoint();
                ExpectFailure(delegate { RunAttachment(endpoint, control, root, guid, delegate { }); }, "stalled model must fail attachment");
                Check(endpoint.Detaches == 1 && !endpoint.Attached && control.Configuration == original, "stalled model rolls back new attachment and restores configuration");
                endpoint = new Endpoint();
                ExpectFailure(delegate { RunAttachment(endpoint, control, root, guid, delegate { throw new InvalidOperationException("Capture invalidated"); }); }, "capture failure must fail attachment");
                Check(endpoint.Detaches == 1 && !endpoint.Attached && control.Configuration == original, "capture failure rolls back new attachment and restores configuration");
                endpoint = new Endpoint { Present = true };
                ExpectFailure(delegate { RunAttachment(endpoint, control, root, guid, delegate { }); }, "existing unverified effect must fail verification");
                Check(endpoint.Attaches == 0 && endpoint.Detaches == 0 && endpoint.Attached && control.Configuration == original, "preexisting attachment is preserved on verification failure");
                endpoint = new Endpoint { FailAttach = true };
                ExpectFailure(delegate { RunAttachment(endpoint, control, root, guid, delegate { throw new Exception("Verifier must not run after attach failure"); }); }, "partial attachment must fail");
                Check(endpoint.Detaches == 1 && !endpoint.Attached && control.Configuration == original, "partial attach failure is rolled back");
                endpoint = new Endpoint { FailDetach = true };
                var rollbackError = ExpectFailure(delegate { RunAttachment(endpoint, control, root, guid, delegate { }); }, "failed rollback must fail attachment");
                Check(rollbackError.Message.Contains("Rollback failure") && control.Configuration == original, "rollback failure is reported and exact config is restored");
            }
            var state = new DesktopState();
            state.SetPresent(new[] { "mic-a", "mic-b" });
            Check(state.Enabled("mic-a", true, true), "present enabled microphone processes");
            state.CaptureEndpoint = "mic-a";
            Check(!state.CanRefresh && !state.SetPresent(new[] { "mic-b" }) && !state.Enabled("mic-a", true, true), "refresh and settings preserve comparison bypass");
            Check(state.Enabled("mic-b", true, true), "comparison only bypasses captured microphone");
            state.CaptureEndpoint = null;
            state.SetPresent(new[] { "mic-b" });
            Check(!state.Enabled("mic-a", true, true) && !state.Enabled("mic-b", false, true) && !state.Enabled("mic-b", true, false), "settings cannot re-enable absent or disabled microphones");
            state.RefreshError = "Enumeration failed";
            Check(state.Status("Processing") == "Enumeration failed", "refresh error takes precedence over activity status");
            state.SetPresent(new[] { "mic-b" });
            Check(state.Status("Processing") == "Processing", "successful refresh clears error");
            state.Integrating = true;
            Check(!state.CanRefresh && !state.SetPresent(new[] { "mic-a" }), "elevated operation blocks refresh");
            var activity = new Activity();
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Check(!activity.Update(100, 0, now) && !activity.Update(200, 0, now.AddSeconds(1)), "audio fail-open frames do not prove model processing");
            Check(activity.Update(300, 1, now.AddSeconds(2)) && activity.Update(300, 1, now.AddSeconds(2.01)), "short audio quantum gaps retain confirmed status");
            Check(!activity.Update(400, 1, now.AddSeconds(4)) && !activity.Update(0, 0, now.AddSeconds(5)), "stalled model expires and reset counters invalidate status");
            Console.WriteLine("PASS: defaults, persistence, corruption, protocol, counters, activity");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
