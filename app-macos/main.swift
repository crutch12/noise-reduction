import AppKit
import Carbon.HIToolbox
import Combine
import SwiftUI

// обработчик клавиши вызывается из C и не может ничего захватить
private var hotkeyPressed: (() -> Void)?

// значок в строке меню: левый клик открывает панель, правый меню; окно настроек отдельно
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private let controller = Controller()
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    private let popover = NSPopover()
    private var window: NSWindow?
    private var observers = Set<AnyCancellable>()
    private var shownGlyph: TrayGlyph?
    private var hotkey: EventHotKeyRef?

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMainMenu()
        popover.behavior = .transient
        popover.contentViewController = hosting(PopoverView(controller: controller, openSettings: { [weak self] in self?.showWindow() }, quit: { NSApp.terminate(nil) }))
        if let button = item.button {
            button.target = self
            button.action = #selector(statusClicked)
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }
        controller.objectWillChange.receive(on: DispatchQueue.main).sink { [weak self] in self?.update() }.store(in: &observers)
        update()
        registerHotkey()
        if !CommandLine.arguments.contains("--tray") || controller.needsDriver { showWindow() }
    }

    // повторный запуск уже открытого приложения показывает настройки
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        showWindow()
        return true
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationWillTerminate(_ notification: Notification) { controller.stop() }

    // значок в Dock и переключение по Cmd+Tab нужны только пока открыто окно
    func windowWillClose(_ notification: Notification) { NSApp.setActivationPolicy(.accessory) }

    private func hosting<Content: View>(_ view: Content) -> NSViewController {
        let host = NSHostingController(rootView: view)
        host.sizingOptions = .preferredContentSize
        return host
    }

    // та же клавиша, что на Windows: на Mac Ctrl+Option+N; разрешений система для неё не спрашивает
    private func registerHotkey() {
        hotkeyPressed = { [weak self] in
            guard let self else { return }
            self.controller.setEnabled(!self.controller.settings.enabled)
        }
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
            hotkeyPressed?()
            return noErr
        }, 1, &spec, nil, nil)
        let id = EventHotKeyID(signature: OSType(0x4E524B59), id: 1)
        controller.hotkeyAvailable = RegisterEventHotKey(UInt32(kVK_ANSI_N), UInt32(controlKey | optionKey), id, GetApplicationEventTarget(), 0, &hotkey) == noErr
    }

    private func appearance(_ theme: String) -> NSAppearance? {
        theme == "light" ? NSAppearance(named: .aqua) : theme == "dark" ? NSAppearance(named: .darkAqua) : nil
    }

    private func update() {
        window?.appearance = appearance(controller.settings.windowTheme)
        popover.appearance = appearance(controller.settings.trayTheme)
        let glyph: TrayGlyph
        switch controller.state {
        case .working, .idle: glyph = .on
        case .paused: glyph = .off
        case .error, .noPermission, .needsDriver: glyph = .problem
        }
        if glyph != shownGlyph {
            shownGlyph = glyph
            item.button?.image = glyph.image
        }
        item.button?.toolTip = "Noise Reduction: " + controller.statusText
    }

    @objc private func statusClicked() {
        if NSApp.currentEvent?.type == .rightMouseUp { showMenu() } else { togglePopover() }
    }

    private func togglePopover() {
        guard let button = item.button else { return }
        if popover.isShown {
            popover.performClose(nil)
        } else {
            NSApp.activate(ignoringOtherApps: true)
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            // без этого панель открывается неактивной, и выключатель выглядит серым
            popover.contentViewController?.view.window?.makeKey()
        }
    }

    private func showMenu() {
        guard let button = item.button else { return }
        let menu = NSMenu()
        let toggle = NSMenuItem(title: controller.t("Шумоподавление"), action: #selector(toggleEnabled), keyEquivalent: "")
        toggle.state = controller.settings.enabled ? .on : .off
        toggle.target = self
        menu.addItem(toggle)
        let settings = NSMenuItem(title: controller.t("Настройки"), action: #selector(openSettings), keyEquivalent: "")
        settings.target = self
        menu.addItem(settings)
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: controller.t("Выход"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: ""))
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: button.bounds.maxY + 4), in: button)
    }

    @objc private func toggleEnabled() { controller.setEnabled(!controller.settings.enabled) }

    @objc private func openSettings() { showWindow() }

    private func showWindow() {
        popover.performClose(nil)
        if window == nil {
            let host = NSHostingController(rootView: SettingsView(controller: controller))
            host.sizingOptions = .preferredContentSize
            if #available(macOS 14, *) { host.sceneBridgingOptions = .all }
            let created = NSWindow(contentViewController: host)
            created.title = "Noise Reduction"
            created.styleMask = [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView]
            // панель инструментов нужна, чтобы боковая панель шла до верха окна, как в системных приложениях
            created.toolbar = NSToolbar()
            created.toolbarStyle = .unified
            created.isReleasedWhenClosed = false
            created.delegate = self
            created.appearance = appearance(controller.settings.windowTheme)
            created.center()
            window = created
        }
        NSApp.setActivationPolicy(.regular)
        window?.makeKeyAndOrderFront(nil)
        // сразу после смены режима система ещё не считает приложение обычным и не отдаёт ему фокус
        DispatchQueue.main.async {
            NSApp.activate(ignoringOtherApps: true)
            self.window?.makeKeyAndOrderFront(nil)
        }
    }

    // без меню приложения не работают Cmd+Q и Cmd+W
    private func buildMainMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(NSMenuItem(title: controller.t("Выход"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        appItem.submenu = appMenu
        main.addItem(appItem)
        let windowItem = NSMenuItem()
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(NSMenuItem(title: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w"))
        windowItem.submenu = windowMenu
        main.addItem(windowItem)
        NSApp.mainMenu = main
    }
}

let delegate = AppDelegate()
NSApplication.shared.delegate = delegate
NSApplication.shared.run()
