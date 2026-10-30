import AVFoundation
import AppKit
import CoreAudio

enum State { case paused, idle, working, error, needsDriver, noPermission }

// как и на Windows, микрофон занимаем только пока им пользуется какая-то программа
final class Controller: ObservableObject {
    // как на Windows: у быстрой модели сила это доля исходного звука, у точной предел самой модели
    static let fastValues = [6, 12, 60], preciseValues = [12, 24, 60]
    static let presetNames = ["Мягко", "Обычно", "Сильно"]

    @Published private(set) var settings = Settings.load()
    @Published private(set) var state = State.idle
    @Published private(set) var error: String?
    @Published private(set) var devices: [Bridge.Device] = []
    @Published private(set) var startupEnabled = Autostart.enabled
    @Published private(set) var installing = false
    @Published var customOpen = false

    private let bridge = Bridge()
    private var timer: Timer?
    private var idleSince: Date?
    private var retryAt = Date.distantPast
    private var lastModel: UInt64 = 0, lastErrors: UInt64 = 0
    private var bridgeInput: String?
    private var bridgeDefault: String?

    init() {
        if startupEnabled { Autostart.set(true) }
        refreshDevices()
        watchDevices()
        tick()
        let timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in self?.tick() }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    var language: String { Strings.shared.resolve(settings.language) }
    func t(_ text: String) -> String { Strings.shared.get(text, language) }

    private var key: String { "source:" + (settings.source ?? "default") }

    var profile: Profile {
        settings.profiles.first { $0.id == key } ?? Profile(id: key, enabled: settings.enableNewMicrophones)
    }

    var values: [Int] { profile.deepFilter ? Self.preciseValues : Self.fastValues }

    var statusText: String {
        switch state {
        case .working: return t("Работает")
        case .paused: return t("Пауза")
        case .error: return t("Ошибка обработки") + (error.map { ": " + $0 } ?? "")
        case .needsDriver: return t("Нужен драйвер виртуального микрофона")
        case .noPermission: return t("Не удалось открыть микрофон")
        case .idle: return t("Ждёт программ")
        }
    }

    func setEnabled(_ value: Bool) { change { $0.enabled = value } }

    func setLanguage(_ value: String) { change { $0.language = value } }

    func setSource(_ value: String?) {
        change { $0.source = value }
        // другой микрофон: мост перезапустится на следующем такте
        bridge.stop()
        tick()
    }

    func setProfile(_ edit: (inout Profile) -> Void) {
        change { settings in
            var value = settings.profiles.first { $0.id == self.key } ?? Profile(id: self.key, enabled: settings.enableNewMicrophones)
            edit(&value)
            value.attenuation = min(max(value.attenuation, 0), 60)
            if let index = settings.profiles.firstIndex(where: { $0.id == value.id }) { settings.profiles[index] = value } else { settings.profiles.append(value) }
        }
    }

    func setDeepFilter(_ value: Bool) {
        guard profile.deepFilter != value else { return }
        // пресет сохраняется при смене модели: обычно остаётся обычно
        let index = values.firstIndex(of: profile.attenuation)
        let next = value ? Self.preciseValues : Self.fastValues
        setProfile {
            $0.deepFilter = value
            if let index { $0.attenuation = next[index] }
        }
    }

    func setStartup(_ value: Bool) {
        Autostart.set(value)
        startupEnabled = Autostart.enabled
    }

    func installDriver() {
        guard !installing else { return }
        installing = true
        DispatchQueue.global(qos: .userInitiated).async {
            let failure = Driver.install()
            DispatchQueue.main.async {
                self.installing = false
                self.error = failure
                self.tick()
            }
        }
    }

    func openPrivacySettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")!)
    }

    func stop() {
        timer?.invalidate()
        bridge.stop()
    }

    private func change(_ edit: (inout Settings) -> Void) {
        edit(&settings)
        settings.save()
        apply()
        tick()
    }

    private func apply() {
        let value = profile
        bridge.configure(enabled: settings.enabled && value.enabled, attenuation: value.attenuation, lowCut: value.lowCut, deepFilter: value.deepFilter)
    }

    private func set(_ next: State) {
        if next != state { state = next }
    }

    private func tick() {
        guard Driver.installed else {
            set(.needsDriver)
            return
        }
        let inUse = nr_bridge_in_use("NoiseReduction_Microphone") == 1
        // после того как программа отпустила микрофон, ещё немного держим мост, чтобы не дёргать устройство
        if inUse { idleSince = nil } else if idleSince == nil { idleSince = Date() }
        let want = inUse || Date().timeIntervalSince(idleSince ?? Date()) < 3
        // без разрешения macOS отдаёт тишину вместо ошибки, и программы молча останутся без звука
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .denied, .restricted:
            bridge.stop()
            set(.noPermission)
            return
        case .notDetermined:
            if want { AVCaptureDevice.requestAccess(for: .audio) { _ in DispatchQueue.main.async { self.tick() } } }
            set(settings.enabled && profile.enabled ? .idle : .paused)
            return
        default: break
        }
        if want && !bridge.running && Date() >= retryAt {
            let input = devices.contains { $0.name == settings.source } ? settings.source : nil
            if bridge.start(input: input, output: "uid:NoiseReduction_Feed") {
                apply()
                error = nil
                lastModel = 0
                lastErrors = 0
                bridgeInput = input
                bridgeDefault = devices.first { $0.isDefault }?.name
            } else {
                error = t("Не удалось открыть микрофон")
                retryAt = Date().addingTimeInterval(5)
            }
        } else if !want && bridge.running {
            bridge.stop()
            error = nil
        }
        let stats = bridge.stats()
        if stats.errors > lastErrors && error == nil { error = t("Ошибка обработки") }
        let paused = !settings.enabled || !profile.enabled
        set(paused ? .paused : error != nil ? .error : bridge.running && stats.modelFrames > lastModel ? .working : .idle)
        lastModel = stats.modelFrames
        lastErrors = stats.errors
    }

    private func refreshDevices() {
        let next = Bridge.inputs()
        if next != devices { devices = next }
    }

    // микрофон выдернули или сменили основной: мост должен перейти на актуальный, иначе программы останутся без звука
    private func devicesChanged() {
        refreshDevices()
        guard bridge.running else { return }
        let gone = bridgeInput.map { input in !devices.contains { $0.name == input } } ?? false
        let moved = bridgeInput == nil && devices.first { $0.isDefault }?.name != bridgeDefault
        if gone || moved {
            bridge.stop()
            tick()
        }
    }

    private func watchDevices() {
        for selector in [kAudioHardwarePropertyDevices, kAudioHardwarePropertyDefaultInputDevice] {
            var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
            AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, .main) { [weak self] _, _ in self?.devicesChanged() }
        }
    }
}
