using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NoiseReduction.Desktop;

namespace NoiseReduction.Avalonia
{

    internal sealed class KnownMicrophone
    {
        public string Name, SourceName;
    }

    internal sealed class AppRule
    {
        public string Name, SourceName;
        public bool? Muted;
        public bool Enabled = true;
    }

    internal sealed record ApplicationRuleInfo(string Id, string Name, string SourceName, bool? Muted, bool Enabled);

    internal sealed class StreamOwnership
    {
        public uint StreamId, ClientId;
        public string AppId, OriginalSource, AppliedSource, Session;
        public bool OriginalMuted, AppliedMuted, RouteChanged, MuteChanged;
        internal bool SameStream(RecordingApp app) => app.StreamId == StreamId && app.ClientId == ClientId && app.Id == AppId;
        internal bool CanRestoreRoute(RecordingApp app) => SameStream(app) && RouteChanged && app.SourceName == AppliedSource;
        internal bool CanRestoreMute(RecordingApp app) => SameStream(app) && MuteChanged && app.Muted == AppliedMuted;
    }

    internal sealed class LinuxPreferences
    {
        public Dictionary<string, KnownMicrophone> Devices = new(StringComparer.Ordinal);
        public Dictionary<string, AppRule> Rules = new(StringComparer.Ordinal);
        public List<StreamOwnership> Ownership = new();
        public List<PulseModule> Modules = new();
        public HashSet<string> SplitSerials = new(StringComparer.Ordinal);
        public string Owner = Guid.NewGuid().ToString("N");
        public string Session = Guid.NewGuid().ToString("N");
        public string LastPhysicalSource, OriginalDefault, AppliedDefault;
        [JsonIgnore] public List<string> AmbiguousMigrations = new();

        internal static LinuxPreferences Load(string path)
        {
            if (!File.Exists(path)) return new();
            var preferences = JsonSerializer.Deserialize(File.ReadAllText(path), LinuxJsonContext.Default.LinuxPreferences);
            if (preferences == null || preferences.Devices == null || preferences.Rules == null || preferences.Ownership == null || preferences.Modules == null || preferences.SplitSerials == null ||
                !Guid.TryParseExact(preferences.Owner, "N", out _) || !Guid.TryParseExact(preferences.Session, "N", out _) ||
                preferences.Devices.Any(pair => String.IsNullOrEmpty(pair.Key) || pair.Value == null) ||
                preferences.Rules.Any(pair => String.IsNullOrEmpty(pair.Key) || pair.Value == null) || preferences.Ownership.Any(item => item == null) || preferences.Modules.Any(item => item == null))
                throw new InvalidDataException("Linux audio settings are incomplete");
            return preferences;
        }

        internal void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this, LinuxJsonContext.Default.LinuxPreferences);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }

        internal bool RememberDevices(Settings settings, List<PulseSource> sources, string physicalDefault)
        {
            bool changed = false;
            if (settings.Source == "source:default") { settings.Source = null; changed = true; }
            AmbiguousMigrations.Clear();
            var physical = sources.Where(source => source.Physical).ToList();
            if (physical.Any(source => source.Name == physicalDefault) && LastPhysicalSource != physicalDefault)
            {
                LastPhysicalSource = physicalDefault;
                changed = true;
            }
            foreach (var source in physical)
            {
                string legacy = "source:" + source.Description;
                var old = settings.Profiles.FirstOrDefault(profile => profile.Id == legacy);
                if (old != null && physical.Count(candidate => candidate.Description == source.Description) > 1)
                    AmbiguousMigrations.Add(source.StableId);
                else if (old != null && !settings.Has(source.StableId))
                {
                    settings.Profiles.Add(Copy(old, source.StableId));
                    settings.Profiles.Remove(old);
                    if (settings.Source == source.Description || settings.Source == legacy) settings.Source = source.StableId;
                    changed = true;
                }
                if (settings.Source == source.Name || (settings.Source == source.Description && physical.Count(candidate => candidate.Description == source.Description) == 1))
                {
                    settings.Source = source.StableId;
                    changed = true;
                }
                if (!settings.Has(source.StableId)) { settings.For(source.StableId); changed = true; }
                if (!Devices.TryGetValue(source.StableId, out var known) || known.Name != source.Description || known.SourceName != source.Name)
                {
                    if (known != null && known.SourceName != source.Name)
                        foreach (var rule in Rules.Values.Where(rule => rule.SourceName == known.SourceName)) rule.SourceName = source.Name;
                    Devices[source.StableId] = new KnownMicrophone { Name = source.Description, SourceName = source.Name };
                    changed = true;
                }
            }
            var oldDefault = settings.Profiles.FirstOrDefault(profile => profile.Id == "source:default");
            var selected = SelectPhysical(sources, physicalDefault, settings.Source);
            if (oldDefault != null && settings.Source == null && selected != null)
            {
                settings.Profiles.RemoveAll(profile => profile.Id == selected.StableId);
                settings.Profiles.Add(Copy(oldDefault, selected.StableId));
                settings.Profiles.Remove(oldDefault);
                changed = true;
            }
            return changed;
        }

        internal void MigrateSelection(Settings settings, string id)
        {
            if (settings.Source == null || settings.Source.StartsWith("pulse:", StringComparison.Ordinal)) return;
            string oldKey = settings.Source.StartsWith("source:", StringComparison.Ordinal) ? settings.Source : "source:" + settings.Source;
            var legacy = settings.Profiles.FirstOrDefault(profile => profile.Id == oldKey);
            if (legacy == null) return;
            settings.Profiles.RemoveAll(profile => profile.Id == id);
            settings.Profiles.Add(Copy(legacy, id));
            settings.Profiles.Remove(legacy);
        }

        internal PulseSource SelectPhysical(List<PulseSource> sources, string systemDefault, string selected)
        {
            var physical = sources.Where(source => source.Physical).ToList();
            return physical.FirstOrDefault(source => source.StableId == selected)
                ?? physical.FirstOrDefault(source => source.Name == systemDefault)
                ?? physical.FirstOrDefault(source => source.Name == LastPhysicalSource)
                ?? physical.OrderBy(source => source.StableId, StringComparer.Ordinal).FirstOrDefault();
        }

        private static Profile Copy(Profile profile, string id) => new()
        {
            Id = id, Enabled = profile.Enabled, Attenuation = profile.Attenuation,
            LowCut = profile.LowCut, Economy = profile.Economy, DeepFilter = profile.DeepFilter
        };

        internal static void ResetProfile(Settings settings, string id)
        {
            var profile = settings.For(id);
            profile.Attenuation = 12;
            profile.LowCut = profile.DeepFilter = profile.Economy = false;
        }
    }

    [JsonSourceGenerationOptions(IncludeFields = true, WriteIndented = true)]
    [JsonSerializable(typeof(LinuxPreferences))]
    internal partial class LinuxJsonContext : JsonSerializerContext { }
}
