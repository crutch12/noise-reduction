using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NoiseReduction.Desktop;

namespace NoiseReduction.Avalonia
{

    internal sealed class Controller : IDisposable
    {
        private sealed class Pipeline : IDisposable
        {
            internal readonly Bridge Bridge = new();
            internal BridgeMetrics Previous;
            internal DateTime LastProgress, IdleSince, RetryAt;
            internal string SourceName, Error;
            internal int Failures;
            public void Dispose() => Bridge.Dispose();
        }

        private readonly Action<Action> dispatch;
        private readonly LinuxAudio audio = new();
        private readonly LinuxPreferences preferences;
        private readonly LinuxPersistence persistence;
        private readonly VirtualMicrophone graph;
        private readonly Channel<Func<Task>> operations = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
        private readonly CancellationTokenSource lifetime = new();
        private readonly Dictionary<string, Pipeline> pipelines = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> appliedRules = new(StringComparer.Ordinal);
        private readonly List<string> conflicts = new();
        private readonly List<string> ruleErrors = new();
        private readonly object settingsGate = new(), stopGate = new();
        private readonly Task coordinator, timer, watcher;
        private Task stopTask;
        private Microphone[] devices = Array.Empty<Microphone>();
        private RecordingApp[] apps = Array.Empty<RecordingApp>();
        private ApplicationRuleInfo[] rules = Array.Empty<ApplicationRuleInfo>();
        private Dictionary<string, DeviceStatus> statuses = new(StringComparer.Ordinal);
        private List<PulseSource> sources = new();
        private string selectedId, currentDefault, startupError;
        private string diagnostics = "Starting audio coordinator";
        private bool initialized;
        private int refreshPending, stopping;

        internal Settings Settings { get; }
        internal IReadOnlyList<Microphone> Devices => devices;
        internal IReadOnlyList<RecordingApp> Apps => apps;
        internal IReadOnlyList<ApplicationRuleInfo> Rules => rules;
        internal string SelectedDeviceId => selectedId;
        internal bool ProcessedDefault => currentDefault == VirtualMicrophone.Alias || devices.Any(device => OutputFor(device.Id) == currentDefault);
        internal State State { get; private set; } = State.Idle;
        internal string Error { get; private set; }
        internal bool Busy { get; private set; }
        internal bool? OnBattery { get; private set; }
        internal bool RoutingSupported { get; private set; }
        internal event Action Changed;

        internal Controller(Action<Action> dispatch = null)
        {
            this.dispatch = dispatch ?? (action => action());
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoiseReduction");
            persistence = new LinuxPersistence(directory);
            Settings = persistence.LoadSettings();
            UiText.Language = Settings.Language;
            preferences = persistence.LoadPreferences();
            startupError = persistence.RecoveryWarning;
            graph = new VirtualMicrophone(audio, preferences);
            coordinator = Task.Run(CoordinateAsync);
            timer = Task.Run(TimerAsync);
            watcher = Task.Run(() => audio.WatchAsync(SignalRefresh, lifetime.Token));
            SignalRefresh();
        }

        internal Profile ProfileFor(string id)
        {
            lock (settingsGate) return Settings.For(id ?? selectedId ?? "source:default");
        }

        internal DeviceStatus StatusFor(string id) => id != null && statuses.TryGetValue(id, out var status) ? status : new DeviceStatus(State.Idle, "Waiting for audio server", false, default);
        internal IReadOnlyList<RecordingApp> ConsumersFor(string id)
        {
            string physical = devices.FirstOrDefault(device => device.Id == id)?.SourceName;
            string output = OutputFor(id);
            return apps.Where(app => app.SourceName == physical || app.SourceName == output || (id == selectedId && app.SourceName == VirtualMicrophone.Alias)).ToArray();
        }
        internal string OutputFor(string id) => VirtualMicrophone.SourceFor(id);
        internal uint ConfigurationFor(string id)
        {
            lock (settingsGate)
            {
                return Bridge.Configuration(Settings, ProfileFor(id), OnBattery);
            }
        }

        internal void Change(Action change) => Observe(ChangeAsync(change));

        internal Task ChangeAsync(Action change) => Queue(async () =>
        {
            lock (settingsGate) { change(); persistence.SaveSettings(Settings); UiText.Language = Settings.Language; startupError = null; }
            try { await ReconcileAsync(lifetime.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                if (persistence.Error != null) throw;
                Error = error.Message;
                State = State.Error;
            }
        });

        internal void ChangeProfile(string id, Action<Profile> change) => Change(() => change(ProfileFor(id)));
        internal Task ChangeProfileAsync(string id, Action<Profile> change) => ChangeAsync(() => change(ProfileFor(id)));
        internal void ResetProfile(string id) => Change(() =>
        {
            LinuxPreferences.ResetProfile(Settings, id);
        });

        internal void SetSource(string id) => Observe(SetSourceAsync(id));

        internal Task SetSourceAsync(string id) => ChangeAsync(() =>
        {
            if (id != null && !preferences.Devices.ContainsKey(id)) throw new ArgumentException("Select a known physical microphone");
            if (id != null) preferences.MigrateSelection(Settings, id);
            Settings.Source = id;
        });

        internal Task RefreshAsync() => Queue(() => ReconcileAsync(lifetime.Token));

        internal Task MakeDefaultAsync() => Queue(async () =>
        {
            await ReconcileAsync(lifetime.Token);
            if (!RoutingSupported) throw new IOException("Audio server is unavailable");
            string before = await audio.DefaultAsync(lifetime.Token);
            if (before == VirtualMicrophone.Alias) return;
            preferences.OriginalDefault = before;
            preferences.AppliedDefault = VirtualMicrophone.Alias;
            persistence.SavePreferences(preferences);
            await audio.RequireAsync(lifetime.Token, "set-default-source", VirtualMicrophone.Alias);
            await ReconcileAsync(lifetime.Token);
        });

        internal Task RouteAsync(uint streamId, string sourceName, bool remember = false) => Queue(async () =>
        {
            var current = await CurrentAppsAsync(lifetime.Token);
            var app = current.FirstOrDefault(app => app.StreamId == streamId) ?? throw new IOException("The recording stream has closed");
            ValidateTarget(sourceName);
            if (remember) ValidateRuleIdentity(app);
            await MoveAsync(app, sourceName, lifetime.Token);
            if (remember)
            {
                if (!preferences.Rules.TryGetValue(app.Id, out var rule)) preferences.Rules[app.Id] = rule = new();
                rule.SourceName = sourceName;
                rule.Name = app.Name;
                rule.Enabled = true;
                persistence.SavePreferences(preferences);
            }
            await ReconcileAsync(lifetime.Token);
        });

        internal Task MuteAsync(uint streamId, bool muted, bool remember = false) => Queue(async () =>
        {
            var current = await CurrentAppsAsync(lifetime.Token);
            var app = current.FirstOrDefault(app => app.StreamId == streamId) ?? throw new IOException("The recording stream has closed");
            if (remember) ValidateRuleIdentity(app);
            await SetMuteAsync(app, muted, lifetime.Token);
            if (remember)
            {
                if (!preferences.Rules.TryGetValue(app.Id, out var rule)) preferences.Rules[app.Id] = rule = new();
                rule.Muted = muted;
                rule.Name = app.Name;
                rule.Enabled = true;
                persistence.SavePreferences(preferences);
            }
            await ReconcileAsync(lifetime.Token);
        });

        internal Task ClearRulesAsync(string appId) => Queue(async () =>
        {
            preferences.Rules.Remove(appId);
            await RestoreAsync(appId, lifetime.Token);
            foreach (string key in appliedRules.Keys.Where(key => key.EndsWith("|" + appId, StringComparison.Ordinal)).ToList()) appliedRules.Remove(key);
            persistence.SavePreferences(preferences);
            await ReconcileAsync(lifetime.Token);
        });

        internal Task SetRuleEnabledAsync(string appId, bool enabled) => Queue(async () =>
        {
            if (!preferences.Rules.TryGetValue(appId, out var rule)) return;
            rule.Enabled = enabled;
            if (!enabled) await RestoreAsync(appId, lifetime.Token);
            foreach (string key in appliedRules.Keys.Where(key => key.EndsWith("|" + appId, StringComparison.Ordinal)).ToList()) appliedRules.Remove(key);
            persistence.SavePreferences(preferences);
            await ReconcileAsync(lifetime.Token);
        });

        internal Task SetRuleAsync(string appId, string sourceName, bool? muted) => Queue(async () =>
        {
            if (!preferences.Rules.TryGetValue(appId, out var rule)) throw new IOException("The saved application rule no longer exists");
            if (appId.StartsWith("unknown:", StringComparison.Ordinal) || appId.StartsWith("binary:", StringComparison.Ordinal))
                throw new IOException("This application has no unambiguous identity; use a temporary route");
            bool knownTarget = sourceName == null || sourceName == VirtualMicrophone.Alias ||
                preferences.Devices.Any(device => OutputFor(device.Key) == sourceName || device.Value.SourceName == sourceName);
            if (!knownTarget && sourceName != rule.SourceName) throw new IOException("Select a known microphone for the saved rule");
            await RestoreAsync(appId, lifetime.Token);
            rule.SourceName = sourceName;
            rule.Muted = muted;
            foreach (string key in appliedRules.Keys.Where(key => key.EndsWith("|" + appId, StringComparison.Ordinal)).ToList()) appliedRules.Remove(key);
            persistence.SavePreferences(preferences);
            await ReconcileAsync(lifetime.Token);
        });

        private async Task CoordinateAsync()
        {
            await foreach (var operation in operations.Reader.ReadAllAsync()) await operation();
        }

        private Task Queue(Func<Task> operation, bool showBusy = true, bool allowStop = false)
        {
            if (Volatile.Read(ref stopping) != 0 && !allowStop) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!operations.Writer.TryWrite(async () =>
            {
                if (showBusy) { Busy = true; Publish(); }
                try { await operation(); completion.TrySetResult(); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !allowStop) { completion.TrySetCanceled(); }
                catch (Exception error)
                {
                    Error = error.Message;
                    State = State.Error;
                    completion.TrySetException(error);
                }
                finally
                {
                    if (showBusy) Busy = false;
                    rules = preferences.Rules.Select(pair => new ApplicationRuleInfo(pair.Key, pair.Value.Name ?? pair.Key, pair.Value.SourceName, pair.Value.Muted, pair.Value.Enabled)).ToArray();
                    diagnostics = BuildDiagnostics();
                    Publish();
                }
            })) completion.TrySetResult();
            return completion.Task;
        }

        private static async void Observe(Task task)
        {
            try { await task; } catch (Exception) { }
        }

        private void SignalRefresh()
        {
            if (Volatile.Read(ref stopping) != 0 || Interlocked.Exchange(ref refreshPending, 1) != 0) return;
            Observe(Queue(async () =>
            {
                Interlocked.Exchange(ref refreshPending, 0);
                await ReconcileAsync(lifetime.Token);
            }, false));
        }

        private async Task TimerAsync()
        {
            try
            {
                using var ticks = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await ticks.WaitForNextTickAsync(lifetime.Token)) SignalRefresh();
            }
            catch (OperationCanceledException) { }
        }

        private async Task ReconcileAsync(CancellationToken cancellation)
        {
            try
            {
                Task<string> sourceQuery = audio.ListAsync("sources", cancellation);
                Task<string> moduleQuery = audio.ListAsync("modules", cancellation);
                Task<string> defaultQuery = audio.DefaultAsync(cancellation);
                await Task.WhenAll(sourceQuery, moduleQuery, defaultQuery);
                int splitCount = preferences.SplitSerials.Count;
                sources = LinuxAudio.ParseSources(await sourceQuery, preferences.SplitSerials);
                var modules = LinuxAudio.ParseModules(await moduleQuery);
                currentDefault = await defaultQuery;
                selectedId = preferences.SelectPhysical(sources, currentDefault, Settings.Source)?.StableId;
                UpdateDevices();
                bool recoverJournal = false;
                bool sameSession = graph.MatchesSession(sources, modules);
                if (!initialized || !sameSession)
                {
                    if (!sameSession)
                    {
                        foreach (var pipeline in pipelines.Values) { pipeline.Bridge.Stop(); pipeline.Previous = default; }
                        graph.ForgetSession();
                        appliedRules.Clear();
                        preferences.Ownership.Clear();
                        preferences.OriginalDefault = preferences.AppliedDefault = null;
                        preferences.Session = Guid.NewGuid().ToString("N");
                        persistence.SavePreferences(preferences);
                    }
                    else recoverJournal = preferences.Ownership.Count > 0 || preferences.AppliedDefault != null;
                    initialized = true;
                }
                bool save;
                lock (settingsGate)
                {
                    save = preferences.RememberDevices(Settings, sources, currentDefault);
                    if (save) persistence.SaveSettings(Settings);
                }
                if (save || splitCount != preferences.SplitSerials.Count) persistence.SavePreferences(preferences);
                var selected = preferences.SelectPhysical(sources, currentDefault, Settings.Source);
                selectedId = selected?.StableId;
                UpdateDevices();
                OnBattery = LinuxPower.Read();
                await graph.PrepareAsync(devices, sources, modules, cancellation);
                SaveGraphOwnership();
                // A server restart removes our graph; refreshed IDs must never reuse stale route ownership
                var refreshedSources = LinuxAudio.ParseSources(await audio.ListAsync("sources", cancellation), preferences.SplitSerials);
                var aliasSource = refreshedSources.FirstOrDefault(source => source.Name == VirtualMicrophone.Alias);
                bool aliasReady = graph.AliasError == null && graph.Owns(aliasSource);
                string aliasProblem = graph.AliasError ?? (aliasReady ? null : "Virtual alias is unavailable");
                sources = refreshedSources;
                apps = LinuxAudio.ParseStreams(await audio.ListAsync("source-outputs", cancellation), sources).ToArray();
                if (recoverJournal)
                {
                    await RestoreAsync(null, cancellation);
                    await RestoreDefaultAsync(cancellation);
                    persistence.SavePreferences(preferences);
                    apps = LinuxAudio.ParseStreams(await audio.ListAsync("source-outputs", cancellation), sources).ToArray();
                }
                ruleErrors.Clear();
                await ApplyRulesAsync(cancellation);
                apps = LinuxAudio.ParseStreams(await audio.ListAsync("source-outputs", cancellation), sources).ToArray();
                bool aliasDemand = aliasReady && apps.Any(app => app.SourceName == VirtualMicrophone.Alias && !app.Corked);
                try { await graph.ConnectAliasAsync(selectedId, aliasDemand && selectedId != null, modules, cancellation); }
                catch (Exception error) when (!LinuxAudio.ConnectionFailure(error)) { aliasProblem = error.Message; aliasReady = false; }
                SaveGraphOwnership();
                var demand = LinuxAudio.Demand(devices, apps, aliasReady ? selectedId : null);
                var nextStatuses = new Dictionary<string, DeviceStatus>(StringComparer.Ordinal);
                DateTime now = DateTime.UtcNow;
                foreach (var device in devices)
                {
                    if (!pipelines.TryGetValue(device.Id, out var pipeline)) pipelines[device.Id] = pipeline = new Pipeline();
                    graph.Errors.TryGetValue(device.Id, out string endpointError);
                    bool wanted = device.Available && endpointError == null && demand.Contains(device.Id);
                    if (wanted) pipeline.IdleSince = DateTime.MaxValue;
                    else if (pipeline.IdleSince == DateTime.MaxValue) pipeline.IdleSince = now;
                    bool keep = wanted || (device.Available && endpointError == null && pipeline.Bridge.Running && now - pipeline.IdleSince < TimeSpan.FromSeconds(2));
                    if (pipeline.SourceName != device.SourceName || !keep)
                    {
                        if (!device.Available && pipeline.Bridge.Running)
                        {
                            statuses = new Dictionary<string, DeviceStatus>(statuses, StringComparer.Ordinal)
                            {
                                [device.Id] = new DeviceStatus(State.Unavailable, "Microphone disconnected; closing audio transport", false, pipeline.Previous)
                            };
                            State = devices.Any(other => other.Available && StatusFor(other.Id).State == State.Working) ? State.Working : State.Unavailable;
                            diagnostics = BuildDiagnostics();
                            Publish();
                        }
                        pipeline.Bridge.Stop();
                        pipeline.Previous = default;
                        pipeline.SourceName = device.SourceName;
                    }
                    uint config = ConfigurationFor(device.Id);
                    if (keep && !pipeline.Bridge.Running && now >= pipeline.RetryAt)
                    {
                        try
                        {
                            if (!pipeline.Bridge.Start(device.SourceName, VirtualMicrophone.FeedFor(device.Id), config)) throw new IOException("Could not open the microphone transport");
                            pipeline.LastProgress = now;
                            pipeline.Previous = default;
                            pipeline.Error = null;
                        }
                        catch (Exception error)
                        {
                            pipeline.Error = error.Message;
                            pipeline.RetryAt = now.AddSeconds(Math.Min(30, 2 << Math.Min(pipeline.Failures++, 4)));
                        }
                    }
                    BridgeMetrics metrics = default;
                    if (pipeline.Bridge.Running)
                    {
                        try
                        {
                            pipeline.Bridge.Configure(config);
                            metrics = pipeline.Bridge.Snapshot();
                            bool progressed = metrics.CaptureFrames > pipeline.Previous.CaptureFrames && metrics.PlaybackFrames > pipeline.Previous.PlaybackFrames;
                            if (progressed) { pipeline.LastProgress = now; pipeline.Failures = 0; pipeline.Error = null; }
                            if (metrics.Errors > pipeline.Previous.Errors) pipeline.Error = "Audio processing reported an error; original audio is retained";
                            else if (progressed) pipeline.Error = null;
                            if (metrics.Running == 0 || now - pipeline.LastProgress > TimeSpan.FromSeconds(6))
                            {
                                pipeline.Error = "Audio callbacks stopped; reconnecting";
                                pipeline.Bridge.Stop();
                                pipeline.RetryAt = now.AddSeconds(2);
                            }
                            pipeline.Previous = metrics;
                            }
                        catch (Exception error)
                        {
                            pipeline.Error = error.Message;
                            pipeline.Bridge.Stop();
                            pipeline.RetryAt = now.AddSeconds(5);
                            metrics = default;
                        }
                    }
                    bool requested = (config & 1) != 0;
                    bool precise = metrics.ActualModel == 2;
                    State state;
                    string message;
                    if (endpointError != null) { state = State.Error; message = endpointError; }
                    else if (!device.Available) { state = State.Unavailable; message = "Microphone disconnected"; }
                    else if (!keep) { state = requested ? State.Idle : State.Paused; message = "No active recording consumers"; pipeline.Error = null; }
                    else if (!pipeline.Bridge.Running || metrics.CaptureFrames == 0 || metrics.PlaybackFrames == 0) { state = State.Recovering; message = pipeline.Error ?? "Opening microphone transport"; }
                    else if (pipeline.Error != null) { state = State.Error; message = pipeline.Error; }
                    else if (!requested) { state = State.Bypass; message = "Original audio, processing paused"; }
                    else if (metrics.ActualModel == 0) { state = State.Fallback; message = "Model unavailable, original audio"; }
                    else if (ProfileFor(device.Id).DeepFilter && !precise) { state = State.Fallback; message = Settings.FastOnBattery && OnBattery == true ? "RNNoise while on battery" : "RNNoise fallback, DeepFilterNet is unavailable"; }
                    else { state = State.Working; message = precise ? "DeepFilterNet" : "RNNoise"; }
                    if (device.Id == selectedId && Settings.Source != null && Settings.Source != device.Id && state != State.Unavailable && state != State.Error && state != State.Recovering)
                    {
                        state = State.Fallback;
                        message += "; selected microphone is unavailable, alias uses " + device.Name;
                    }
                    nextStatuses[device.Id] = new DeviceStatus(state, message, precise, metrics);
                }
                statuses = nextStatuses;
                RoutingSupported = true;
                Error = startupError ?? persistence.Error ?? aliasProblem ?? ruleErrors.FirstOrDefault() ?? nextStatuses.Values.FirstOrDefault(status => status.State == State.Error)?.Message;
                State = Error != null ? State.Error : nextStatuses.Values.Any(status => status.State == State.Recovering) ? State.Recovering
                    : nextStatuses.Values.Any(status => status.State == State.Working) ? State.Working
                    : nextStatuses.Values.Any(status => status.State == State.Fallback) ? State.Fallback
                    : nextStatuses.Values.Any(status => status.State == State.Bypass) ? State.Bypass
                    : !Settings.Enabled ? State.Paused : devices.Any(device => device.Available) ? State.Idle : State.Unavailable;
            }
            catch (Exception error)
            {
                RoutingSupported = false;
                if (LinuxAudio.ConnectionFailure(error))
                {
                    foreach (var pipeline in pipelines.Values) { pipeline.Bridge.Stop(); pipeline.Previous = default; }
                    statuses = devices.ToDictionary(device => device.Id, device => new DeviceStatus(
                        device.Available ? State.Recovering : State.Unavailable, error.Message, false, default), StringComparer.Ordinal);
                    initialized = false;
                    appliedRules.Clear();
                }
                throw;
            }
        }

        private void UpdateDevices()
        {
            var physical = sources.Where(source => source.Physical).ToDictionary(source => source.StableId, StringComparer.Ordinal);
            var known = new Dictionary<string, KnownMicrophone>(preferences.Devices, StringComparer.Ordinal);
            foreach (var source in physical.Values) known[source.StableId] = new KnownMicrophone { Name = source.Description, SourceName = source.Name };
            devices = known.Select(pair =>
            {
                bool available = physical.TryGetValue(pair.Key, out var source);
                return new Microphone(pair.Key, pair.Value.Name, available ? source.Name : pair.Value.SourceName, source?.Name == currentDefault, available);
            }).OrderBy(device => device.Name, StringComparer.CurrentCulture).ThenBy(device => device.Id, StringComparer.Ordinal).ToArray();
            var names = devices.GroupBy(device => device.Name).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            devices = devices.Select(device => names.Contains(device.Name) ? device with { Name = device.Name + " (" + device.Id[^6..] + ")" } : device).ToArray();
        }

        private async Task<RecordingApp[]> CurrentAppsAsync(CancellationToken cancellation)
        {
            sources = LinuxAudio.ParseSources(await audio.ListAsync("sources", cancellation), preferences.SplitSerials);
            return LinuxAudio.ParseStreams(await audio.ListAsync("source-outputs", cancellation), sources).ToArray();
        }

        private void ValidateTarget(string sourceName)
        {
            if (!TargetAvailable(sourceName) || !sources.Any(source => source.Name == sourceName && (source.Physical || graph.Owns(source))))
                throw new IOException("The selected recording source is unavailable");
            if (sourceName.EndsWith(".monitor", StringComparison.Ordinal)) throw new IOException("A monitor is not a recording microphone");
        }

        private bool TargetAvailable(string sourceName)
        {
            if (sourceName == null || !sources.Any(source => source.Name == sourceName)) return false;
            if (sourceName == VirtualMicrophone.Alias) return selectedId != null;
            var microphone = devices.FirstOrDefault(device => OutputFor(device.Id) == sourceName);
            return microphone == null || microphone.Available;
        }

        private static void ValidateRuleIdentity(RecordingApp app)
        {
            if (app.Id.StartsWith("unknown:", StringComparison.Ordinal) || app.Id.StartsWith("binary:", StringComparison.Ordinal))
                throw new IOException("This application has no unambiguous identity; use a temporary route");
        }

        private StreamOwnership Own(RecordingApp app)
        {
            var ownership = preferences.Ownership.FirstOrDefault(item => item.SameStream(app) && item.Session == preferences.Session);
            if (ownership == null)
            {
                preferences.Ownership.RemoveAll(item => item.StreamId == app.StreamId);
                ownership = new StreamOwnership { StreamId = app.StreamId, ClientId = app.ClientId, AppId = app.Id,
                    OriginalSource = app.SourceName, AppliedSource = app.SourceName, OriginalMuted = app.Muted, AppliedMuted = app.Muted, Session = preferences.Session };
                preferences.Ownership.Add(ownership);
            }
            return ownership;
        }

        private async Task MoveAsync(RecordingApp app, string sourceName, CancellationToken cancellation)
        {
            if (app.SourceName == sourceName) return;
            var ownership = Own(app);
            if (ownership.RouteChanged && app.SourceName != ownership.AppliedSource) ownership.OriginalSource = app.SourceName;
            ownership.AppliedSource = sourceName;
            ownership.RouteChanged = true;
            persistence.SavePreferences(preferences);
            await audio.RequireAsync(cancellation, "move-source-output", app.StreamId.ToString(System.Globalization.CultureInfo.InvariantCulture), sourceName);
        }

        private async Task SetMuteAsync(RecordingApp app, bool muted, CancellationToken cancellation)
        {
            if (app.Muted == muted) return;
            var ownership = Own(app);
            if (ownership.MuteChanged && app.Muted != ownership.AppliedMuted) ownership.OriginalMuted = app.Muted;
            ownership.AppliedMuted = muted;
            ownership.MuteChanged = true;
            persistence.SavePreferences(preferences);
            await audio.RequireAsync(cancellation, "set-source-output-mute", app.StreamId.ToString(System.Globalization.CultureInfo.InvariantCulture), muted ? "1" : "0");
        }

        private async Task ApplyRulesAsync(CancellationToken cancellation)
        {
            var live = apps.Select(RuleKey).ToHashSet(StringComparer.Ordinal);
            foreach (string key in appliedRules.Keys.Where(key => !live.Contains(key)).ToList()) appliedRules.Remove(key);
            bool changed = preferences.Ownership.RemoveAll(item => !apps.Any(item.SameStream)) > 0;
            foreach (var app in apps)
            {
                if (!preferences.Rules.TryGetValue(app.Id, out var rule) || !rule.Enabled) continue;
                string key = RuleKey(app), signature = rule.SourceName + "|" + rule.Muted;
                if (appliedRules.TryGetValue(key, out string applied) && applied == signature)
                {
                    var ownership = preferences.Ownership.FirstOrDefault(item => item.SameStream(app));
                    if (ownership?.RouteChanged == true && app.SourceName != ownership.AppliedSource) AddConflict("Recording route changed externally: " + app.Name);
                    if (ownership?.MuteChanged == true && app.Muted != ownership.AppliedMuted) AddConflict("Recording mute changed externally: " + app.Name);
                    continue;
                }
                if (rule.SourceName != null && !TargetAvailable(rule.SourceName)) continue;
                try
                {
                    if (rule.SourceName != null) { ValidateTarget(rule.SourceName); await MoveAsync(app, rule.SourceName, cancellation); }
                    if (rule.Muted.HasValue) await SetMuteAsync(app, rule.Muted.Value, cancellation);
                    appliedRules[key] = signature;
                }
                catch (Exception error) when (!LinuxAudio.ConnectionFailure(error)) { ruleErrors.Add(app.Name + ": " + error.Message); }
            }
            if (changed) persistence.SavePreferences(preferences);
        }

        private static string RuleKey(RecordingApp app) => app.StreamId + "|" + app.ClientId + "|" + app.Id;

        private void SaveGraphOwnership()
        {
            if (!graph.Dirty) return;
            try { persistence.SavePreferences(preferences); graph.MarkSaved(); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { AddConflict(error.Message); }
        }

        private async Task RestoreAsync(string appId, CancellationToken cancellation)
        {
            var current = await CurrentAppsAsync(cancellation);
            foreach (var ownership in preferences.Ownership.Where(item => appId == null || item.AppId == appId).ToList())
            {
                var app = current.FirstOrDefault(ownership.SameStream);
                if (app != null && ownership.Session == preferences.Session)
                {
                    if (ownership.CanRestoreRoute(app))
                    {
                        string target = ownership.OriginalSource;
                        if (!Restorable(target))
                            target = preferences.SelectPhysical(sources, currentDefault, Settings.Source)?.Name;
                        if (target != null) await audio.RequireAsync(cancellation, "move-source-output", app.StreamId.ToString(System.Globalization.CultureInfo.InvariantCulture), target);
                    }
                    else if (ownership.RouteChanged)
                    {
                        AddConflict("Recording route changed externally: " + app.Name);
                        PreserveRuleConflict(app);
                    }
                    if (ownership.CanRestoreMute(app))
                        await audio.RequireAsync(cancellation, "set-source-output-mute", app.StreamId.ToString(System.Globalization.CultureInfo.InvariantCulture), ownership.OriginalMuted ? "1" : "0");
                    else if (ownership.MuteChanged)
                    {
                        AddConflict("Recording mute changed externally: " + app.Name);
                        PreserveRuleConflict(app);
                    }
                }
                preferences.Ownership.Remove(ownership);
            }
        }

        private void AddConflict(string message)
        {
            if (conflicts.Contains(message)) return;
            conflicts.Add(message);
            if (conflicts.Count > 20) conflicts.RemoveAt(0);
        }

        private void PreserveRuleConflict(RecordingApp app)
        {
            if (preferences.Rules.TryGetValue(app.Id, out var rule)) appliedRules[RuleKey(app)] = rule.SourceName + "|" + rule.Muted;
        }

        private bool Restorable(string name) => name != VirtualMicrophone.Alias && name != VirtualMicrophone.Feed + ".monitor" &&
            sources.Any(source => source.Name == name && LinuxAudio.Value(source.Properties, LinuxAudio.OwnerProperty) != preferences.Owner);

        private async Task RestoreDefaultAsync(CancellationToken cancellation)
        {
            if (preferences.AppliedDefault == null) return;
            string actual = await audio.DefaultAsync(cancellation);
            if (actual == preferences.AppliedDefault)
            {
                string target = preferences.OriginalDefault;
                if (!Restorable(target))
                    target = preferences.SelectPhysical(sources, actual, Settings.Source)?.Name;
                if (target == null) throw new IOException("No physical microphone is available to restore the default source");
                await audio.RequireAsync(cancellation, "set-default-source", target);
            }
            else AddConflict("Default recording source changed externally; preserving the current source");
            preferences.AppliedDefault = preferences.OriginalDefault = null;
        }

        internal string Diagnostics(string id = null) => diagnostics;

        private string BuildDiagnostics()
        {
            var text = new StringBuilder();
            text.AppendLine("Build: " + BuildInfo.Version + "; commit=" + BuildInfo.Commit);
            text.AppendLine("Host: " + RuntimeInformation.OSDescription + "; architecture=" + RuntimeInformation.ProcessArchitecture + "; runtime=" + RuntimeInformation.FrameworkDescription);
            text.AppendLine("Audio: session=" + preferences.Session + "; backend=PulseAudio protocol, compatible with pipewire-pulse");
            text.AppendLine("Bridge: 48000 Hz float32 mono; priming target=60 ms (configured, not measured end-to-end latency); native queue fill=not exposed; coordinator pending=" + (operations.Reader.CanCount ? operations.Reader.Count.ToString() : "not exposed"));
            text.AppendLine("State: " + State + "; error: " + (Error ?? "none"));
            text.AppendLine("Selected: " + (Settings.Source ?? "system") + "; effective: " + (selectedId ?? "none") + "; default: " + (currentDefault ?? "unknown"));
            text.AppendLine("Power: " + (OnBattery.HasValue ? OnBattery.Value ? "battery" : "external" : "unknown") + "; events: " + (audio.WatcherError ?? "connected"));
            foreach (var device in devices)
            {
                var status = StatusFor(device.Id);
                var metrics = status.Metrics;
                text.AppendLine(device.Name + " [" + device.Id + "] " + status.State + ": " + status.Message);
                text.AppendLine("source=" + device.SourceName + "; output=" + OutputFor(device.Id) + "; config=" + ConfigurationFor(device.Id) + "; actual_model=" + metrics.ActualModel + "; native_running=" + metrics.Running);
                text.AppendLine("capture=" + metrics.CaptureFrames + "; model=" + metrics.ModelFrames + "; playback=" + metrics.PlaybackFrames + "; errors=" + metrics.Errors + "; underruns=" + metrics.Underruns + "; drops=" + metrics.DroppedFrames);
            }
            foreach (var app in apps) text.AppendLine("app=" + app.Id + "; stream=" + app.StreamId + "; source=" + app.SourceName + "; corked=" + app.Corked + "; muted=" + app.Muted);
            foreach (var rule in preferences.Rules) text.AppendLine("rule=" + rule.Key + "; source=" + rule.Value.SourceName + "; mute=" + rule.Value.Muted + "; enabled=" + rule.Value.Enabled + "; target_available=" + TargetAvailable(rule.Value.SourceName));
            if (preferences.AmbiguousMigrations.Count > 0) text.AppendLine("Legacy microphone name is ambiguous; select its physical microphone to migrate the saved profile");
            foreach (string conflict in conflicts) text.AppendLine(conflict);
            return text.ToString().TrimEnd();
        }

        internal Task StopAsync()
        {
            lock (stopGate) return stopTask ??= StopCoreAsync();
        }

        private async Task StopCoreAsync()
        {
            Interlocked.Exchange(ref stopping, 1);
            lifetime.Cancel();
            await Queue(async () =>
            {
                foreach (var pipeline in pipelines.Values) pipeline.Dispose();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try
                {
                    var failures = new List<Exception>();
                    try { await RestoreAsync(null, timeout.Token); } catch (Exception error) { failures.Add(error); }
                    try { await RestoreDefaultAsync(timeout.Token); } catch (Exception error) { failures.Add(error); }
                    try { persistence.SavePreferences(preferences); } catch (Exception error) { failures.Add(error); }
                    try { await graph.ReleaseAsync(timeout.Token); } catch (Exception error) { failures.Add(error); }
                    try { persistence.SavePreferences(preferences); } catch (Exception error) { failures.Add(error); }
                    if (failures.Count > 0) throw new AggregateException("Audio shutdown could not restore every owned change", failures);
                }
                finally { operations.Writer.TryComplete(); }
            }, false, true);
            await Task.WhenAll(coordinator, timer, watcher);
            lifetime.Dispose();
        }

        private void Publish() => dispatch(() => Changed?.Invoke());
        public void Dispose() => Observe(StopAsync());
    }
}
