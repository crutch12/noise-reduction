import AppKit
import SwiftUI

final class PickerValues: ObservableObject {
    @Published var language = "system"
    @Published var windowTheme = "system"
    @Published var trayTheme = "system"
}

struct PickerLayoutFixture: View {
    @ObservedObject var values: PickerValues
    var body: some View {
        Card {
            PickerRow(title: "Language", selection: $values.language, options:
                [PickerOption(id: "system", title: "System default"), PickerOption(id: "en", title: "English")]
                + ["中文（简体）", "हिन्दी", "العربية", "Português", "日本語", "한국어", "Bahasa Indonesia", "ไทย", "فارسی"].map { PickerOption(id: $0, title: $0) })
            Divider()
            PickerRow(title: "Window theme", selection: $values.windowTheme, options: themes)
            Divider()
            PickerRow(title: "Tray panel theme", selection: $values.trayTheme, options: themes)
        }
        .padding(16)
    }
    private var themes: [PickerOption] {
        [PickerOption(id: "system", title: "System default"), PickerOption(id: "light", title: "Light"), PickerOption(id: "dark", title: "Dark")]
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 260), styleMask: [.titled], backing: .buffered, defer: false)
let values = PickerValues()
let host = NSHostingView(rootView: PickerLayoutFixture(values: values))
window.contentView = host
host.frame = NSRect(x: 0, y: 0, width: 560, height: 260)
for _ in 0..<10 {
    host.layoutSubtreeIfNeeded()
    RunLoop.main.run(until: Date().addingTimeInterval(0.05))
}
var controls: [NSPopUpButton] = []
func inspect(_ view: NSView) {
    if let picker = view as? NSPopUpButton { controls.append(picker) }
    for child in view.subviews { inspect(child) }
}
inspect(host)
print("native picker count", controls.count)
for picker in controls {
    let frame = picker.convert(picker.bounds, to: host)
    print("picker", picker.titleOfSelectedItem ?? "", NSStringFromRect(frame), "right inset", host.bounds.maxX - frame.maxX)
}
if let bitmap = host.bitmapImageRepForCachingDisplay(in: host.bounds) {
    host.cacheDisplay(in: host.bounds, to: bitmap)
    if let data = bitmap.representation(using: .png, properties: [:]), CommandLine.arguments.count > 1 {
        try data.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
    }
}
guard controls.count == 3 else { print("FAIL: expected three native popup buttons"); exit(1) }
let frames = controls.map { $0.convert($0.bounds, to: host) }
let sameWidths = frames.allSatisfy { abs($0.width - frames[0].width) < 1 }
let alignedEdges = frames.allSatisfy { abs(host.bounds.maxX - $0.maxX - 28) < 3 }
print("equal widths", sameWidths, "aligned with card inset", alignedEdges)
controls[0].selectItem(at: 1)
controls[0].sendAction(controls[0].action!, to: controls[0].target)
let selectedByID = values.language == "en" && values.windowTheme == "system" && values.trayTheme == "system"
values.windowTheme = "dark"
for _ in 0..<4 { host.layoutSubtreeIfNeeded(); RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
let updatedFromBinding = controls[1].titleOfSelectedItem == "Dark"
print("selection uses stable ID", selectedByID, "external binding update", updatedFromBinding)
let selectableItems = controls.allSatisfy { $0.itemArray.allSatisfy(\.isEnabled) }
print("menu options are enabled", selectableItems)
exit(sameWidths && alignedEdges && selectedByID && updatedFromBinding && selectableItems ? 0 : 1)
