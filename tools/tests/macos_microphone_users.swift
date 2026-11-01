import AppKit
import CoreAudio

let physical: [AudioObjectID: String] = [10: "Built-in microphone", 20: "USB microphone"]
func check(_ ids: [AudioObjectID], selected: String?, expected: Set<String>) {
    let actual = MicrophoneUsers.deviceNames(ids, physical: physical, virtual: 30, selected: selected)
    guard actual == expected else { print("FAIL", ids, actual, "expected", expected); exit(1) }
}
check([10], selected: "USB microphone", expected: ["Built-in microphone"])
check([30], selected: "USB microphone", expected: ["USB microphone"])
check([10, 30, 10], selected: "Built-in microphone", expected: ["Built-in microphone"])
check([10, 30], selected: "USB microphone", expected: ["Built-in microphone", "USB microphone"])
check([30], selected: nil, expected: [])
check([99], selected: "USB microphone", expected: [])
print("PASS physical, virtual, multiple inputs, duplicates, missing source and unknown device")

var names: [String] = []
var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
var size: UInt32 = 0
let system = AudioObjectID(kAudioObjectSystemObject)
if AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr {
    var devices = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
    if AudioObjectGetPropertyData(system, &address, 0, nil, &size, &devices) == noErr {
        for device in devices {
            var nameAddress = AudioObjectPropertyAddress(mSelector: kAudioObjectPropertyName, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
            var name: Unmanaged<CFString>?
            var nameSize = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
            if AudioObjectGetPropertyData(device, &nameAddress, 0, nil, &nameSize, &name) == noErr, let name = name?.takeRetainedValue() { names.append(name as String) }
        }
    }
}
let snapshot = MicrophoneUsers.snapshot(devices: names, selected: names.first)
for (device, users) in snapshot.sorted(by: { $0.key < $1.key }) {
    for user in users { print("LIVE", device, user.processIdentifier, user.bundleIdentifier ?? "", "icon", user.icon != nil) }
}
if CommandLine.arguments.count > 1 {
    let expected = CommandLine.arguments[1]
    guard snapshot.values.flatMap({ $0 }).contains(where: { $0.bundleIdentifier == expected && $0.icon != nil }) else {
        print("FAIL active microphone application not found:", expected); exit(1)
    }
    print("PASS active microphone application and icon:", expected)
}
