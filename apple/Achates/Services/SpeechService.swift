import AVFoundation
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

@MainActor
@Observable
final class SpeechService {
    var isRecording = false
    var transcript = ""

    private var audioEngine: AVAudioEngine?
    private var recognitionTask: SFSpeechRecognitionTask?
    private var recognitionRequest: SFSpeechAudioBufferRecognitionRequest?

    func requestAuthorization() async -> Bool {
        await withCheckedContinuation { continuation in
            SFSpeechRecognizer.requestAuthorization { status in
                continuation.resume(returning: status == .authorized)
            }
        }
    }

    func startRecording() async throws {
        let authorized = await requestAuthorization()
        guard authorized else { throw SpeechError.notAuthorized }

        guard let recognizer = SFSpeechRecognizer(), recognizer.isAvailable else {
            throw SpeechError.recognizerUnavailable
        }

        if recognizer.supportsOnDeviceRecognition {
            recognizer.supportsOnDeviceRecognition = true
        }

        #if os(iOS)
        let audioSession = AVAudioSession.sharedInstance()
        try audioSession.setCategory(.record, mode: .measurement, options: .duckOthers)
        try audioSession.setActive(true, options: .notifyOthersOnDeactivation)
        #endif

        let engine = AVAudioEngine()
        let request = SFSpeechAudioBufferRecognitionRequest()
        request.shouldReportPartialResults = true
        if recognizer.supportsOnDeviceRecognition {
            request.requiresOnDeviceRecognition = true
        }

        let inputNode = engine.inputNode
        let recordingFormat = inputNode.outputFormat(forBus: 0)

        inputNode.installTap(onBus: 0, bufferSize: 1024, format: recordingFormat) { buffer, _ in
            request.append(buffer)
        }

        engine.prepare()
        try engine.start()

        recognitionTask = recognizer.recognitionTask(with: request) { [weak self] result, error in
            Task { @MainActor [weak self] in
                guard let self else { return }
                if let result {
                    self.transcript = result.bestTranscription.formattedString
                }
                if error != nil || (result?.isFinal ?? false) {
                    self.stopRecordingInternal()
                }
            }
        }

        audioEngine = engine
        recognitionRequest = request
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
        recognitionRequest?.endAudio()
        recognitionTask?.cancel()

        audioEngine = nil
        recognitionRequest = nil
        recognitionTask = nil
        isRecording = false
    }
}
