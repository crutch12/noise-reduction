import SwiftUI

struct StatusRow: View {
    @ObservedObject var controller: Controller

    private var color: Color {
        switch controller.state {
        case .working, .idle: return .green
        case .paused: return .secondary
        case .error, .noPermission: return .red
        case .needsDriver: return .orange
        }
    }

    var body: some View {
        HStack(spacing: 6) {
            Circle().fill(color).frame(width: 8, height: 8)
            Text(controller.statusText).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            ForEach(controller.users, id: \.processIdentifier) { app in
                if let icon = app.icon { Image(nsImage: icon).resizable().frame(width: 14, height: 14).help(app.localizedName ?? "") }
            }
        }
    }
}

// действие, без которого шумодав не заработает: установка драйвера или разрешение на микрофон
struct RequiredAction: View {
    @ObservedObject var controller: Controller

    var body: some View {
        switch controller.state {
        case .needsDriver:
            HStack {
                Button(controller.t("Установить драйвер")) { controller.installDriver() }
                    .disabled(controller.installing)
                if controller.installing { ProgressView().controlSize(.small) }
            }
            if let error = controller.error { Text(error).font(.caption).foregroundStyle(.red) }
        case .noPermission:
            Button(controller.t("Настройки")) { controller.openPrivacySettings() }
        default:
            EmptyView()
        }
    }
}

struct SourcePicker: View {
    @ObservedObject var controller: Controller

    var body: some View {
        Picker(controller.t("Исходный микрофон"), selection: Binding(
            get: { controller.devices.contains { $0.name == controller.settings.source } ? controller.settings.source ?? "" : "" },
            set: { controller.setSource($0.isEmpty ? nil : $0) }
        )) {
            Text(controller.t("Как в системе")).tag("")
            ForEach(controller.devices, id: \.name) { Text($0.name).tag($0.name) }
        }
    }
}

struct ModelPicker: View {
    @ObservedObject var controller: Controller

    var body: some View {
        Picker(controller.t("Модель"), selection: Binding(
            get: { controller.profile.deepFilter },
            set: { controller.setDeepFilter($0) }
        )) {
            Text(controller.t("Быстрая")).tag(false)
            Text(controller.t("Точная")).tag(true)
        }
        .pickerStyle(.segmented)
    }
}

struct StrengthPicker: View {
    @ObservedObject var controller: Controller
    private let custom = 3

    private var preset: Int {
        controller.customOpen ? custom : controller.values.firstIndex(of: controller.profile.attenuation) ?? custom
    }

    private var value: String {
        let attenuation = controller.profile.attenuation
        return attenuation >= 60 ? controller.t("Максимум") : "\(attenuation) " + controller.t("дБ")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(controller.t("Сила подавления"))
                Spacer()
                Text(value).foregroundStyle(.secondary).monospacedDigit()
            }
            Picker(controller.t("Сила подавления"), selection: Binding(
                get: { preset },
                set: { index in
                    controller.customOpen = index == custom
                    if index != custom { controller.setProfile { $0.attenuation = controller.values[index] } }
                }
            )) {
                ForEach(Controller.presetNames.indices, id: \.self) { Text(controller.t(Controller.presetNames[$0])).tag($0) }
                Text(controller.t("Своё")).tag(custom)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(maxWidth: .infinity)
            if preset == custom {
                Slider(value: Binding(
                    get: { Double(controller.profile.attenuation) },
                    set: { new in controller.setProfile { $0.attenuation = Int(new.rounded()) } }
                ), in: 0...60, step: 1)
                .labelsHidden()
            }
        }
    }
}

// выбор исходного микрофона прямо в панели, как в меню звука macOS
struct MicrophoneList: View {
    @ObservedObject var controller: Controller

    private func row(_ title: String, _ value: String?) -> some View {
        let selected = value == (controller.devices.contains { $0.name == controller.settings.source } ? controller.settings.source : nil)
        return Button { controller.setSource(value) } label: {
            HStack(spacing: 8) {
                Image(systemName: "checkmark").font(.caption.weight(.semibold)).opacity(selected ? 1 : 0)
                Text(title).lineLimit(1).truncationMode(.middle)
                Spacer(minLength: 0)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(controller.t("Исходный микрофон")).font(.caption).foregroundStyle(.secondary)
            row(controller.t("Как в системе"), nil)
            ForEach(controller.devices, id: \.name) { row($0.name, $0.name) }
        }
    }
}

// всплывающая панель из строки меню: включить, выключить и выбрать микрофон, остальное в настройках
struct PopoverView: View {
    @ObservedObject var controller: Controller
    let openSettings: () -> Void
    let quit: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text(controller.t("Шумоподавление")).font(.headline)
                    Spacer()
                    Toggle(controller.t("Шумоподавление"), isOn: Binding(get: { controller.settings.enabled }, set: { controller.setEnabled($0) }))
                        .toggleStyle(.switch)
                        .labelsHidden()
                        .help(controller.t("Для всех микрофонов, клавиша Ctrl+Alt+N"))
                }
                StatusRow(controller: controller)
                RequiredAction(controller: controller)
            }
            .padding(12)
            Divider()
            MicrophoneList(controller: controller).padding(12)
            Divider()
            HStack {
                Button(controller.t("Настройки"), action: openSettings)
                Spacer()
                Button(controller.t("Выход"), action: quit)
            }
            .buttonStyle(.borderless)
            .padding(12)
        }
        .frame(width: 280)
        .environment(\.layoutDirection, ["ar", "fa"].contains(controller.language) ? .rightToLeft : .leftToRight)
    }
}

// как в Системных настройках: стеклянная боковая панель и сплошной контент, стекло система рисует сама
enum SettingsPage: Hashable, CaseIterable {
    case noise, microphones, general

    var title: String {
        switch self {
        case .noise: return "Шумоподавление"
        case .microphones: return "Микрофоны"
        case .general: return "Приложение"
        }
    }

    var symbol: String {
        switch self {
        case .noise: return "waveform"
        case .microphones: return "mic"
        case .general: return "gearshape"
        }
    }
}

struct SettingsView: View {
    @ObservedObject var controller: Controller
    @State private var page: SettingsPage? = .noise
    @State private var confirmUninstall = false

    init(controller: Controller) { self.controller = controller }

    var body: some View {
        NavigationSplitView {
            List(SettingsPage.allCases, id: \.self, selection: $page) { item in
                Label(controller.t(item.title), systemImage: item.symbol)
            }
            .navigationSplitViewColumnWidth(min: 180, ideal: 200, max: 240)
        } detail: {
            Form { content }
                .formStyle(.grouped)
                .navigationTitle(controller.t((page ?? .noise).title))
        }
        .frame(minWidth: 680, idealWidth: 720, minHeight: 480, idealHeight: 540)
        .environment(\.layoutDirection, ["ar", "fa"].contains(controller.language) ? .rightToLeft : .leftToRight)
    }

    @ViewBuilder private var themes: some View {
        Text(controller.t("Как в системе")).tag("system")
        Text(controller.t("Светлая")).tag("light")
        Text(controller.t("Тёмная")).tag("dark")
    }

    @ViewBuilder private var content: some View {
        switch page ?? .noise {
        case .noise:
            Section {
                Toggle(isOn: Binding(get: { controller.settings.enabled }, set: { controller.setEnabled($0) })) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(controller.t("Шумоподавление"))
                        StatusRow(controller: controller)
                    }
                }
                .help(controller.t("Для всех микрофонов, клавиша Ctrl+Alt+N"))
                RequiredAction(controller: controller)
            } footer: {
                Text(controller.t("Общий выключатель, настройки микрофонов сохраняются") + "\n" + controller.t("В Zoom, Discord и других программах выберите микрофон Noise Reduction")).font(.caption).foregroundStyle(.secondary)
            }
            Section {
                ModelPicker(controller: controller)
                StrengthPicker(controller: controller)
                Toggle(controller.t("Срезать низкий гул"), isOn: Binding(
                    get: { controller.profile.lowCut },
                    set: { new in controller.setProfile { $0.lowCut = new } }
                ))
            } footer: {
                Text(controller.t("Точная лучше убирает клавиатуру и голоса на фоне, но чуть сильнее нагружает процессор")).font(.caption).foregroundStyle(.secondary)
            }
            VoiceTestSection(controller: controller)
            Section {
                HStack {
                    Button(controller.t("Сбросить настройки")) { controller.resetProfile() }
                    Button(controller.t("Скопировать диагностику")) { controller.copyDiagnostics() }
                }
            } header: {
                Text(controller.t("Дополнительно"))
            }
        case .microphones:
            Section {
                SourcePicker(controller: controller)
            }
            Section {
                if controller.devices.isEmpty { Text(controller.t("Доступные микрофоны не найдены")).foregroundStyle(.secondary) }
                Toggle(controller.t("Как в системе"), isOn: Binding(get: { controller.isEnabled(source: nil) }, set: { controller.setEnabled(source: nil, $0) }))
                ForEach(controller.devices, id: \.name) { device in
                    Toggle(device.isDefault ? device.name + " (" + controller.t("основной") + ")" : device.name, isOn: Binding(
                        get: { controller.isEnabled(source: device.name) },
                        set: { controller.setEnabled(source: device.name, $0) }
                    ))
                }
            }
            Section {
                Toggle(controller.t("Включать шумоподавление для новых микрофонов"), isOn: Binding(get: { controller.settings.enableNewMicrophones }, set: { controller.setEnableNew($0) }))
            }
        case .general:
            Section {
                Picker(controller.t("Язык"), selection: Binding(get: { controller.settings.language }, set: { controller.setLanguage($0) })) {
                    Text(controller.t("Как в системе")).tag("system")
                    ForEach(Strings.shared.languages, id: \.id) { Text($0.name).tag($0.id) }
                }
                Picker(controller.t("Тема окна"), selection: Binding(get: { controller.settings.windowTheme }, set: { controller.setWindowTheme($0) })) { themes }
                Picker(controller.t("Тема панели трея"), selection: Binding(get: { controller.settings.trayTheme }, set: { controller.setTrayTheme($0) })) { themes }
            }
            Section {
                Toggle(controller.t("Запускать при входе в систему"), isOn: Binding(get: { controller.startupEnabled }, set: { controller.setStartup($0) }))
            } footer: {
                Text(controller.t("Закрытое окно остаётся в трее") + (controller.hotkeyAvailable ? "\n" + controller.t("Ctrl+Alt+N включает и выключает шумоподавление") : "")).font(.caption).foregroundStyle(.secondary)
            }
            Section {
                Toggle(controller.t("На батарее использовать быструю модель"), isOn: Binding(get: { controller.settings.fastOnBattery }, set: { controller.setFastOnBattery($0) }))
            } footer: {
                Text(controller.t("Пока ноутбук работает от батареи, вместо точной модели работает быстрая")).font(.caption).foregroundStyle(.secondary)
            }
            Section {
                Button(controller.t("Удалить программу"), role: .destructive) { confirmUninstall = true }
                    .disabled(controller.installing)
                    .confirmationDialog(controller.t("Удалить программу"), isPresented: $confirmUninstall) {
                        Button(controller.t("Удалить"), role: .destructive) { controller.uninstall() }
                        Button(controller.t("Отмена"), role: .cancel) {}
                    }
                if controller.state != .needsDriver, let error = controller.error { Text(error).font(.caption).foregroundStyle(.red) }
            }
        }
    }
}
