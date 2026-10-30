import AVFoundation
import AppKit
import CoreAudio
import IOKit.ps

enum AppState { case paused, idle, working, error, needsDriver, noPermission, notSelected }

// как и на Windows, микрофон занимаем только пока им пользуется какая-то программа
final class Controller: ObservableObject {
    // как на Windows: у быстрой модели сила это доля исходного звука, у точной предел самой модели
    static let fastValues = [6, 12, 60], preciseValues = [12, 24, 60]
    static let presetNames = ["Мягко", "Обычно", "Сильно"]

    @Published private(set) var settings = Settings.load()
    @Published private(set) var state = AppState.idle
    @Published private(set) var error: String?
    @Published private(set) var devices: [Bridge.Device] = []
    @Published private(set) var startupEnabled = Autostart.enabled
    @Published private(set) var installing = false
    @Published private(set) var customOpen = Set<String>()
    @Published var hotkeyAvailable = false
    @Published private(set) var users: [NSRunningApplication] = []

    let voice = VoiceTest()
    private let bridge = Bridge()
    private var timer: Timer?
    private var idleSince: Date?
    private var retryAt = Date.distantPast
    private var lastModel: UInt64 = 0, lastErrors: UInt64 = 0
    private var bridgeInput: String?
    private var bridgeDefault: String?
    private var driverCurrent = Driver.isCurrent()
    private var onBattery = Controller.batteryPower()
    private var ticks = 0

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
    func t(_ text: String) -> String { Strings.shared.get(text, language).replacingOccurrences(of: "Ctrl+Alt+N", with: "⌃⌥N") }

    // настройки у каждого настоящего микрофона свои, как на Windows; записи source:default остались от прошлых версий и служат образцом
    func profile(for device: String?) -> Profile {
        let id = "source:" + (device ?? "default")
        if let found = settings.profiles.first(where: { $0.id == id }) { return found }
        var fresh = settings.profiles.first { $0.id == "source:default" } ?? Profile(id: id, enabled: settings.enableNewMicrophones)
        fresh.id = id
        fresh.enabled = settings.enableNewMicrophones
        return fresh
    }

    // микрофон, который сейчас обрабатывается: виртуальный один, поэтому источник тоже один
    var activeDevice: String? { input ?? devices.first { $0.isDefault }?.name }

    var profile: Profile { profile(for: activeDevice) }

    // основной микрофон системы может быть и нашим виртуальным: слушать самих себя нельзя, берём настоящий
    var input: String? {
        if devices.contains(where: { $0.name == settings.source }) { return settings.source }
        return devices.contains { $0.isDefault } ? nil : devices.first?.name
    }

    // те же биты, что и в control-файле на Windows
    var bridgeConfig: UInt32 { config(for: activeDevice) }

    func config(for device: String?) -> UInt32 {
        let value = profile(for: device)
        let deepFilter = value.deepFilter && !(settings.fastOnBattery && onBattery)
        return UInt32((settings.enabled && value.enabled ? 1 : 0) | (value.lowCut ? 2 : 0) | (deepFilter ? 8 : 0) | (min(max(value.attenuation, 0), 60) << 8))
    }

    func values(for device: String?) -> [Int] { profile(for: device).deepFilter ? Self.preciseValues : Self.fastValues }

    func isCustomOpen(_ device: String?) -> Bool { customOpen.contains(device ?? "") }

    func setCustomOpen(_ device: String?, _ value: Bool) {
        if value { customOpen.insert(device ?? "") } else { customOpen.remove(device ?? "") }
    }

    var statusText: String {
        switch state {
        case .working: return t("Работает")
        case .paused: return t("Пауза")
        case .error: return t("Ошибка обработки") + (error.map { ": " + $0 } ?? "")
        case .needsDriver: return t("Нужен драйвер виртуального микрофона")
        case .noPermission: return t("Не удалось открыть микрофон")
        case .idle: return t("Ждёт программ")
        case .notSelected: return t("Noise Reduction не выбран основным микрофоном")
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

    func setProfile(_ device: String?, _ edit: (inout Profile) -> Void) {
        var value = profile(for: device)
        change { settings in
            edit(&value)
            value.attenuation = min(max(value.attenuation, 0), 60)
            if let index = settings.profiles.firstIndex(where: { $0.id == value.id }) { settings.profiles[index] = value } else { settings.profiles.append(value) }
        }
    }

    func setDeepFilter(_ device: String?, _ value: Bool) {
        let current = profile(for: device)
        guard current.deepFilter != value else { return }
        // пресет сохраняется при смене модели: обычно остаётся обычно
        let index = values(for: device).firstIndex(of: current.attenuation)
        let next = value ? Self.preciseValues : Self.fastValues
        setProfile(device) {
            $0.deepFilter = value
            if let index { $0.attenuation = next[index] }
        }
    }

    func setFastOnBattery(_ value: Bool) { change { $0.fastOnBattery = value } }

    func setWindowTheme(_ value: String) { change { $0.windowTheme = value } }

    func setTrayTheme(_ value: String) { change { $0.trayTheme = value } }

    func setEnableNew(_ value: Bool) { change { $0.enableNewMicrophones = value } }

    func isEnabled(source: String?) -> Bool { profile(for: source).enabled }

    func setEnabled(source: String?, _ value: Bool) { setProfile(source) { $0.enabled = value } }

    func copyDiagnostics(_ device: String?) {
        let model = config(for: device) & 8 != 0 ? "DeepFilterNet" : "RNNoise"
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        let lines = [t("Диагностика"), "Noise Reduction " + version, ProcessInfo.processInfo.operatingSystemVersionString, device ?? t("Как в системе"), model, t("Формат движка: 48 kHz"), statusText]
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(lines.joined(separator: "\n"), forType: .string)
    }

    func resetProfile(_ device: String?) {
        setCustomOpen(device, false)
        setProfile(device) {
            $0.attenuation = 12
            $0.lowCut = false
            $0.deepFilter = false
        }
    }

    // драйвер и автозапуск убираем сами, а приложение отправляем в корзину
    func uninstall() {
        guard !installing else { return }
        installing = true
        DispatchQueue.global(qos: .userInitiated).async {
            let failure = Driver.remove()
            DispatchQueue.main.async {
                self.installing = false
                if let failure {
                    self.error = failure
                    return
                }
                Autostart.set(false)
                NSWorkspace.shared.recycle([Bundle.main.bundleURL]) { _, _ in NSApp.terminate(nil) }
            }
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
                self.driverCurrent = Driver.isCurrent()
                self.tick()
            }
        }
    }

    // если система откажет, открываем настройки звука: там можно выбрать вручную
    func selectVirtualMicrophone() {
        if !DefaultInput.selectVirtual() { NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Sound-Settings.extension?input")!) }
        tick()
    }

    func openPrivacySettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")!)
    }

    var needsDriver: Bool { !driverCurrent }

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
        guard bridge.running else { return }
        bridge.configure(bridgeConfig)
    }

    private static func batteryPower() -> Bool {
        guard let info = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(), let type = IOPSGetProvidingPowerSourceType(info)?.takeUnretainedValue() else { return false }
        return type as String == kIOPSBatteryPowerValue
    }

    private func set(_ next: AppState) {
        if next != state { state = next }
    }

    private func tick() {
        ticks += 1
        if ticks % 10 == 0 {
            let battery = Self.batteryPower()
            if battery != onBattery {
                onBattery = battery
                objectWillChange.send()
                apply()
            }
        }
        guard driverCurrent else {
            set(.needsDriver)
            return
        }
        let inUse = nr_bridge_in_use("NoiseReduction_Microphone") == 1
        // после того как программа отпустила микрофон, ещё немного держим мост, чтобы не дёргать устройство
        if inUse { idleSince = nil } else if idleSince == nil { idleSince = Date() }
        let want = inUse || Date().timeIntervalSince(idleSince ?? Date()) < 3
        let apps = inUse ? Self.programsUsingMicrophone() : []
        if apps != users { users = apps }
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
            let input = self.input
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
        } else if !want {
            // программа ушла: прошлая неудача больше ничего не значит, красным висеть незачем
            bridge.stop()
            error = nil
        }
        let stats = bridge.stats()
        if stats.errors > lastErrors && error == nil { error = t("Ошибка обработки") }
        let paused = !settings.enabled || !profile.enabled
        // программа может выбрать наш микрофон и сама, тогда основным ему быть не обязательно
        let working = bridge.running && stats.modelFrames > lastModel
        set(paused ? .paused : error != nil ? .error : working ? .working : inUse || DefaultInput.isVirtual ? .idle : .notSelected)
        lastModel = stats.modelFrames
        lastErrors = stats.errors
    }

    // кто сейчас пишет наш микрофон: система умеет отвечать на это начиная с macOS 14.2
    private static func programsUsingMicrophone() -> [NSRunningApplication] {
        guard #available(macOS 14.2, *) else { return [] }
        func address(_ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
            AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
        }
        func list(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> [AudioObjectID] {
            var where_ = address(selector, scope)
            var size: UInt32 = 0
            guard AudioObjectGetPropertyDataSize(object, &where_, 0, nil, &size) == noErr, size > 0 else { return [] }
            var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
            guard AudioObjectGetPropertyData(object, &where_, 0, nil, &size, &ids) == noErr else { return [] }
            return ids
        }
        var uid = "NoiseReduction_Microphone" as CFString
        var device = AudioObjectID(kAudioObjectUnknown)
        var translate = address(kAudioHardwarePropertyTranslateUIDToDevice)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        let found = withUnsafePointer(to: &uid) { AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &translate, UInt32(MemoryLayout<CFString>.size), $0, &size, &device) }
        guard found == noErr, device != kAudioObjectUnknown else { return [] }
        var seen = Set<pid_t>()
        return list(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyProcessObjectList).compactMap { process in
            guard list(process, kAudioProcessPropertyDevices, kAudioObjectPropertyScopeInput).contains(device) else { return nil }
            var running: UInt32 = 0, pid: pid_t = 0
            var runningAddress = address(kAudioProcessPropertyIsRunningInput), pidAddress = address(kAudioProcessPropertyPID)
            var runningSize = UInt32(MemoryLayout<UInt32>.size), pidSize = UInt32(MemoryLayout<pid_t>.size)
            guard AudioObjectGetPropertyData(process, &runningAddress, 0, nil, &runningSize, &running) == noErr, running != 0,
                  AudioObjectGetPropertyData(process, &pidAddress, 0, nil, &pidSize, &pid) == noErr,
                  pid != ProcessInfo.processInfo.processIdentifier, seen.insert(pid).inserted else { return nil }
            return NSRunningApplication(processIdentifier: pid)
        }
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
