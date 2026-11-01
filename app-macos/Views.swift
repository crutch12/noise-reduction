import SwiftUI

extension View {
    // системный серый на светлом фоне даёт контраст ниже 4.5:1, подсказки должны читаться
    func hintStyle() -> some View { font(.callout).foregroundStyle(Color.primary.opacity(0.72)) }
}

struct MicrophoneUserIcons: View {
    let users: [NSRunningApplication]

    var body: some View {
        HStack(spacing: 4) {
            ForEach(users, id: \.processIdentifier) { app in
                Group {
                    if let icon = app.icon { Image(nsImage: icon).resizable() }
                    else { Image(systemName: "app").resizable() }
                }
                .frame(width: 14, height: 14)
                .help(app.localizedName ?? app.bundleIdentifier ?? "")
                .accessibilityLabel(app.localizedName ?? app.bundleIdentifier ?? "")
            }
        }
    }
}

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
            MicrophoneUserIcons(users: controller.users)
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
        PickerRow(title: controller.t("Исходный микрофон"), selection: Binding(
            get: { controller.devices.contains { $0.name == controller.settings.source } ? controller.settings.source ?? "" : "" },
            set: { controller.setSource($0.isEmpty ? nil : $0) }
        ), options: [PickerOption(id: "", title: controller.t("Как в системе"))]
            + controller.devices.map { PickerOption(id: $0.name, title: $0.name) })
    }
}

struct ModelPicker: View {
    @ObservedObject var controller: Controller
    let device: String

    var body: some View {
        HStack {
            Text(controller.t("Модель"))
            Spacer(minLength: 12)
            Picker(controller.t("Модель"), selection: Binding(
                get: { controller.profile(for: device).deepFilter },
                set: { controller.setDeepFilter(device, $0) }
            )) {
                Text(controller.t("Быстрая")).tag(false)
                Text(controller.t("Точная")).tag(true)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            .help(controller.t("RNNoise, почти не нагружает процессор") + "\n" + controller.t("DeepFilterNet, нагрузка примерно вдвое выше"))
        }
    }
}

struct StrengthPicker: View {
    @ObservedObject var controller: Controller
    let device: String
    private let custom = 3

    private var preset: Int {
        controller.isCustomOpen(device) ? custom : controller.values(for: device).firstIndex(of: controller.profile(for: device).attenuation) ?? custom
    }

    private func text(_ attenuation: Int) -> String {
        attenuation >= 60 ? controller.t("Максимум") : "\(attenuation) " + controller.t("дБ")
    }

    private var value: String { text(controller.profile(for: device).attenuation) }

    // у сегментов нет своих подсказок, поэтому значения всех пресетов показываем вместе
    private var presetsHelp: String {
        zip(Controller.presetNames, controller.values(for: device)).map { controller.t($0) + ": " + text($1) }.joined(separator: "\n")
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
            .frame(maxWidth: .infinity, alignment: .leading)
            .help(presetsHelp)
            if preset == custom {
                Slider(value: Binding(
                    get: { Double(controller.profile(for: device).attenuation) },
                    set: { new in controller.setProfile(device) { $0.attenuation = Int(new.rounded()) } }
                ), in: 0...60, step: 1)
                .labelsHidden()
                .help(controller.t("Сила в децибелах"))
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
                Text(title).lineLimit(1).truncationMode(.middle).help(title)
                Spacer(minLength: 0)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(controller.t("Исходный микрофон")).hintStyle()
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

// стекло только на фоне окна: сквозь него видно то, что за окном, а текст лежит на плотных карточках и читается
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

// плотная карточка: системная форма красит строки полупрозрачно и своей заливки не принимает
struct Card<Content: View>: View {
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 10) { content }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.primary.opacity(0.08)))
    }
}

struct SwitchRow<Label: View>: View {
    let isOn: Binding<Bool>
    @ViewBuilder let label: Label

    var body: some View {
        HStack {
            label
            Spacer(minLength: 12)
            Toggle("", isOn: isOn).toggleStyle(.switch).labelsHidden().controlSize(.small)
        }
    }
}

struct PickerOption {
    let id: String
    let title: String
}

struct NativePicker: NSViewRepresentable {
    let title: String
    @Binding var selection: String
    let options: [PickerOption]

    final class Coordinator: NSObject {
        var selection: Binding<String>
        init(_ selection: Binding<String>) { self.selection = selection }
        @objc func changed(_ sender: NSPopUpButton) {
            if let id = sender.selectedItem?.representedObject as? String { selection.wrappedValue = id }
        }
    }

    func makeCoordinator() -> Coordinator { Coordinator($selection) }

    func makeNSView(context: Context) -> NSPopUpButton {
        let view = NSPopUpButton(frame: .zero, pullsDown: false)
        view.controlSize = .regular
        view.autoenablesItems = false
        view.setContentHuggingPriority(.defaultLow, for: .horizontal)
        view.target = context.coordinator
        view.action = #selector(Coordinator.changed(_:))
        return view
    }

    func updateNSView(_ view: NSPopUpButton, context: Context) {
        context.coordinator.selection = $selection
        if view.itemTitles != options.map(\.title) || view.itemArray.map({ $0.representedObject as? String }) != options.map({ Optional($0.id) }) {
            view.removeAllItems()
            for option in options {
                let item = NSMenuItem(title: option.title, action: nil, keyEquivalent: "")
                item.representedObject = option.id
                view.menu?.addItem(item)
            }
        }
        if let index = options.firstIndex(where: { $0.id == selection }) { view.selectItem(at: index) }
        view.cell?.userInterfaceLayoutDirection = context.environment.layoutDirection == .rightToLeft ? .rightToLeft : .leftToRight
        view.isEnabled = context.environment.isEnabled
        view.setAccessibilityLabel(title)
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: NSPopUpButton, context: Context) -> CGSize? {
        CGSize(width: proposal.width ?? 200, height: nsView.intrinsicContentSize.height)
    }
}

struct PickerRow: View {
    let title: String
    let selection: Binding<String>
    let options: [PickerOption]

    var body: some View {
        HStack {
            Text(title)
            Spacer(minLength: 12)
            NativePicker(title: title, selection: selection, options: options).frame(width: 200, alignment: .trailing)
        }
    }
}

struct SectionTitle: View {
    let text: String

    var body: some View { Text(text).font(.headline).padding(.top, 10).padding(.leading, 12) }
}

// как на Windows: крутится хотя бы полсекунды, иначе не видно, что нажатие сработало
struct RefreshMicrophonesButton: View {
    @ObservedObject var controller: Controller
    @State private var refreshing = false

    var body: some View {
        Button {
            refreshing = true
            controller.refreshMicrophones()
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { refreshing = false }
        } label: {
            ZStack {
                Image(systemName: "arrow.clockwise").opacity(refreshing ? 0 : 1)
                if refreshing { ProgressView().controlSize(.small) }
            }
            .frame(width: 20, height: 20)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(refreshing)
        .help(controller.t("Обновить список микрофонов"))
        .accessibilityLabel(controller.t("Обновить список микрофонов"))
    }
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
        Card {
            HStack {
                Button { withAnimation(.easeInOut(duration: 0.15)) { open.toggle() } } label: {
                    HStack(spacing: 8) {
                        chevron(open)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(device.isDefault ? name + " (" + controller.t("основной") + ")" : name).lineLimit(1).truncationMode(.middle).help(name)
                            HStack(spacing: 6) {
                                Text(controller.t(controller.activeDevice == name ? "Используется" : "Не используется")).hintStyle()
                                MicrophoneUserIcons(users: controller.users(for: name))
                            }
                        }
                        Spacer(minLength: 8)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                Toggle(name, isOn: Binding(get: { controller.isEnabled(source: name) }, set: { controller.setEnabled(source: name, $0) }))
                    .toggleStyle(.switch)
                    .labelsHidden()
                    .controlSize(.small)
            }
            if open {
                Divider()
                VStack(alignment: .leading, spacing: 10) {
                    ModelPicker(controller: controller, device: name)
                    Text(controller.t("Точная лучше убирает клавиатуру и голоса на фоне, но чуть сильнее нагружает процессор")).hintStyle()
                    StrengthPicker(controller: controller, device: name)
                }
                .disabled(busy)
                Divider()
                VoiceTestSection(controller: controller, test: voice, device: name)
                Divider()
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
                    SwitchRow(isOn: Binding(
                        get: { controller.profile(for: name).lowCut },
                        set: { new in controller.setProfile(name) { $0.lowCut = new } }
                    )) { Text(controller.t("Срезать низкий гул")) }
                    .disabled(busy)
                    .help(controller.t("Гул ниже 80 Гц: сеть, вентиляторы"))
                    HStack {
                        Button(controller.t("Сбросить настройки")) { controller.resetProfile(name) }
                            .disabled(busy)
                            .help(controller.t("Быстрая модель и обычная сила, остальное выключено"))
                        Button(controller.t("Скопировать диагностику")) { controller.copyDiagnostics(name) }
                    }
                }
            }
        }
    }
}

// ссылка с подчёркиванием при наведении и адресом в подсказке
struct BuildLink: View {
    let title: String
    let address: String
    @State private var hovering = false

    var body: some View {
        Link(destination: URL(string: address)!) { Text(title).underline(hovering) }
            .help(address)
            .onHover { hovering = $0 }
    }
}

// откуда собрана программа: тег, коммит и исходный код
struct BuildLinks: View {
    @ObservedObject var controller: Controller
    private let repository = "https://github.com/crutch12/noise-reduction"
    private let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    private let commit = Bundle.main.object(forInfoDictionaryKey: "NRCommit") as? String ?? ""

    var body: some View {
        HStack(spacing: 12) {
            BuildLink(title: "v" + version, address: repository + "/releases/tag/v" + version)
            if !commit.isEmpty { BuildLink(title: String(commit.prefix(7)), address: repository + "/commit/" + commit) }
            BuildLink(title: controller.t("Исходный код"), address: repository)
        }
        .font(.callout)
        .frame(maxWidth: .infinity)
    }
}

// одна страница, как на Windows: общий выключатель, микрофоны, приложение
struct SettingsView: View {
    @ObservedObject var controller: Controller
    @State private var confirmUninstall = false

    private var themes: [PickerOption] {
        [PickerOption(id: "system", title: controller.t("Как в системе")),
         PickerOption(id: "light", title: controller.t("Светлая")),
         PickerOption(id: "dark", title: controller.t("Тёмная"))]
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                Card {
                    SwitchRow(isOn: Binding(get: { controller.settings.enabled }, set: { controller.setEnabled($0) })) {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(controller.t("Шумоподавление")).font(.headline)
                            StatusRow(controller: controller)
                        }
                    }
                    .help(controller.t("Для всех микрофонов, клавиша Ctrl+Alt+N"))
                    RequiredAction(controller: controller)
                    Divider()
                    Text(controller.t("Общий выключатель, настройки микрофонов сохраняются") + "\n" + controller.t("В Zoom, Discord и других программах выберите микрофон Noise Reduction")).hintStyle()
                }
                HStack(alignment: .bottom) {
                    SectionTitle(text: controller.t("Микрофоны"))
                    Spacer()
                    RefreshMicrophonesButton(controller: controller).padding(.trailing, 8)
                }
                Card {
                    SourcePicker(controller: controller)
                    if controller.devices.isEmpty { Text(controller.t("Доступные микрофоны не найдены")).hintStyle() }
                    Divider()
                    SwitchRow(isOn: Binding(get: { controller.settings.enableNewMicrophones }, set: { controller.setEnableNew($0) })) {
                        Text(controller.t("Включать шумоподавление для новых микрофонов"))
                    }
                }
                ForEach(controller.devices, id: \.name) { MicrophoneCard(controller: controller, device: $0).id($0.name) }
                SectionTitle(text: controller.t("Приложение"))
                Card {
                    PickerRow(title: controller.t("Язык"), selection: Binding(get: { controller.settings.language }, set: { controller.setLanguage($0) }),
                        options: [PickerOption(id: "system", title: controller.t("Как в системе"))] + Strings.shared.languages.map { PickerOption(id: $0.id, title: $0.name) })
                    Divider()
                    PickerRow(title: controller.t("Тема окна"), selection: Binding(get: { controller.settings.windowTheme }, set: { controller.setWindowTheme($0) }), options: themes)
                    Divider()
                    PickerRow(title: controller.t("Тема панели трея"), selection: Binding(get: { controller.settings.trayTheme }, set: { controller.setTrayTheme($0) }), options: themes)
                }
                Card {
                    SwitchRow(isOn: Binding(get: { controller.startupEnabled }, set: { controller.setStartup($0) })) {
                        Text(controller.t("Запускать при входе в систему"))
                    }
                    Text(controller.t("Закрытое окно остаётся в трее") + (controller.hotkeyAvailable ? "\n" + controller.t("Ctrl+Alt+N включает и выключает шумоподавление") : "")).hintStyle()
                    Divider()
                    SwitchRow(isOn: Binding(get: { controller.settings.fastOnBattery }, set: { controller.setFastOnBattery($0) })) {
                        Text(controller.t("На батарее использовать быструю модель"))
                    }
                    Text(controller.t("Пока ноутбук работает от батареи, вместо точной модели работает быстрая")).hintStyle()
                    Divider()
                    HStack(spacing: 16) {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(controller.t("Удалить программу"))
                            Text(controller.language == "ru"
                                ? "Удалит приложение, виртуальный микрофон и запись автозапуска\nНастройки микрофонов сохранятся"
                                : "Removes the app, virtual microphone and login item\nMicrophone settings are kept").hintStyle()
                        }
                        Spacer(minLength: 12)
                        Button(controller.t("Удалить"), role: .destructive) { confirmUninstall = true }
                            .disabled(controller.installing)
                            .confirmationDialog(controller.t("Удалить программу"), isPresented: $confirmUninstall) {
                                Button(controller.t("Удалить"), role: .destructive) { controller.uninstall() }
                                Button(controller.t("Отмена"), role: .cancel) {}
                            }
                    }
                    if controller.state != .needsDriver, let error = controller.error { Text(error).font(.callout).foregroundStyle(.red) }
                }
            }
            .padding([.horizontal, .top], 16)
            BuildLinks(controller: controller).padding(.top, 4).padding(.bottom, 12)
        }
        .id(controller.windowSession)
        .background(WindowBackground().ignoresSafeArea())
        .frame(minWidth: 520, minHeight: 480)
        .environment(\.layoutDirection, ["ar", "fa"].contains(controller.language) ? .rightToLeft : .leftToRight)
    }
}
