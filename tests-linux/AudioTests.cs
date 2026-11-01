using System.Text.Json;
using NoiseReduction.Avalonia;
using NoiseReduction.Desktop;

namespace NoiseReduction.Linux.Tests
{

    internal static class AudioTests
    {
        private const string Sources = """
        [
          {"index":4,"name":"alsa_input.usb_A.analog-stereo","description":"USB microphone","monitor_of_sink":null,"properties":{"device.serial":"USB_A","device.bus_path":"pci-0000:00:14.0-usb-0:1:1.0","device.class":"sound"}},
          {"index":7,"name":"alsa_input.usb_B.analog-stereo","description":"USB microphone","monitor_of_sink":null,"properties":{"device.serial":"USB_B","device.class":"sound"}},
          {"index":11,"name":"noise_reduction","description":"Noise Reduction","monitor_of_sink":null,"properties":{"noise-reduction.owner":"desktop"}},
          {"index":12,"name":"alsa_output.speaker.monitor","description":"Monitor of speakers","monitor_of_sink":1,"properties":{}},
          {"index":13,"name":"third_party","description":"Noise Reduction enthusiast mic","monitor_of_sink":null,"properties":{"device.class":"sound"}}
        ]
        """;

        [Test]
        internal static void AudioPhysicalIdentitySurvivesRenameAndExcludesOwnedGraph()
        {
            var sources = LinuxAudio.ParseSources(Sources);
            Assert.Equal(3, sources.Count(source => source.Physical));
            var first = sources.Single(source => source.Index == 4);
            var renamed = LinuxAudio.ParseSources(Sources.Replace("USB microphone", "Podcast mic")).Single(source => source.Index == 4);
            Assert.Equal(first.StableId, renamed.StableId);
            Assert.True(first.StableId != sources.Single(source => source.Index == 7).StableId);
            Assert.True(sources.Single(source => source.Index == 13).Physical);
        }

        [Test]
        internal static void AudioSerializedUsbIdentitySurvivesMovingToAnotherPort()
        {
            string first = LinuxAudio.ParseSources(Sources).Single(source => source.Index == 4).StableId;
            string replugged = LinuxAudio.ParseSources(Sources.Replace("usb-0:1:1.0", "usb-0:5:1.0")).Single(source => source.Index == 4).StableId;
            Assert.Equal(first, replugged);
        }

        private const string DuplicateSerialSources = """
        [{"index":4,"name":"alsa_input.usb_Generic_00.analog-stereo","description":"USB microphone","monitor_of_sink":null,"properties":{"device.serial":"Generic_USB","device.profile.name":"analog-stereo","device.bus_path":"pci-usb-port-1","device.class":"sound"}},
         {"index":7,"name":"alsa_input.usb_Generic_01.analog-stereo","description":"USB microphone","monitor_of_sink":null,"properties":{"device.serial":"Generic_USB","device.profile.name":"analog-stereo","device.bus_path":"pci-usb-port-2","device.class":"sound"}}]
        """;

        [Test]
        internal static void AudioDuplicateSerialUsesIndependentPortsAndKeepsAmbiguousProfileUnassigned()
        {
            var preferences = new LinuxPreferences();
            var sources = LinuxAudio.ParseSources(DuplicateSerialSources, preferences.SplitSerials);
            Assert.True(sources[0].StableId != sources[1].StableId);
            using var document = JsonDocument.Parse(DuplicateSerialSources);
            var original = LinuxAudio.ParseSources("[" + document.RootElement[0].GetRawText() + "]")[0];
            var settings = new Settings();
            settings.Profiles.Add(new Profile { Id = original.StableId, Attenuation = 37, DeepFilter = true });
            preferences.RememberDevices(settings, sources, sources[0].Name);
            Assert.Equal(37, settings.For(original.StableId).Attenuation);
            Assert.Equal(12, settings.For(sources[0].StableId).Attenuation);
            Assert.Equal(12, settings.For(sources[1].StableId).Attenuation);
        }

        [Test]
        internal static void AudioDuplicateSerialSplitSurvivesSaveAndOtherMicrophoneUnplug()
        {
            string directory = Path.Combine(Path.GetTempPath(), "nr-identity-" + Guid.NewGuid().ToString("N"));
            try
            {
                var preferences = new LinuxPreferences();
                var together = LinuxAudio.ParseSources(DuplicateSerialSources, preferences.SplitSerials);
                string path = Path.Combine(directory, "linux.json");
                preferences.Save(path);
                var loaded = LinuxPreferences.Load(path);
                Assert.True(loaded.SplitSerials.Count > 0);
                using var document = JsonDocument.Parse(DuplicateSerialSources);
                string second = "[" + document.RootElement[1].GetRawText() + "]";
                var alone = LinuxAudio.ParseSources(second, loaded.SplitSerials);
                Assert.Equal(together[1].StableId, alone[0].StableId);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Test]
        internal static void AudioPulse13TextFallbackIncludesConsumerIdentityAndCork()
        {
            const string text = "Source Output #44\n\tClient: 9\n\tSource: 4\n\tCorked: yes\n\tMute: no\n\tProperties:\n\t\tapplication.name = \"Firefox\"\n\t\tapplication.id = \"org.mozilla.firefox\"\n\t\tapplication.icon_name = \"firefox\"\n";
            var streams = LinuxAudio.ParseStreams(text, LinuxAudio.ParseSources(Sources));
            Assert.Equal(1, streams.Count);
            Assert.Equal("org.mozilla.firefox", streams[0].Id);
            Assert.Equal("firefox", streams[0].Icon);
            Assert.True(streams[0].Corked);
            Assert.Equal("alsa_input.usb_A.analog-stereo", streams[0].SourceName);
            Assert.Equal(9u, streams[0].ClientId);
        }

        [Test]
        internal static void AudioPulse16ModuleJsonWithoutIndexRequiresTextFallback()
        {
            const string json = """
            [{"name":"module-null-sink","argument":"sink_name=noise_reduction_feed channels=1 rate=48000","usage_counter":"1","properties":{"module.version":"16.1"}},
             {"name":"module-remap-source","argument":"master=noise_reduction_feed.monitor source_name=noise_reduction channels=1","usage_counter":"0","properties":{"module.version":"16.1"}}]
            """;
            bool rejected = false;
            try { LinuxAudio.ParseModules(json); } catch (InvalidDataException) { rejected = true; }
            Assert.True(rejected, "Missing server module identifiers must never become module 0");
            const string text = "Module #4\n\tName: module-null-sink\n\tArgument: sink_name=noise_reduction_feed channels=1 rate=48000\n\tUsage counter: 1\n\tProperties:\n\t\tmodule.version = \"16.1\"\nModule #5\n\tName: module-remap-source\n\tArgument: master=noise_reduction_feed.monitor source_name=noise_reduction channels=1\n\tUsage counter: 0\n\tProperties:\n\t\tmodule.version = \"16.1\"\n";
            var modules = LinuxAudio.ParseModules(text);
            Assert.Equal(4u, modules[0].Index);
            Assert.Equal(5u, modules[1].Index);
            Assert.True(modules[1].Arguments.Contains("master=noise_reduction_feed.monitor"));
        }

        [Test]
        internal static void AudioUnknownRecordersRemainVisibleAndOwnedWorkerIsHidden()
        {
            const string streams = """
            [{"index":1,"source":4,"client":6,"corked":false,"mute":true,"properties":{}},
             {"index":2,"source":4,"client":7,"corked":false,"mute":false,"properties":{"application.id":"org.noise-reduction.bridge"}}]
            """;
            var apps = LinuxAudio.ParseStreams(streams, LinuxAudio.ParseSources(Sources));
            Assert.Equal(1, apps.Count);
            Assert.Equal("unknown:6", apps[0].Id);
            Assert.True(apps[0].Muted);
        }

        [Test]
        internal static void AudioOwnedServerRemapStreamsAreHiddenButOtherUnknownRecordersRemain()
        {
            const string sources = """
            [{"index":3,"name":"noise_reduction_feed.monitor","owner_module":4,"monitor_source":"noise_reduction_feed","properties":{"device.class":"monitor"}},
             {"index":4,"name":"noise_reduction","owner_module":5,"monitor_source":"","properties":{"noise-reduction.owner":"desktop","device.master_device":"noise_reduction_feed.monitor","device.class":"filter"}}]
            """;
            const string streams = """
            [{"index":59,"owner_module":5,"source":3,"client":null,"corked":false,"mute":false,"properties":{"media.name":"Remapped Stream"}},
             {"index":61,"owner_module":9,"source":3,"client":null,"corked":false,"mute":false,"properties":{"media.name":"Remapped Stream"}},
             {"index":62,"owner_module":null,"source":4,"client":77,"corked":false,"mute":false,"properties":{}}]
            """;
            var apps = LinuxAudio.ParseStreams(streams, LinuxAudio.ParseSources(sources));
            Assert.Equal(2, apps.Count);
            Assert.True(apps.Any(app => app.StreamId == 61 && app.Id == "unknown:0"));
            Assert.True(apps.Any(app => app.StreamId == 62 && app.Id == "unknown:77"));
            Assert.True(!apps.Any(app => app.StreamId == 59));
        }

        [Test]
        internal static void AudioMigrationDoesNotGuessBetweenDuplicateDisplayNames()
        {
            var settings = new Settings { Source = "USB microphone" };
            settings.Profiles.Add(new Profile { Id = "source:USB microphone", Attenuation = 37, DeepFilter = true });
            var preferences = new LinuxPreferences();
            preferences.RememberDevices(settings, LinuxAudio.ParseSources(Sources), "alsa_input.usb_A.analog-stereo");
            Assert.Equal("USB microphone", settings.Source);
            Assert.True(settings.Has("source:USB microphone"));
            Assert.Equal(37, settings.For("source:USB microphone").Attenuation);
            Assert.Equal(2, preferences.AmbiguousMigrations.Count);
        }

        [Test]
        internal static void AudioUniqueLegacyProfileMigratesAllValuesAndDefaultNeverSelectsSelf()
        {
            var settings = new Settings { Source = "Old microphone" };
            settings.Profiles.Add(new Profile { Id = "source:Old microphone", Enabled = false, Attenuation = 42, LowCut = true, DeepFilter = true });
            var sources = LinuxAudio.ParseSources(Sources.Replace("USB microphone", "Old microphone").Replace("\"USB_B\"", "\"USB_B\""));
            sources.RemoveAll(source => source.Index == 7);
            var preferences = new LinuxPreferences();
            preferences.RememberDevices(settings, sources, "alsa_input.usb_A.analog-stereo");
            var id = sources.Single(source => source.Index == 4).StableId;
            Assert.Equal(id, settings.Source);
            Assert.True(!settings.For(id).Enabled && settings.For(id).LowCut && settings.For(id).DeepFilter);
            Assert.Equal(42, settings.For(id).Attenuation);
            var physical = preferences.SelectPhysical(sources, "noise_reduction", settings.Source);
            Assert.Equal("alsa_input.usb_A.analog-stereo", physical.Name);
        }

        [Test]
        internal static void AudioCorkedConsumersDoNotDemandCaptureAndMultipleOutputsStayIndependent()
        {
            var devices = new[] { new Microphone("a", "A", "input_a", true), new Microphone("b", "B", "input_b", false) };
            var apps = new[] { new RecordingApp(1, "chat", "Chat", "", VirtualMicrophone.SourceFor("a"), false, false), new RecordingApp(2, "chat", "Chat", "", VirtualMicrophone.SourceFor("b"), true, false), new RecordingApp(3, "chat", "Chat", "", "noise_reduction", false, false) };
            var demand = LinuxAudio.Demand(devices, apps, "a");
            Assert.True(demand.Contains("a"));
            Assert.True(!demand.Contains("b"));
            Assert.True(VirtualMicrophone.SourceFor("a") != VirtualMicrophone.SourceFor("b"));
        }

        [Test]
        internal static void AudioOwnershipRejectsReusedStreamAndExternalRouteChanges()
        {
            var route = new StreamOwnership { StreamId = 12, ClientId = 7, AppId = "chat", OriginalSource = "physical", AppliedSource = "noise_reduction", AppliedMuted = true, OriginalMuted = false, RouteChanged = true, MuteChanged = true };
            var current = new RecordingApp(12, "chat", "Chat", "", "noise_reduction", false, true, 7);
            Assert.True(route.CanRestoreRoute(current) && route.CanRestoreMute(current));
            Assert.True(!route.CanRestoreRoute(current with { Id = "other" }));
            Assert.True(!route.CanRestoreRoute(current with { SourceName = "changed_by_user" }));
            Assert.True(!route.CanRestoreMute(current with { Muted = false }));
            Assert.True(!route.CanRestoreRoute(current with { ClientId = 8 }));
            Assert.True(route.CanRestoreMute(current with { SourceName = "changed_by_user" }));
        }

        [Test]
        internal static void AudioBatteryDischargingPackWinsOverAnotherFullPackWithoutAc()
        {
            string directory = Path.Combine(Path.GetTempPath(), "nr-power-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, "BAT0"));
            Directory.CreateDirectory(Path.Combine(directory, "BAT1"));
            try
            {
                foreach (string battery in new[] { "BAT0", "BAT1" }) File.WriteAllText(Path.Combine(directory, battery, "type"), "Battery\n");
                File.WriteAllText(Path.Combine(directory, "BAT0", "status"), "Discharging\n");
                File.WriteAllText(Path.Combine(directory, "BAT1", "status"), "Full\n");
                Assert.Equal<bool?>(true, LinuxPower.Read(directory));
                Directory.CreateDirectory(Path.Combine(directory, "AC"));
                File.WriteAllText(Path.Combine(directory, "AC", "type"), "Mains\n");
                File.WriteAllText(Path.Combine(directory, "AC", "online"), "1\n");
                Assert.Equal<bool?>(false, LinuxPower.Read(directory));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        internal static void AudioBatteryOverrideMapsStandardPresetAndKeepsCustomAttenuation()
        {
            var settings = new Settings { FastOnBattery = true };
            var profile = new Profile { DeepFilter = true, Attenuation = 24 };
            Assert.Equal(1u | (12u << 8), Bridge.Configuration(settings, profile, true));
            Assert.Equal(1u | 8u | (24u << 8), Bridge.Configuration(settings, profile, false));
            profile.Attenuation = 17;
            Assert.Equal(1u | (17u << 8), Bridge.Configuration(settings, profile, true));
            Assert.True(profile.DeepFilter);
            Assert.Equal(17, profile.Attenuation);
        }

        [Test]
        internal static void AudioResetProfilePreservesItsDisabledState()
        {
            var settings = new Settings();
            settings.Profiles.Add(new Profile { Id = "pulse:saved", Enabled = false, Attenuation = 37, LowCut = true, DeepFilter = true });
            LinuxPreferences.ResetProfile(settings, "pulse:saved");
            var profile = settings.For("pulse:saved");
            Assert.True(!profile.Enabled);
            Assert.Equal(12, profile.Attenuation);
            Assert.True(!profile.LowCut && !profile.DeepFilter);
        }

        [Test]
        internal static void AudioStableDeviceUpdatesOnlyItsRawApplicationRulesAfterReplug()
        {
            var preferences = new LinuxPreferences();
            preferences.Devices["pulse:saved"] = new KnownMicrophone { Name = "Mic", SourceName = "old_raw_source" };
            preferences.Rules["chat"] = new AppRule { SourceName = "old_raw_source" };
            preferences.Rules["other"] = new AppRule { SourceName = "unrelated_raw_source" };
            var sources = new List<PulseSource> { new(4, "new_raw_source", "Mic", "pulse:saved", true, null, new()) };
            preferences.RememberDevices(new Settings(), sources, "new_raw_source");
            Assert.Equal("new_raw_source", preferences.Rules["chat"].SourceName);
            Assert.Equal("unrelated_raw_source", preferences.Rules["other"].SourceName);
        }

        [Test]
        internal static void AudioLinuxRulesAndOwnershipSurviveAtomicSave()
        {
            string directory = Path.Combine(Path.GetTempPath(), "nr-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                var preferences = new LinuxPreferences();
                preferences.Rules["org.mozilla.firefox"] = new AppRule { SourceName = "noise_reduction", Muted = true };
                preferences.Ownership.Add(new StreamOwnership { StreamId = 19, ClientId = 7, AppId = "org.mozilla.firefox", Session = preferences.Session, OriginalSource = "physical", AppliedSource = "noise_reduction", RouteChanged = true });
                string path = Path.Combine(directory, "linux.json");
                preferences.Save(path);
                preferences.Rules["org.mozilla.firefox"].Muted = false;
                preferences.Save(path);
                var loaded = LinuxPreferences.Load(path);
                Assert.Equal<bool?>(false, loaded.Rules["org.mozilla.firefox"].Muted);
                Assert.Equal(preferences.Session, loaded.Ownership[0].Session);
                Assert.True(loaded.Ownership[0].RouteChanged);
                Assert.True(!File.Exists(path + ".tmp"));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Test]
        internal static void AudioSaveFailurePersistsUntilEachFailedFileIsSavedSuccessfully()
        {
            string directory = Path.Combine(Path.GetTempPath(), "nr-save-" + Guid.NewGuid().ToString("N"));
            var persistence = new LinuxPersistence(directory);
            var settings = new Settings();
            var preferences = new LinuxPreferences();
            try
            {
                persistence.SaveSettings(settings);
                File.Delete(Path.Combine(directory, "settings.json"));
                Directory.Delete(directory);
                File.WriteAllText(directory, "A file prevents creating the settings directory");
                bool settingsFailed = false, preferencesFailed = false;
                try { persistence.SaveSettings(settings); } catch (IOException) { settingsFailed = true; }
                try { persistence.SavePreferences(preferences); } catch (IOException) { preferencesFailed = true; }
                Assert.True(settingsFailed && preferencesFailed);
                Assert.True(persistence.Error != null, "A successful audio refresh must not clear a failed save");
                File.Delete(directory);
                persistence.SaveSettings(settings);
                Assert.True(persistence.Error != null, "The Linux ownership file is still unsaved");
                persistence.SavePreferences(preferences);
                Assert.Equal<string>(null, persistence.Error);
            }
            finally
            {
                if (File.Exists(directory)) File.Delete(directory);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        internal static void AudioCorruptSettingsArePreservedBeforeDefaultsCanOverwriteThem()
        {
            string directory = Path.Combine(Path.GetTempPath(), "nr-corrupt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                const string settingsContents = "{invalid microphone settings";
                const string preferencesContents = "{invalid ownership journal";
                File.WriteAllText(Path.Combine(directory, "settings.json"), settingsContents);
                File.WriteAllText(Path.Combine(directory, "linux.json"), preferencesContents);
                var persistence = new LinuxPersistence(directory);
                var settings = persistence.LoadSettings();
                var preferences = persistence.LoadPreferences();
                persistence.SaveSettings(settings);
                persistence.SavePreferences(preferences);
                string[] backups = Directory.GetFiles(directory, "*.invalid-*");
                Assert.Equal(2, backups.Length);
                Assert.True(backups.Select(File.ReadAllText).Contains(settingsContents));
                Assert.True(backups.Select(File.ReadAllText).Contains(preferencesContents));
                Assert.True(persistence.RecoveryWarning != null);
                Assert.True(Settings.Load(Path.Combine(directory, "settings.json")).Enabled);
                Assert.True(LinuxPreferences.Load(Path.Combine(directory, "linux.json")).Ownership.Count == 0);
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        internal static async Task AudioMissingPactlExplainsHowToInstallItsDependency()
        {
            string directory = Path.Combine(Path.GetTempPath(), "nr-empty-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string originalPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", directory);
                string message = null;
                try { await LinuxAudio.RunAsync(CancellationToken.None, "info"); }
                catch (Exception error) { message = error.Message; }
                Assert.True(message != null && message.Contains("pulseaudio-utils") && message.Contains("sudo apt install"), "Missing pactl must explain the installable dependency");
                Assert.True(!message.Contains("working directory"), "Dependency guidance must not expose a raw process launch error");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
                Directory.Delete(directory);
            }
        }
    }
}
