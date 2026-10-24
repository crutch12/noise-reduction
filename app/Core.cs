using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Text.RegularExpressions;
using System.Threading;
using System.Globalization;

namespace NoiseReduction.Desktop
{
    [DataContract]
    public sealed class Profile
    {
        [DataMember] public string Id;
        [DataMember] public bool Enabled = true;
        [DataMember] public int Attenuation = 24;
        [DataMember] public bool LowCut;
        [OnDeserializing] private void Defaults(StreamingContext context) { Enabled = true; Attenuation = 24; LowCut = false; }
    }
    [DataContract]
    public sealed class Settings
    {
        [DataMember] public bool Enabled = true;
        [DataMember] public string Language = "system";
        [DataMember] public List<Profile> Profiles = new List<Profile>();
        [DataMember] public bool EnableNewMicrophones = true;
        [OnDeserializing] private void Defaults(StreamingContext context) { Enabled = true; Language = "system"; Profiles = new List<Profile>(); EnableNewMicrophones = true; }
        public bool Has(string id) { return Profiles.Exists(p => p.Id == id); }
        public Profile For(string id)
        {
            var profile = Profiles.Find(p => p.Id == id);
            if (profile == null) { profile = new Profile { Id = id, Enabled = EnableNewMicrophones }; Profiles.Add(profile); }
            profile.Attenuation = Math.Max(0, Math.Min(60, profile.Attenuation));
            return profile;
        }
        public static Settings Load(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var value = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                    if (value == null || value.Profiles == null) return new Settings();
                    value.Profiles.RemoveAll(p => p == null || String.IsNullOrEmpty(p.Id));
                    value.Language = LanguagePreference.Normalize(value.Language);
                    return value;
                }
            }
            catch (IOException) { return new Settings(); }
            catch (SerializationException) { return new Settings(); }
        }
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { new DataContractJsonSerializer(typeof(Settings)).WriteObject(stream, this); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
    }
    public static class LanguagePreference
    {
        public static string Normalize(string value)
        {
            value = (value ?? "system").Trim().ToLowerInvariant();
            return value == "ru" || value == "en" ? value : "system";
        }
        public static string Resolve(string preference, string systemCulture)
        {
            preference = Normalize(preference);
            if (preference != "system") return preference;
            try { return CultureInfo.GetCultureInfo(systemCulture ?? "").TwoLetterISOLanguageName == "ru" ? "ru" : "en"; }
            catch (CultureNotFoundException) { return "en"; }
        }
    }
    public sealed class ControlFile : IDisposable
    {
        private readonly MemoryMappedFile mapping;
        private readonly MemoryMappedViewAccessor view;
        private readonly SecureControl file;
        private readonly bool readOnly;
        private readonly string root;
        private readonly string endpointGuid;
        private static readonly Regex NotificationPattern = new Regex(@"\A([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.notify\z", RegexOptions.CultureInvariant);
        public ControlFile(string root, string guid) : this(root, guid, false) { }
        public ControlFile(string root, string guid, bool readOnly)
        {
            this.root = root;
            this.readOnly = readOnly;
            endpointGuid = new Guid(guid).ToString("D");
            file = SecureControl.Open(root, guid, readOnly);
            try
            {
                var access = readOnly ? MemoryMappedFileAccess.Read : MemoryMappedFileAccess.ReadWrite;
                mapping = MemoryMappedFile.CreateFromFile(file.Stream, null, 64, access, HandleInheritability.None, true);
                view = mapping.CreateViewAccessor(0, 64, access);
            }
            catch { if (mapping != null) mapping.Dispose(); file.Dispose(); throw; }
        }
        public void Set(bool enabled, int attenuation)
        { Set(enabled, attenuation, false); }
        // бит 1 включает срез гула ниже 80 Гц
        public void Set(bool enabled, int attenuation, bool lowCut)
        {
            uint configuration = (uint)((enabled ? 1 : 0) | (lowCut ? 2 : 0) | (Math.Max(0, Math.Min(60, attenuation)) << 8));
            SetConfiguration(configuration);
        }
        public uint Configuration { get { return view.ReadUInt32(8); } }
        public void SetConfiguration(uint configuration)
        {
            if (readOnly) throw new InvalidOperationException("Control-файл открыт только для чтения");
            if (view.ReadUInt32(8) == configuration) return;
            view.Write(8, configuration);
            try
            {
                foreach (string marker in Directory.GetFiles(root, endpointGuid + ".*.notify"))
                {
                    string name = NotificationName(Path.GetFileName(marker));
                    if (name == null) continue;
                    try
                    {
#if NETCOREAPP
                        using (var notification = EventWaitHandleAcl.OpenExisting(name, EventWaitHandleRights.Modify)) notification.Set();
#else
                        using (var notification = EventWaitHandle.OpenExisting(name, EventWaitHandleRights.Modify)) notification.Set();
#endif
                    }
                    catch (WaitHandleCannotBeOpenedException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (FileNotFoundException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        public long Frames { get { return view.ReadInt64(16); } }
        public long ModelFrames { get { return view.ReadInt64(24); } }
        public long Errors { get { return view.ReadInt64(32); } }
        public static string NotificationName(string fileName)
        {
            var match = NotificationPattern.Match(fileName);
            return match.Success ? "Global\\NoiseReduction.Changed." + match.Groups[1].Value + "." + match.Groups[2].Value : null;
        }
        public void Dispose() { view.Dispose(); mapping.Dispose(); file.Dispose(); }
    }
    public static class AttachmentTransaction
    {
        public static void Run(ControlFile control, Action attach, Action verify)
        {
            if ((control.Configuration & 1) == 0) throw new InvalidOperationException("Сначала включите обработку микрофона в окне программы и повторите подключение");
            try
            {
                attach();
                long initial = control.ModelFrames;
                verify();
                if (control.ModelFrames <= initial) throw new InvalidOperationException("Счётчик обработанных моделью кадров не вырос");
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Windows не подтвердил работу эффекта\nПерезапустите запись в использующем микрофон приложении\n" + error.Message, error);
            }
        }
    }
    public sealed class ProbeConfiguration : IDisposable
    {
        private readonly ControlFile control;
        private readonly uint original;
        public ProbeConfiguration(ControlFile control)
        { this.control = control; original = control.Configuration; control.SetConfiguration(original | 1u); }
        public void Dispose() { control.SetConfiguration(original); }
    }
    public sealed class DesktopState
    {
        private readonly HashSet<string> present = new HashSet<string>();
        public string CaptureEndpoint;
        public bool Integrating;
        public string RefreshError;
        public bool CanRefresh { get { return CaptureEndpoint == null && !Integrating; } }
        public bool SetPresent(IEnumerable<string> ids)
        {
            if (!CanRefresh) return false;
            present.Clear();
            foreach (string id in ids) present.Add(id);
            RefreshError = null;
            return true;
        }
        public bool Enabled(string id, bool globalEnabled, bool profileEnabled)
        { return globalEnabled && profileEnabled && present.Contains(id) && CaptureEndpoint != id; }
        public string Status(string activity) { return RefreshError ?? activity; }
    }
    public sealed class Activity
    {
        private long previous;
        private bool sampled;
        private long previousModel;
        private DateTime activeUntil = DateTime.MinValue;
        public bool Active { get { return DateTime.UtcNow < activeUntil; } }
        public bool Update(long frames, long modelFrames)
        { return Update(frames, modelFrames, DateTime.UtcNow); }
        public bool Update(long frames, long modelFrames, DateTime now)
        {
            if (sampled && frames > previous && modelFrames > previousModel) activeUntil = now.AddMilliseconds(1500);
            if (frames < previous || modelFrames < previousModel) activeUntil = DateTime.MinValue;
            previous = frames; previousModel = modelFrames; sampled = true;
            return now < activeUntil;
        }
    }
}
