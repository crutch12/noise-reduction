import SwiftUI

struct StatusRow: View {
    @ObservedObject var controller: Controller

    private var color: Color {
        switch controller.state {
        case .working, .idle: return .green
        case .paused: return .secondary
        case .error, .noPermission: return .red
        case .needsDriver, .notSelected: return .orange
        }
    }

    var body: some View {
        HStack(spacing: 6) {
            Circle().fill(color).frame(width: 8, height: 8)
            Text(controller.statusText).font(.callout).fixedSize(horizontal: false, vertical: true)
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
        case .notSelected:
            Button(controller.t("Сделать основным микрофоном")) { controller.selectVirtualMicrophone() }
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
    let device: String

    var body: some View {
        Picker(controller.t("Модель"), selection: Binding(
            get: { controller.profile(for: device).deepFilter },
            set: { controller.setDeepFilter(device, $0) }
        )) {
            Text(controller.t("Быстрая")).tag(false)
            Text(controller.t("Точная")).tag(true)
        }
        .pickerStyle(.segmented)
    }
}

struct StrengthPicker: View {
    @ObservedObject var controller: Controller
    let device: String
    private let custom = 3

    private var preset: Int {
        controller.isCustomOpen(device) ? custom : controller.values(for: device).firstIndex(of: controller.profile(for: device).attenuation) ?? custom
    }

    private var value: String {
        let attenuation = controller.profile(for: device).attenuation
        return attenuation >= 60 ? controller.t("Максимум") : "\(attenuation) " + controller.t("дБ")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(controller.t("Сила подавления"))
                Spacer()
                if preset == custom { Text(value).foregroundStyle(.secondary).monospacedDigit() }
            }
            Picker(controller.t("Сила подавления"), selection: Binding(
                get: { preset },
                set: { index in
                    controller.setCustomOpen(device, index == custom)
                    if index != custom {
                        let next = controller.values(for: device)[index]
                        controller.setProfile(device) { $0.attenuation = next }
                    }
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
                    get: { Double(controller.profile(for: device).attenuation) },
                    set: { new in controller.setProfile(device) { $0.attenuation = Int(new.rounded()) } }
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
            Text(controller.t("Исходный микрофон")).font(.caption.weight(.medium)).foregroundStyle(.secondary)
            row(controller.t("Как в системе"), nil)
            ForEach(controller.devices, id: \.name) { row($0.name, $0.name) }
        }
    }
}

struct MenuMaterial: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .menu
        view.blendingMode = .behindWindow
        view.state = .active
        return view
    }

    func updateNSView(_ view: NSVisualEffectView, context: Context) {}
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
            .buttonStyle(.plain)
            .padding(12)
        }
        .frame(width: 280)
        // материал системных меню плотнее, чем у панели, сквозь него текст читается
        .background(MenuMaterial().ignoresSafeArea())
        .environment(\.layoutDirection, ["ar", "fa"].contains(controller.language) ? .rightToLeft : .leftToRight)
    }
}

// фон окна из системных материалов, сквозь него слегка видно то, что за окном
struct WindowBackground: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .underWindowBackground
        view.blendingMode = .behindWindow
        view.state = .followsWindowActiveState
        return view
    }

    func updateNSView(_ view: NSVisualEffectView, context: Context) {}
}

// карточка микрофона, как на Windows: строка раскрывается по клику в любом месте, выключатель работает отдельно
struct MicrophoneCard: View {
    @ObservedObject var controller: Controller
    @ObservedObject var voice: VoiceTest
    let device: Bridge.Device
    @State private var open: Bool
    @State private var advancedOpen = false

    init(controller: Controller, device: Bridge.Device) {
        self.controller = controller
        voice = controller.voice
        self.device = device
        _open = State(initialValue: controller.activeDevice == device.name)
    }

    private var name: String { device.name }
    private var busy: Bool { voice.recording && voice.owner == name }

    private func chevron(_ expanded: Bool) -> some View {
        Image(systemName: "chevron.right").font(.caption.weight(.semibold)).foregroundStyle(.secondary).rotationEffect(.degrees(expanded ? 90 : 0))
    }

    var body: some View {
        Section {
            HStack {
                Button { withAnimation(.easeInOut(duration: 0.15)) { open.toggle() } } label: {
                    HStack(spacing: 8) {
                        chevron(open)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(device.isDefault ? name + " (" + controller.t("основной") + ")" : name).lineLimit(1).truncationMode(.middle)
                            Text(controller.t(controller.activeDevice == name ? "Используется" : "Не используется")).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer(minLength: 8)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                Toggle(name, isOn: Binding(get: { controller.isEnabled(source: name) }, set: { controller.setEnabled(source: name, $0) }))
                    .toggleStyle(.switch)
                    .labelsHidden()
            }
            if open {
                VStack(alignment: .leading, spacing: 12) {
                    ModelPicker(controller: controller, device: name)
                    Text(controller.t("Точная лучше убирает клавиатуру и голоса на фоне, но чуть сильнее нагружает процессор")).font(.caption).foregroundStyle(.secondary)
                    StrengthPicker(controller: controller, device: name)
                }
                .disabled(busy)
                VoiceTestSection(controller: controller, test: voice, device: name)
                Button { withAnimation(.easeInOut(duration: 0.15)) { advancedOpen.toggle() } } label: {
                    HStack {
                        Text(controller.t("Дополнительно"))
                        Spacer()
                        chevron(advancedOpen)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                if advancedOpen {
                    Toggle(controller.t("Срезать низкий гул"), isOn: Binding(
                        get: { controller.profile(for: name).lowCut },
                        set: { new in controller.setProfile(name) { $0.lowCut = new } }
                    ))
                    .disabled(busy)
                    HStack {
                        Button(controller.t("Сбросить настройки")) { controller.resetProfile(name) }
                            .disabled(busy)
                        Button(controller.t("Скопировать диагностику")) { controller.copyDiagnostics(name) }
                    }
                }
            }
        }
    }
}

// одна страница, как на Windows: общий выключатель, микрофоны, приложение
struct SettingsView: View {
    @ObservedObject var controller: Controller
    @State private var confirmUninstall = false

    @ViewBuilder private var themes: some View {
        Text(controller.t("Как в системе")).tag("system")
        Text(controller.t("Светлая")).tag("light")
        Text(controller.t("Тёмная")).tag("dark")
    }

    var body: some View {
        Form {
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
                SourcePicker(controller: controller)
                if controller.devices.isEmpty { Text(controller.t("Доступные микрофоны не найдены")).foregroundStyle(.secondary) }
            } header: {
                Text(controller.t("Микрофоны"))
            }
            ForEach(controller.devices, id: \.name) { MicrophoneCard(controller: controller, device: $0).id($0.name) }
            Section {
                Toggle(controller.t("Включать шумоподавление для новых микрофонов"), isOn: Binding(get: { controller.settings.enableNewMicrophones }, set: { controller.setEnableNew($0) }))
            }
            Section {
                Picker(controller.t("Язык"), selection: Binding(get: { controller.settings.language }, set: { controller.setLanguage($0) })) {
                    Text(controller.t("Как в системе")).tag("system")
                    ForEach(Strings.shared.languages, id: \.id) { Text($0.name).tag($0.id) }
                }
                Picker(controller.t("Тема окна"), selection: Binding(get: { controller.settings.windowTheme }, set: { controller.setWindowTheme($0) })) { themes }
                Picker(controller.t("Тема панели трея"), selection: Binding(get: { controller.settings.trayTheme }, set: { controller.setTrayTheme($0) })) { themes }
            } header: {
                Text(controller.t("Приложение"))
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
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .background(WindowBackground().ignoresSafeArea())
        .frame(minWidth: 460, minHeight: 420)
        .environment(\.layoutDirection, ["ar", "fa"].contains(controller.language) ? .rightToLeft : .leftToRight)
    }
}
