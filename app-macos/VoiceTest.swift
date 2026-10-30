import AVFoundation
import CoreAudio
import SwiftUI

// проверка на своём голосе: запись остаётся в памяти, на диск ничего не пишется
final class VoiceTest: ObservableObject {
    @Published private(set) var recording = false
    @Published private(set) var playing = false
    @Published private(set) var elapsed = 0
    @Published private(set) var failed = false
    @Published private(set) var originalPeaks: [Float] = []
    @Published private(set) var processedPeaks: [Float] = []
    @Published var listenProcessed = true { didSet { applyVolumes() } }
    // запись одна на всё приложение, новая на другом микрофоне заменяет прошлую
    @Published private(set) var owner: String?
    private(set) var started = Date()

    private static let rate = 48000.0
    private let lock = NSLock()
    private var engine: AVAudioEngine?
    private var captured: [Float] = []
    private var live: [Float] = []
    private var bucketPeak: Float = 0
    private var bucketCount = 0
    private var original: [Float] = []
    private var processed: [Float] = []
    private var players: [AVAudioPlayer] = []
    private var timer: Timer?
    private var generation = 0
    private var lastConfig: UInt32?
    private var pendingProcess: DispatchWorkItem?

    var ready: Bool { !processed.isEmpty && !recording }

    // доля пройденного при прослушивании, -1 если не играет
    var progress: Double {
        guard let player = players.first, player.duration > 0 else { return -1 }
        return player.currentTime / player.duration
    }

    // пик каждых 20 мс для живой волны
    func livePeaks() -> [Float] { lock.withLock { live } }

    func startRecording(from input: String?, config: UInt32) {
        forget()
        owner = input
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
        lock.withLock {
            captured = []
            live = []
            bucketPeak = 0
            bucketCount = 0
        }
        originalPeaks = []
        processedPeaks = []
        original = []
        processed = []
        engine.inputNode.installTap(onBus: 0, bufferSize: 1024, format: format) { [weak self] buffer, _ in
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
            self.collect(UnsafeBufferPointer(start: samples, count: Int(output.frameLength)))
        }
        do { try engine.start() } catch {
            engine.inputNode.removeTap(onBus: 0)
            failed = true
            return
        }
        self.engine = engine
        recording = true
        elapsed = 0
        started = Date()
        lastConfig = config
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.elapsed += 1
            // длинная запись только занимала бы память
            if self.elapsed >= 300 { self.stopRecording(config: self.lastConfig ?? config) }
        }
    }

    private func collect(_ samples: UnsafeBufferPointer<Float>) {
        lock.withLock {
            captured.append(contentsOf: samples)
            for sample in samples {
                bucketPeak = max(bucketPeak, abs(sample))
                bucketCount += 1
                if bucketCount >= 960 {
                    live.append(bucketPeak)
                    bucketPeak = 0
                    bucketCount = 0
                }
            }
        }
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
        originalPeaks = Self.peaks(original)
        lastConfig = nil
        process(config: config)
    }

    // новая сила или модель сразу применяются к готовой записи, ползунок шлёт много значений подряд, считаем только последнее
    func process(config: UInt32) {
        guard !original.isEmpty, !recording, config != lastConfig else { return }
        lastConfig = config
        generation += 1
        let current = generation, source = original
        let models = Bundle.main.executableURL!.deletingLastPathComponent().path
        pendingProcess?.cancel()
        let work = DispatchWorkItem {
            var result = [Float](repeating: 0, count: source.count)
            nr_bridge_process(models, config, source, &result, Int32(source.count))
            DispatchQueue.main.async {
                guard current == self.generation else { return }
                self.processed = result
                self.processedPeaks = Self.peaks(result)
                self.reloadPlayers()
            }
        }
        pendingProcess = work
        DispatchQueue.global(qos: .userInitiated).asyncAfter(deadline: .now() + 0.15, execute: work)
    }

    func togglePlaying() {
        if playing { stopPlaying() } else { startPlaying() }
    }

    func stopPlaying() {
        players.forEach { $0.stop() }
        players = []
        playing = false
    }

    // закрытое окно забывает проверку: и запись, и прослушивание
    func forget() {
        if recording {
            timer?.invalidate()
            engine?.inputNode.removeTap(onBus: 0)
            engine?.stop()
            engine = nil
            recording = false
        }
        stopPlaying()
        generation += 1
        pendingProcess?.cancel()
        lock.withLock {
            captured = []
            live = []
        }
        original = []
        processed = []
        originalPeaks = []
        processedPeaks = []
        lastConfig = nil
        failed = false
        owner = nil
    }

    // оба варианта играют одновременно и по кругу, переключение просто меняет, какой слышно
    private func startPlaying() {
        guard let first = try? AVAudioPlayer(data: Self.wave(original)), let second = try? AVAudioPlayer(data: Self.wave(processed)) else {
            failed = true
            return
        }
        players = [first, second]
        applyVolumes()
        players.forEach { $0.numberOfLoops = -1 }
        let start = first.deviceCurrentTime + 0.1
        players.forEach { $0.prepareToPlay(); $0.play(atTime: start) }
        playing = true
    }

    // пересчёт при игре: продолжаем с того же места, уже с новым звуком
    private func reloadPlayers() {
        guard playing, let time = players.first?.currentTime else { return }
        stopPlaying()
        startPlaying()
        players.forEach { $0.currentTime = time }
    }

    private func applyVolumes() {
        guard players.count == 2 else { return }
        players[0].volume = listenProcessed ? 0 : 1
        players[1].volume = listenProcessed ? 1 : 0
    }

    private static func peaks(_ samples: [Float]) -> [Float] {
        let count = 240
        var result = [Float](repeating: 0, count: count)
        guard !samples.isEmpty else { return [] }
        for (index, sample) in samples.enumerated() {
            let bucket = index * count / samples.count
            result[bucket] = max(result[bucket], abs(sample))
        }
        return result
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

// волна как на Windows: при записи бежит справа налево, потом исходный звук серым, после шумодава цветом поверх и бегунок
struct WaveformView: View {
    @ObservedObject var test: VoiceTest
    private static let bucketSeconds = 0.02, windowSeconds = 4.0, delay = 0.08

    var body: some View {
        TimelineView(.animation(paused: !test.recording && !test.playing)) { timeline in
            Canvas { context, size in
                if test.recording { drawLive(context, size, timeline.date) } else { drawResult(context, size) }
            }
        }
        .frame(height: 48)
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
        .background(.quaternary, in: RoundedRectangle(cornerRadius: 4))
        .clipped()
    }

    // голос заметен и на тихом микрофоне, громкий не упирается в край
    private func height(_ peak: Float) -> CGFloat { min(1, pow(CGFloat(max(0, peak)) * 4, 0.6)) }

    private func shape(_ points: [(CGFloat, CGFloat)], middle: CGFloat) -> Path {
        var path = Path()
        guard let first = points.first else { return path }
        path.move(to: CGPoint(x: first.0, y: middle - first.1))
        for point in points.dropFirst() { path.addLine(to: CGPoint(x: point.0, y: middle - point.1)) }
        for point in points.reversed() { path.addLine(to: CGPoint(x: point.0, y: middle + point.1)) }
        path.closeSubpath()
        return path
    }

    private func drawLive(_ context: GraphicsContext, _ size: CGSize, _ date: Date) {
        let peaks = test.livePeaks()
        guard size.width > 0, !peaks.isEmpty else { return }
        // время показа идёт плавно по часам, данные отстают на delay и всегда успевают прийти
        let now = min(date.timeIntervalSince(test.started) - Self.delay, Double(peaks.count) * Self.bucketSeconds)
        let scale = Double(size.width) / Self.windowSeconds, middle = size.height / 2
        let first = max(0, Int((now - Self.windowSeconds) / Self.bucketSeconds) - 1), last = min(peaks.count - 1, Int(now / Self.bucketSeconds))
        guard first <= last else { return }
        let points = (first...last).map { index -> (CGFloat, CGFloat) in
            (size.width - CGFloat((now - Double(index) * Self.bucketSeconds) * scale), max(0.75, height(peaks[index]) * middle))
        }
        context.fill(shape(points, middle: middle), with: .color(.accentColor))
    }

    private func drawResult(_ context: GraphicsContext, _ size: CGSize) {
        guard size.width > 0, !test.originalPeaks.isEmpty else { return }
        let top = max(test.originalPeaks.max() ?? 0, 0.02), middle = size.height / 2
        func points(_ peaks: [Float]) -> [(CGFloat, CGFloat)] {
            let step = size.width / CGFloat(max(1, peaks.count - 1))
            return peaks.enumerated().map { (CGFloat($0.offset) * step, max(0.75, CGFloat(min(1, $0.element / top)) * middle)) }
        }
        context.fill(shape(points(test.originalPeaks), middle: middle), with: .color(.secondary.opacity(test.listenProcessed ? 0.4 : 0.7)))
        context.fill(shape(points(test.processedPeaks), middle: middle), with: .color(.accentColor.opacity(test.listenProcessed ? 1 : 0.45)))
        let progress = test.progress
        if progress >= 0 {
            context.fill(Path(CGRect(x: min(max(progress, 0), 1) * max(0, size.width - 2), y: 0, width: 2, height: size.height)), with: .color(.primary))
        }
    }
}

struct VoiceTestSection: View {
    @ObservedObject var controller: Controller
    @ObservedObject var test: VoiceTest
    let device: String

    private var mine: Bool { test.owner == device }

    private var time: String { String(format: "%d:%02d", test.elapsed / 60, test.elapsed % 60) }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(controller.t("Проверка на своём голосе"))
            HStack {
                Button(test.recording && mine ? controller.t("Остановить") + " " + time : controller.t("Записать")) {
                    if test.recording { test.stopRecording(config: controller.config(for: device)) } else { test.startRecording(from: device, config: controller.config(for: device)) }
                }
                .buttonStyle(.borderedProminent)
                .disabled(test.recording && !mine)
                Button(test.playing && mine ? controller.t("Стоп") : controller.t("Слушать")) { test.togglePlaying() }
                    .disabled(!test.ready || !mine)
                Spacer()
                Picker("", selection: $test.listenProcessed) {
                    Text(controller.t("Оригинал")).tag(false)
                    Text(controller.t("Обработано")).tag(true)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                .disabled(!test.ready || !mine)
                .help(controller.t("Переключение на ходу"))
            }
            if mine && (test.recording || !test.originalPeaks.isEmpty) { WaveformView(test: test) }
            Text(mine && test.failed ? controller.t("Не удалось открыть микрофон") : mine && test.recording ? controller.t("Говорите в микрофон и нажмите Остановить") : controller.t("Запишите фразу, затем слушайте и переключайте Оригинал и Обработано"))
                .font(.callout)
                .foregroundStyle(mine && test.failed ? Color.red : Color.primary.opacity(0.72))
        }
        .onChange(of: controller.config(for: device)) { if mine { test.process(config: $0) } }
    }
}
