import AVFoundation
import CoreAudio
import SwiftUI

// проверка на своём голосе: запись остаётся в памяти, на диск ничего не пишется
final class VoiceTest: ObservableObject {
    @Published private(set) var recording = false
    @Published private(set) var playing = false
    @Published private(set) var elapsed = 0
    @Published private(set) var failed = false
    @Published private(set) var peaks: [Float] = []
    @Published var listenProcessed = true { didSet { applyVolumes(); updatePeaks() } }

    private static let rate = 48000.0
    private let lock = NSLock()
    private var engine: AVAudioEngine?
    private var captured: [Float] = []
    private var original: [Float] = []
    private var processed: [Float] = []
    private var players: [AVAudioPlayer] = []
    private var timer: Timer?
    private var generation = 0
    private var lastConfig: UInt32?

    var hasRecording: Bool { !original.isEmpty }

    func startRecording(from input: String?, config: UInt32) {
        stopPlaying()
        failed = false
        AVCaptureDevice.requestAccess(for: .audio) { granted in
            DispatchQueue.main.async {
                if granted { self.beginCapture(from: input, config: config) } else { self.failed = true }
            }
        }
    }

    private func beginCapture(from input: String?, config: UInt32) {
        let engine = AVAudioEngine()
        if let input, let device = Self.device(named: input), let unit = engine.inputNode.audioUnit {
            var id = device
            AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &id, UInt32(MemoryLayout<AudioDeviceID>.size))
        }
        let format = engine.inputNode.outputFormat(forBus: 0)
        guard format.sampleRate > 0, let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: Self.rate, channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: format, to: target) else {
            failed = true
            return
        }
        lock.withLock { captured = [] }
        engine.inputNode.installTap(onBus: 0, bufferSize: 4096, format: format) { [weak self] buffer, _ in
            let capacity = AVAudioFrameCount(Double(buffer.frameLength) * Self.rate / format.sampleRate) + 64
            guard let self, let output = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else { return }
            var consumed = false
            converter.convert(to: output, error: nil) { _, status in
                if consumed { status.pointee = .noDataNow; return nil }
                consumed = true
                status.pointee = .haveData
                return buffer
            }
            guard let samples = output.floatChannelData?[0] else { return }
            self.lock.withLock { self.captured.append(contentsOf: UnsafeBufferPointer(start: samples, count: Int(output.frameLength))) }
        }
        do { try engine.start() } catch {
            engine.inputNode.removeTap(onBus: 0)
            failed = true
            return
        }
        self.engine = engine
        recording = true
        elapsed = 0
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.elapsed += 1
            // как на Windows, длинная запись не нужна и занимала бы память
            if self.elapsed >= 300 { self.stopRecording(config: self.lastConfig ?? config) }
        }
        lastConfig = config
    }

    func stopRecording(config: UInt32) {
        timer?.invalidate()
        engine?.inputNode.removeTap(onBus: 0)
        engine?.stop()
        engine = nil
        recording = false
        original = lock.withLock { captured }
        lock.withLock { captured = [] }
        failed = original.isEmpty
        lastConfig = nil
        process(config: config)
    }

    // настройки меняются на ходу, запись пересчитывается в фоне, а результат старого пересчёта отбрасывается
    func process(config: UInt32) {
        guard hasRecording, config != lastConfig else { return }
        lastConfig = config
        generation += 1
        let current = generation, source = original
        let models = Bundle.main.executableURL!.deletingLastPathComponent().path
        DispatchQueue.global(qos: .userInitiated).async {
            var result = [Float](repeating: 0, count: source.count)
            nr_bridge_process(models, config, source, &result, Int32(source.count))
            DispatchQueue.main.async {
                guard current == self.generation else { return }
                self.processed = result
                self.updatePeaks()
                self.reloadPlayers()
            }
        }
    }

    func togglePlaying() {
        if playing { stopPlaying() } else { startPlaying() }
    }

    func stopPlaying() {
        timer?.invalidate()
        players.forEach { $0.stop() }
        players = []
        playing = false
    }

    private func startPlaying() {
        guard let first = try? AVAudioPlayer(data: Self.wave(original)), let second = try? AVAudioPlayer(data: Self.wave(processed)) else {
            failed = true
            return
        }
        players = [first, second]
        applyVolumes()
        let start = first.deviceCurrentTime + 0.1
        players.forEach { $0.prepareToPlay(); $0.play(atTime: start) }
        playing = true
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            guard let self, self.players.first?.isPlaying != true else { return }
            self.stopPlaying()
        }
    }

    // пересчёт при игре: продолжаем с того же места, уже с новым звуком
    private func reloadPlayers() {
        guard playing, let time = players.first?.currentTime else { return }
        stopPlaying()
        startPlaying()
        players.forEach { $0.currentTime = time }
    }

    // обе записи играют одновременно, поэтому переключение не сбивает место
    private func applyVolumes() {
        guard players.count == 2 else { return }
        players[0].volume = listenProcessed ? 0 : 1
        players[1].volume = listenProcessed ? 1 : 0
    }

    private func updatePeaks() {
        let samples = listenProcessed ? processed : original
        guard !samples.isEmpty else { peaks = []; return }
        let bins = 160, step = max(samples.count / bins, 1)
        peaks = stride(from: 0, to: samples.count, by: step).map { start in
            samples[start..<min(start + step, samples.count)].reduce(0) { max($0, abs($1)) }
        }
    }

    private static func wave(_ samples: [Float]) -> Data {
        var data = Data()
        func put<T>(_ value: T) { withUnsafeBytes(of: value) { data.append(contentsOf: $0) } }
        let bytes = UInt32(samples.count * 2)
        data.append(contentsOf: Array("RIFF".utf8)); put(36 + bytes)
        data.append(contentsOf: Array("WAVEfmt ".utf8)); put(UInt32(16)); put(UInt16(1)); put(UInt16(1))
        put(UInt32(rate)); put(UInt32(rate) * 2); put(UInt16(2)); put(UInt16(16))
        data.append(contentsOf: Array("data".utf8)); put(bytes)
        for sample in samples { put(Int16((max(-1, min(1, sample.isFinite ? sample : 0)) * 32767).rounded())) }
        return data
    }

    private static func device(named name: String) -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr else { return nil }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &ids) == noErr else { return nil }
        return ids.first { id in
            var nameAddress = AudioObjectPropertyAddress(mSelector: kAudioObjectPropertyName, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
            var value: Unmanaged<CFString>?
            var length = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
            guard AudioObjectGetPropertyData(id, &nameAddress, 0, nil, &length, &value) == noErr else { return false }
            return value?.takeRetainedValue() as String? == name
        }
    }
}

struct VoiceTestSection: View {
    @ObservedObject var controller: Controller
    @StateObject private var test = VoiceTest()

    private var time: String { String(format: "%d:%02d", test.elapsed / 60, test.elapsed % 60) }

    var body: some View {
        Section {
            HStack {
                Button(test.recording ? controller.t("Остановить") + " " + time : controller.t("Записать")) {
                    if test.recording { test.stopRecording(config: controller.bridgeConfig) } else { test.startRecording(from: controller.input, config: controller.bridgeConfig) }
                }
                Button(test.playing ? controller.t("Остановить") : controller.t("Слушать")) { test.togglePlaying() }
                    .disabled(!test.hasRecording || test.recording)
                Spacer()
                Picker("", selection: $test.listenProcessed) {
                    Text(controller.t("Оригинал")).tag(false)
                    Text(controller.t("Обработано")).tag(true)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                .disabled(!test.hasRecording)
            }
            if !test.peaks.isEmpty {
                Canvas { context, size in
                    let width = size.width / CGFloat(test.peaks.count)
                    for (index, peak) in test.peaks.enumerated() {
                        let height = max(1, CGFloat(min(peak, 1)) * size.height)
                        let bar = CGRect(x: CGFloat(index) * width, y: (size.height - height) / 2, width: max(width - 1, 1), height: height)
                        context.fill(Path(roundedRect: bar, cornerRadius: 1), with: .color(.accentColor))
                    }
                }
                .frame(height: 44)
            }
        } header: {
            Text(controller.t("Проверка на своём голосе"))
        } footer: {
            Text(test.failed ? controller.t("Не удалось открыть микрофон") : test.recording ? controller.t("Говорите в микрофон и нажмите Остановить") : controller.t("Запишите фразу, затем слушайте и переключайте Оригинал и Обработано"))
                .font(.caption)
                .foregroundStyle(test.failed ? .red : .secondary)
        }
        .onChange(of: controller.bridgeConfig) { test.process(config: $0) }
        .onDisappear { test.stopPlaying() }
    }
}
