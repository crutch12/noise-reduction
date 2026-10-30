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
        // система делает панель слишком прозрачной, текст читается хуже
        .background(Color(nsColor: .windowBackgroundColor).opacity(0.7).ignoresSafeArea())
        .environment(\.layoutDirection, ["ar", "fa"].contains(controller.language) ? .rightToLeft : .leftToRight)
    }
}

// фон окна как в Системных настройках: то, что за окном, лишь слегка просвечивает
struct WindowBackground: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .sidebar
        view.blendingMode = .behindWindow
        view.state = .followsWindowActiveState
        return view
    }

    func updateNSView(_ view: NSVisualEffectView, context: Context) {}
}

struct SettingsView: View {
    @ObservedObject var controller: Controller

    var body: some View {
        Form {
            Section {
                Toggle(isOn: Binding(get: { controller.settings.enabled }, set: { controller.setEnabled($0) })) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(controller.t("Шумоподавление"))
                        StatusRow(controller: controller)
                    }
                }
                RequiredAction(controller: controller)
            } footer: {
                Text(controller.t("В Zoom, Discord и других программах выберите микрофон Noise Reduction")).font(.caption).foregroundStyle(.secondary)
            }
            Section {
                SourcePicker(controller: controller)
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
            Section {
                Picker(controller.t("Язык"), selection: Binding(get: { controller.settings.language }, set: { controller.setLanguage($0) })) {
                    Text(controller.t("Как в системе")).tag("system")
                    ForEach(Strings.shared.languages, id: \.id) { Text($0.name).tag($0.id) }
                }
                Toggle(controller.t("Запускать при входе в систему"), isOn: Binding(get: { controller.startupEnabled }, set: { controller.setStartup($0) }))
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .background(ZStack { WindowBackground(); Color(nsColor: .windowBackgroundColor).opacity(0.82) }.ignoresSafeArea())
        .frame(width: 480)
        .frame(minHeight: 560)
        .environment(\.layoutDirection, ["ar", "fa"].contains(controller.language) ? .rightToLeft : .leftToRight)
    }
}
