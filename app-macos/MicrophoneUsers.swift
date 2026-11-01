import AppKit
import CoreAudio

enum MicrophoneUsers {
    static func deviceNames(_ ids: [AudioObjectID], physical: [AudioObjectID: String], virtual: AudioObjectID, selected: String?) -> Set<String> {
        Set(ids.compactMap { id in id == virtual ? selected : physical[id] })
    }

    static func snapshot(devices: [String], selected: String?) -> [String: [NSRunningApplication]] {
        guard #available(macOS 14.2, *) else { return [:] }
        let system = AudioObjectID(kAudioObjectSystemObject)
        var physical: [AudioObjectID: String] = [:]
        var virtual = AudioObjectID(kAudioObjectUnknown)
        for device in list(system, kAudioHardwarePropertyDevices) {
            if string(device, kAudioDevicePropertyDeviceUID) == "NoiseReduction_Microphone" { virtual = device }
            let name = string(device, kAudioObjectPropertyName)
            if devices.contains(name) { physical[device] = name }
        }
        var result: [String: [NSRunningApplication]] = [:]
        for process in list(system, kAudioHardwarePropertyProcessObjectList) {
            var running: UInt32 = 0, pid: pid_t = 0
            var runningAddress = address(kAudioProcessPropertyIsRunningInput), pidAddress = address(kAudioProcessPropertyPID)
            var runningSize = UInt32(MemoryLayout<UInt32>.size), pidSize = UInt32(MemoryLayout<pid_t>.size)
            guard AudioObjectGetPropertyData(process, &runningAddress, 0, nil, &runningSize, &running) == noErr, running != 0,
                  AudioObjectGetPropertyData(process, &pidAddress, 0, nil, &pidSize, &pid) == noErr,
                  pid != ProcessInfo.processInfo.processIdentifier else { continue }
            let names = deviceNames(list(process, kAudioProcessPropertyDevices, kAudioObjectPropertyScopeInput), physical: physical, virtual: virtual, selected: selected)
            guard !names.isEmpty else { continue }
            let app = NSRunningApplication(processIdentifier: pid)
                ?? NSRunningApplication.runningApplications(withBundleIdentifier: string(process, kAudioProcessPropertyBundleID)).first
            guard let app = app else { continue }
            for name in names {
                if !(result[name] ?? []).contains(where: { $0.processIdentifier == app.processIdentifier }) { result[name, default: []].append(app) }
            }
        }
        return result.mapValues { $0.sorted { $0.processIdentifier < $1.processIdentifier } }
    }

    private static func address(_ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    }

    private static func list(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> [AudioObjectID] {
        var where_ = address(selector, scope)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(object, &where_, 0, nil, &size) == noErr, size > 0 else { return [] }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(object, &where_, 0, nil, &size, &ids) == noErr else { return [] }
        return ids
    }

    private static func string(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String {
        var where_ = address(selector)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(object, &where_, 0, nil, &size, &value) == noErr else { return "" }
        return value?.takeRetainedValue() as String? ?? ""
    }
}
