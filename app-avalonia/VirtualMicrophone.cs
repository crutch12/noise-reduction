using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NoiseReduction.Avalonia
{

    internal sealed class VirtualMicrophone
    {
        internal const string Name = "Noise Reduction";
        internal const string Alias = "noise_reduction";
        internal const string Feed = "noise_reduction_feed";
        private readonly LinuxAudio audio;
        private readonly LinuxPreferences preferences;
        private readonly Dictionary<uint, PulseModule> owned = new();
        private PulseModule aliasConnection;
        private string aliasTarget;
        internal Dictionary<string, string> Errors { get; } = new(StringComparer.Ordinal);
        internal string AliasError { get; private set; }
        internal bool Dirty { get; private set; }

        internal VirtualMicrophone(LinuxAudio audio, LinuxPreferences preferences)
        {
            this.audio = audio;
            this.preferences = preferences;
            foreach (var module in preferences.Modules) owned[module.Index] = module;
        }

        internal static string SourceFor(string id) => "noise_reduction_" + LinuxAudio.Hash(id);
        internal static string FeedFor(string id) => SourceFor(id) + "_feed";
        internal static bool IsOwn(string name) => name == Alias || name == Feed || name == Feed + ".monitor";

        internal void ForgetSession()
        {
            owned.Clear();
            preferences.Modules.Clear();
            Dirty = true;
            aliasConnection = null;
            aliasTarget = null;
        }

        internal bool MatchesSession(IReadOnlyList<PulseSource> sources, List<PulseModule> modules)
        {
            if (sources.Any(source => LinuxAudio.Value(source.Properties, LinuxAudio.OwnerProperty) == preferences.Owner &&
                LinuxAudio.Value(source.Properties, "noise-reduction.session") == preferences.Session)) return true;
            if (modules.Any(module => module.Arguments.Contains("noise-reduction.owner=" + preferences.Owner, StringComparison.Ordinal) &&
                module.Arguments.Contains("noise-reduction.session=" + preferences.Session, StringComparison.Ordinal))) return true;
            var alias = sources.FirstOrDefault(source => source.Name == Alias);
            return alias?.OwnerModule != null && owned.TryGetValue(alias.OwnerModule.Value, out var sourceModule) && modules.Contains(sourceModule) &&
                HasArgument(sourceModule.Arguments, "source_name", Alias) && HasArgument(sourceModule.Arguments, "master", Feed + ".monitor") &&
                owned.Values.Any(module => module.Name == "module-null-sink" && HasArgument(module.Arguments, "sink_name", Feed) && modules.Contains(module));
        }

        internal bool Owns(PulseSource source) => source != null && (LinuxAudio.Value(source.Properties, LinuxAudio.OwnerProperty) == preferences.Owner ||
            (source.Name == Alias && source.OwnerModule.HasValue && owned.TryGetValue(source.OwnerModule.Value, out var module) &&
                HasArgument(module.Arguments, "source_name", Alias) && HasArgument(module.Arguments, "master", Feed + ".monitor")));

        internal void MarkSaved() => Dirty = false;

        private void Remember(PulseModule module)
        {
            owned[module.Index] = module;
            if (preferences.Modules.Contains(module)) return;
            preferences.Modules.RemoveAll(item => item.Index == module.Index);
            preferences.Modules.Add(module);
            Dirty = true;
        }

        internal async Task PrepareAsync(IEnumerable<Microphone> devices, IReadOnlyList<PulseSource> sources, List<PulseModule> modules, CancellationToken cancellation)
        {
            Errors.Clear();
            AliasError = null;
            foreach (var module in modules.Where(module => module.Arguments.Contains("noise-reduction.owner=" + preferences.Owner, StringComparison.Ordinal))) Remember(module);
            try { await EnsureAsync(Alias, Feed, Name, sources, modules, cancellation); }
            catch (Exception error) when (!LinuxAudio.ConnectionFailure(error)) { AliasError = error.Message; }
            foreach (var device in devices)
            {
                try { await EnsureAsync(SourceFor(device.Id), FeedFor(device.Id), Name + " - " + device.Name, sources, modules, cancellation); }
                catch (Exception error) when (!LinuxAudio.ConnectionFailure(error)) { Errors[device.Id] = error.Message; }
            }
        }

        private async Task EnsureAsync(string sourceName, string feed, string description, IReadOnlyList<PulseSource> sources, List<PulseModule> modules, CancellationToken cancellation)
        {
            var source = sources.FirstOrDefault(source => source.Name == sourceName);
            string marker = "noise-reduction.owner=" + preferences.Owner;
            var feedModule = modules.FirstOrDefault(module => module.Name == "module-null-sink" && HasArgument(module.Arguments, "sink_name", feed));
            if (source != null)
            {
                bool own = LinuxAudio.Value(source.Properties, LinuxAudio.OwnerProperty) == preferences.Owner;
                var sourceModule = modules.FirstOrDefault(module => module.Index == source.OwnerModule);
                bool expectedGraph = sourceModule != null && sourceModule.Name == "module-remap-source" &&
                    HasArgument(sourceModule.Arguments, "master", feed + ".monitor") && HasArgument(sourceModule.Arguments, "source_name", sourceName);
                bool legacy = sourceName == Alias && !source.Properties.ContainsKey(LinuxAudio.OwnerProperty) && expectedGraph && feedModule != null &&
                    !feedModule.Arguments.Contains("noise-reduction.owner=", StringComparison.Ordinal);
                if ((!own && !legacy) || !expectedGraph) throw new IOException("Virtual source name is already used by another owner: " + sourceName);
                bool knownFeed = feedModule != null && (feedModule.Arguments.Contains(marker, StringComparison.Ordinal) ||
                    (owned.TryGetValue(feedModule.Index, out var previousFeed) && previousFeed == feedModule));
                if (own && !knownFeed) throw new IOException("Virtual feed ownership changed: " + feed);
                Remember(sourceModule);
                if (feedModule != null) Remember(feedModule);
                return;
            }
            if (feedModule != null && !feedModule.Arguments.Contains(marker, StringComparison.Ordinal) &&
                !(owned.TryGetValue(feedModule.Index, out var trustedFeed) && trustedFeed == feedModule))
                throw new IOException("Virtual feed name is already used by another owner: " + feed);
            if (feedModule == null)
                await LoadAsync(modules, cancellation, "module-null-sink", "sink_name=" + feed, "channels=1", "rate=48000",
                    Properties("sink", description + " Feed"));
            await LoadAsync(modules, cancellation, "module-remap-source", "master=" + feed + ".monitor", "source_name=" + sourceName,
                "channels=1", "master_channel_map=mono", "channel_map=mono", Properties("source", description));
        }

        internal async Task ConnectAliasAsync(string id, bool demanded, List<PulseModule> modules, CancellationToken cancellation)
        {
            var existing = modules.Where(module => module.Name == "module-loopback" &&
                module.Arguments.Contains("noise-reduction.owner=" + preferences.Owner, StringComparison.Ordinal) && HasArgument(module.Arguments, "sink", Feed)).ToList();
            foreach (var module in existing)
            {
                if (demanded && HasArgument(module.Arguments, "source", SourceFor(id))) { aliasConnection = module; aliasTarget = id; }
                else await UnloadAsync(module, cancellation);
            }
            if (!demanded)
            {
                if (aliasConnection != null && !existing.Any(module => module.Index == aliasConnection.Index)) await UnloadAsync(aliasConnection, cancellation);
                aliasConnection = null;
                aliasTarget = null;
                return;
            }
            if (aliasConnection != null && aliasTarget == id && modules.Any(module => module == aliasConnection)) return;
            if (aliasConnection != null) await UnloadAsync(aliasConnection, cancellation);
            aliasConnection = await LoadAsync(modules, cancellation, "module-loopback", "source=" + SourceFor(id), "sink=" + Feed,
                "latency_msec=20", "source_dont_move=true", "sink_dont_move=true",
                "source_output_properties=\"application.id=" + LinuxAudio.WorkerId + " noise-reduction.owner=" + preferences.Owner + "\"",
                "sink_input_properties=\"application.id=" + LinuxAudio.WorkerId + " noise-reduction.owner=" + preferences.Owner + "\"");
            aliasTarget = id;
        }

        private string Properties(string kind, string description)
        {
            string label = new(description.Select(character => character == '\'' || character == '"' || character == '\\' || Char.IsControl(character) ? ' ' : character).ToArray());
            return kind + "_properties=\"device.description='" + label + "' noise-reduction.owner=" + preferences.Owner + " noise-reduction.session=" + preferences.Session + "\"";
        }

        private async Task<PulseModule> LoadAsync(List<PulseModule> modules, CancellationToken cancellation, string name, params string[] arguments)
        {
            string output = await audio.RequireAsync(cancellation, new[] { "load-module", name }.Concat(arguments).ToArray());
            if (!UInt32.TryParse(output.Trim(), out uint index)) throw new IOException("pactl returned an invalid module identifier");
            // Read the canonical argument string, since servers normalize quoting differently
            var current = LinuxAudio.ParseModules(await audio.ListAsync("modules", cancellation));
            var module = current.FirstOrDefault(module => module.Index == index && module.Name == name)
                ?? throw new IOException("The newly loaded audio module disappeared");
            Remember(module);
            modules.Add(module);
            return module;
        }

        private async Task UnloadAsync(PulseModule module, CancellationToken cancellation)
        {
            var current = LinuxAudio.ParseModules(await audio.ListAsync("modules", cancellation));
            if (current.Any(candidate => candidate.Index == module.Index && candidate.Name == module.Name && candidate.Arguments == module.Arguments))
                await audio.RequireAsync(cancellation, "unload-module", module.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            owned.Remove(module.Index);
            if (preferences.Modules.RemoveAll(item => item == module) > 0) Dirty = true;
        }

        internal async Task ReleaseAsync(CancellationToken cancellation)
        {
            var sources = LinuxAudio.ParseSources(await audio.ListAsync("sources", cancellation));
            bool sourceOwned = MatchesSession(sources, LinuxAudio.ParseModules(await audio.ListAsync("modules", cancellation)));
            foreach (var module in owned.Values.OrderBy(module => module.Name == "module-loopback" ? 0 : module.Name == "module-remap-source" ? 1 : 2).ToList())
                if (sourceOwned || (module.Arguments.Contains("noise-reduction.owner=" + preferences.Owner, StringComparison.Ordinal) &&
                    module.Arguments.Contains("noise-reduction.session=" + preferences.Session, StringComparison.Ordinal)))
                    await UnloadAsync(module, cancellation);
            aliasConnection = null;
            aliasTarget = null;
        }

        private static bool HasArgument(string arguments, string key, string value) => (" " + arguments + " ").Contains(" " + key + "=" + value + " ", StringComparison.Ordinal);
    }
}
