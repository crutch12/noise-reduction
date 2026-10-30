import Foundation

// те же названия полей, что в общем Core.cs, настройки старой версии читаются как есть
struct Profile: Codable {
    var id: String
    var enabled = true
    var attenuation = 12
    var lowCut = false
    var economy = false
    var deepFilter = false

    enum CodingKeys: String, CodingKey {
        case id = "Id", enabled = "Enabled", attenuation = "Attenuation", lowCut = "LowCut", economy = "Economy", deepFilter = "DeepFilter"
    }

    init(id: String, enabled: Bool) {
        self.id = id
        self.enabled = enabled
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        id = try values.decode(String.self, forKey: .id)
        enabled = try values.decodeIfPresent(Bool.self, forKey: .enabled) ?? true
        attenuation = try values.decodeIfPresent(Int.self, forKey: .attenuation) ?? 12
        lowCut = try values.decodeIfPresent(Bool.self, forKey: .lowCut) ?? false
        economy = try values.decodeIfPresent(Bool.self, forKey: .economy) ?? false
        deepFilter = try values.decodeIfPresent(Bool.self, forKey: .deepFilter) ?? false
    }
}

struct Settings: Codable {
    var enabled = true
    var language = "system"
    var profiles: [Profile] = []
    var enableNewMicrophones = true
    var source: String?
    var fastOnBattery = false
    var windowTheme = "system"
    var trayTheme = "system"

    enum CodingKeys: String, CodingKey {
        case enabled = "Enabled", language = "Language", profiles = "Profiles", enableNewMicrophones = "EnableNewMicrophones", source = "Source", fastOnBattery = "FastOnBattery", windowTheme = "WindowTheme", trayTheme = "TrayTheme"
    }

    init() {}

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        enabled = try values.decodeIfPresent(Bool.self, forKey: .enabled) ?? true
        language = try values.decodeIfPresent(String.self, forKey: .language) ?? "system"
        profiles = try values.decodeIfPresent([Profile].self, forKey: .profiles) ?? []
        enableNewMicrophones = try values.decodeIfPresent(Bool.self, forKey: .enableNewMicrophones) ?? true
        source = try values.decodeIfPresent(String.self, forKey: .source)
        fastOnBattery = try values.decodeIfPresent(Bool.self, forKey: .fastOnBattery) ?? false
        windowTheme = try values.decodeIfPresent(String.self, forKey: .windowTheme) ?? "system"
        trayTheme = try values.decodeIfPresent(String.self, forKey: .trayTheme) ?? "system"
    }

    static var file: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("NoiseReduction/settings.json")
    }

    static func load() -> Settings {
        guard let data = try? Data(contentsOf: file), let value = try? JSONDecoder().decode(Settings.self, from: data) else { return Settings() }
        return value
    }

    func save() {
        guard let data = try? JSONEncoder().encode(self) else { return }
        try? FileManager.default.createDirectory(at: Self.file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: Self.file, options: .atomic)
    }
}

// ключ перевода это русский текст, как и на других системах
struct Strings {
    struct Language: Decodable {
        let id: String
        let name: String
    }

    private struct Source: Decodable {
        let languages: [Language]
        let texts: [String: [String: String]]
    }

    static let shared = Strings()
    let languages: [Language]
    private let texts: [String: [String: String]]

    private init() {
        let source = Bundle.main.url(forResource: "strings", withExtension: "json")
            .flatMap { try? Data(contentsOf: $0) }
            .flatMap { try? JSONDecoder().decode(Source.self, from: $0) }
        languages = source?.languages ?? []
        texts = source?.texts ?? [:]
    }

    // язык системы без перевода заменяется английским
    func resolve(_ preference: String) -> String {
        if preference != "system" && languages.contains(where: { $0.id == preference }) { return preference }
        let system = Locale.preferredLanguages.first.map { String($0.prefix(2)) } ?? "en"
        return languages.contains(where: { $0.id == system }) ? system : "en"
    }

    func get(_ text: String, _ language: String) -> String {
        if language == "ru" { return text }
        return texts[language]?[text] ?? texts["en"]?[text] ?? text
    }
}

// обёртка над libnr_bridge: реальный микрофон, обработка, виртуальное устройство
final class Bridge {
    struct Device: Hashable {
        let name: String
        let isDefault: Bool
    }

    private var handle: UnsafeMutableRawPointer?
    var running: Bool { handle != nil }

    static func inputs() -> [Device] {
        let size = nr_bridge_list(0, nil, 0)
        var buffer = [CChar](repeating: 0, count: max(Int(size), 1) + 1)
        nr_bridge_list(0, &buffer, Int32(buffer.count))
        return String(cString: buffer).split(separator: "\n").compactMap { line in
            let isDefault = line.hasPrefix("*")
            let name = String(isDefault ? line.dropFirst() : line)
            // свой же виртуальный микрофон источником быть не может
            return name.hasPrefix("Noise Reduction") ? nil : Device(name: name, isDefault: isDefault)
        }
    }

    func start(input: String?, output: String) -> Bool {
        stop()
        handle = nr_bridge_start(input, output, Bundle.main.executableURL!.deletingLastPathComponent().path, 0)
        return running
    }

    func configure(_ config: UInt32) {
        guard let handle else { return }
        nr_bridge_configure(handle, config)
    }

    func stats() -> (modelFrames: UInt64, errors: UInt64) {
        guard let handle else { return (0, 0) }
        var frames: UInt64 = 0, model: UInt64 = 0, errors: UInt64 = 0
        nr_bridge_stats(handle, &frames, &model, &errors)
        return (model, errors)
    }

    func stop() {
        guard let handle else { return }
        nr_bridge_stop(handle)
        self.handle = nil
    }
}

// запуск при входе: LaunchAgent указывает на то место, откуда приложение запущено сейчас
enum Autostart {
    private static var file: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/LaunchAgents/com.github.crutch12.noisereduction.plist")
    }

    static var enabled: Bool { FileManager.default.fileExists(atPath: file.path) }

    static func set(_ value: Bool) {
        guard value else { try? FileManager.default.removeItem(at: file); return }
        let plist: [String: Any] = [
            "Label": "com.github.crutch12.noisereduction",
            "ProgramArguments": [Bundle.main.executablePath!, "--tray"],
            "RunAtLoad": true,
            "ProcessType": "Interactive",
        ]
        guard let data = try? PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0) else { return }
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: file, options: .atomic)
    }
}

// драйвер ставится в системную папку, macOS спросит пароль администратора
enum Driver {
    private static let path = "/Library/Audio/Plug-Ins/HAL/NoiseReduction.driver"
    private static var bundled: URL? { Bundle.main.url(forResource: "NoiseReduction", withExtension: "driver") }

    // другой файл значит старую или повреждённую версию, её тоже нужно заменить
    static func isCurrent() -> Bool {
        guard let installed = FileManager.default.contents(atPath: path + "/Contents/MacOS/NoiseReduction") else { return false }
        guard let bundled, let own = FileManager.default.contents(atPath: bundled.path + "/Contents/MacOS/NoiseReduction") else { return true }
        return installed == own
    }

    static func install() -> String? {
        guard let source = bundled?.path else { return "NoiseReduction.driver not found" }
        // копию проверяем там, куда пишет только root: драйвер с плохой подписью coreaudiod молча не загрузит
        return admin("mkdir -p /Library/Audio/Plug-Ins/HAL && T=$(mktemp -d /Library/Audio/Plug-Ins/.nr-install.XXXXXX) && trap 'rm -rf \"$T\"' EXIT && cp -RH \(quote(source)) \"$T/NoiseReduction.driver\" && codesign --verify --strict \"$T/NoiseReduction.driver\" && xattr -dr com.apple.quarantine \"$T/NoiseReduction.driver\" && rm -rf \(quote(path)) && mv \"$T/NoiseReduction.driver\" \(quote(path)) && killall coreaudiod")
    }

    static func remove() -> String? { admin("rm -rf \(quote(path)); killall coreaudiod") }

    private static func admin(_ script: String) -> String? {
        let escaped = script.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let process = Process()
        let errors = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        process.arguments = ["-e", "do shell script \"\(escaped)\" with administrator privileges"]
        process.standardError = errors
        do { try process.run() } catch { return error.localizedDescription }
        let output = String(data: errors.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        process.waitUntilExit()
        return process.terminationStatus == 0 ? nil : output.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func quote(_ value: String) -> String { "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'" }
}
