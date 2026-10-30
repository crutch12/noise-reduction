import AppKit
import Combine
import SwiftUI

// значок в строке меню: левый клик открывает панель, правый меню; окно настроек отдельно
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private let controller = Controller()
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    private let popover = NSPopover()
    private var window: NSWindow?
    private var observers = Set<AnyCancellable>()

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMainMenu()
        popover.behavior = .transient
        popover.contentViewController = hosting(PopoverView(controller: controller, openSettings: { [weak self] in self?.showWindow() }, quit: { NSApp.terminate(nil) }))
        if let button = item.button {
            button.target = self
            button.action = #selector(statusClicked)
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }
        controller.objectWillChange.receive(on: RunLoop.main).sink { [weak self] in self?.updateIcon() }.store(in: &observers)
        updateIcon()
        if !CommandLine.arguments.contains("--tray") || !Driver.installed { showWindow() }
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

    private func updateIcon() {
        let symbol: String
        switch controller.state {
        case .working: symbol = "mic.fill"
        case .idle: symbol = "mic"
        case .paused: symbol = "mic.slash"
        case .error, .noPermission, .needsDriver: symbol = "exclamationmark.triangle"
        }
        item.button?.image = NSImage(systemSymbolName: symbol, accessibilityDescription: "Noise Reduction")
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
            let created = NSWindow(contentViewController: hosting(SettingsView(controller: controller)))
            created.title = "Noise Reduction"
            created.styleMask = [.titled, .closable, .miniaturizable, .resizable]
            created.isReleasedWhenClosed = false
            created.delegate = self
            created.center()
            window = created
        }
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
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
