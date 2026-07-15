@preconcurrency import AVFoundation
import Speech

enum SpeechError: Error, LocalizedError {
    case notAuthorized
    case recognizerUnavailable
    case audioEngineError(String)

    var errorDescription: String? {
        switch self {
        case .notAuthorized: return "Speech recognition not authorized"
        case .recognizerUnavailable: return "Speech recognizer unavailable"
        case .audioEngineError(let msg): return msg
        }
    }
}

/// On-device dictation built on `SpeechAnalyzer` / `SpeechTranscriber` (iOS 26 /
/// macOS 26). Replaces the legacy `SFSpeechRecognizer` path, which was ~4x less
/// accurate on the same audio. The public surface is unchanged, so callers
/// (`ConversationController`, `ComposerView`, `ChatView`) are untouched.
@MainActor
@Observable
final class SpeechService {
    var isRecording = false
    var transcript = ""

    /// Fired on every partial-result update with the latest transcript. The
    /// ConversationController uses this to drive its silence timer.
    var onTranscriptUpdate: ((String) -> Void)?

    /// In continuous (hands-free) mode the controller decides when a turn ends
    /// (via its silence timer); in push-to-talk the composer's mic button does.
    /// Either way the analyzer runs until `stopRecording()` — it never tears
    /// itself down mid-stream — so this is retained only for callers' intent.
    private var continuous = false

    private var audioEngine: AVAudioEngine?
    private var analyzer: SpeechAnalyzer?
    private var transcriber: SpeechTranscriber?
    private var inputBuilder: AsyncStream<AnalyzerInput>.Continuation?
    private var recognizerTask: Task<Void, Never>?

    /// The stable prefix of the transcript. `SpeechTranscriber` delivers each
    /// phrase as a series of volatile hypotheses that firm up into one finalized
    /// result; we keep the finalized text here and append the live volatile tail.
    private var finalizedText = ""

    /// Requests Speech-framework authorization. Still required by the on-device
    /// analyzer — the framework traps if `NSSpeechRecognitionUsageDescription`
    /// is absent when any Speech API is used.
    func requestAuthorization() async -> Bool {
        await withCheckedContinuation { continuation in
            SFSpeechRecognizer.requestAuthorization { status in
                continuation.resume(returning: status == .authorized)
            }
        }
    }

    func startRecording(continuous: Bool = false) async throws {
        self.continuous = continuous

        let authorized = await requestAuthorization()
        guard authorized else { throw SpeechError.notAuthorized }

        // Pick a transcriber locale equivalent to the user's, if the models
        // support it at all on this device.
        guard let locale = await SpeechTranscriber.supportedLocale(equivalentTo: Locale.current) else {
            throw SpeechError.recognizerUnavailable
        }

        // Configure for live audio: volatile results give the incremental
        // partial updates the composer display and the silence timer depend on.
        let transcriber = SpeechTranscriber(
            locale: locale,
            transcriptionOptions: [],
            reportingOptions: [.volatileResults],
            attributeOptions: []
        )

        // Ensure the on-device model for this locale is installed. Returns nil
        // (instant no-op) when it's already present — the common case, since the
        // system dictation model ships preinstalled. A genuine failure here
        // surfaces as recognizerUnavailable.
        do {
            if let installation = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) {
                try await installation.downloadAndInstall()
            }
        } catch {
            throw SpeechError.recognizerUnavailable
        }

        guard let analyzerFormat = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber]) else {
            throw SpeechError.recognizerUnavailable
        }

        #if os(iOS)
        let audioSession = AVAudioSession.sharedInstance()
        try audioSession.setCategory(.record, mode: .measurement, options: .duckOthers)
        try audioSession.setActive(true, options: .notifyOthersOnDeactivation)
        #endif

        let engine = AVAudioEngine()
        let inputNode = engine.inputNode
        let recordingFormat = inputNode.outputFormat(forBus: 0)

        // The mic's hardware format is rarely what the analyzer wants, so
        // convert every captured buffer on the audio thread before feeding it in.
        guard let converter = AVAudioConverter(from: recordingFormat, to: analyzerFormat) else {
            throw SpeechError.audioEngineError("Unable to convert microphone audio to the transcriber's format")
        }

        let (inputSequence, inputBuilder) = AsyncStream.makeStream(of: AnalyzerInput.self)
        let analyzer = SpeechAnalyzer(modules: [transcriber])

        finalizedText = ""

        // Consume results: accumulate finalized phrases, append the current
        // volatile hypothesis, publish the running transcript. A thrown error
        // (e.g. the analyzer finishing abnormally) stops cleanly.
        let recognizerTask = Task { [weak self] in
            do {
                for try await result in transcriber.results {
                    guard let self else { return }
                    let piece = String(result.text.characters)
                    if result.isFinal {
                        self.finalizedText += piece
                        self.transcript = self.finalizedText
                    } else {
                        self.transcript = self.finalizedText + piece
                    }
                    self.onTranscriptUpdate?(self.transcript)
                }
            } catch {
                self?.stopRecordingInternal()
            }
        }

        inputNode.installTap(onBus: 0, bufferSize: 4096, format: recordingFormat) { buffer, _ in
            guard let converted = SpeechService.convert(buffer, to: analyzerFormat, using: converter) else { return }
            inputBuilder.yield(AnalyzerInput(buffer: converted))
        }

        engine.prepare()
        try engine.start()
        try await analyzer.start(inputSequence: inputSequence)

        audioEngine = engine
        self.transcriber = transcriber
        self.analyzer = analyzer
        self.inputBuilder = inputBuilder
        self.recognizerTask = recognizerTask
        isRecording = true
        transcript = ""
    }

    func stopRecording() -> String {
        let finalTranscript = transcript
        stopRecordingInternal()
        return finalTranscript
    }

    private func stopRecordingInternal() {
        audioEngine?.stop()
        audioEngine?.inputNode.removeTap(onBus: 0)
        inputBuilder?.finish()
        recognizerTask?.cancel()

        // Finish the analysis session off the main actor; the local reference
        // keeps it alive past the property teardown below.
        let analyzer = self.analyzer
        Task { await analyzer?.cancelAndFinishNow() }

        #if os(iOS)
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
        #endif

        audioEngine = nil
        transcriber = nil
        self.analyzer = nil
        inputBuilder = nil
        recognizerTask = nil
        isRecording = false
        continuous = false
        onTranscriptUpdate = nil
    }

    /// Converts one captured buffer to the analyzer's format. Runs on the audio
    /// thread, so it's `nonisolated` and touches only its arguments.
    private nonisolated static func convert(
        _ buffer: AVAudioPCMBuffer,
        to format: AVAudioFormat,
        using converter: AVAudioConverter
    ) -> AVAudioPCMBuffer? {
        let ratio = format.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount((Double(buffer.frameLength) * ratio).rounded(.up)) + 16
        guard capacity > 0, let output = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity) else {
            return nil
        }

        var fed = false
        let status = converter.convert(to: output, error: nil) { _, inputStatus in
            if fed {
                inputStatus.pointee = .noDataNow
                return nil
            }
            fed = true
            inputStatus.pointee = .haveData
            return buffer
        }

        if status == .error || output.frameLength == 0 {
            return nil
        }
        return output
    }
}
