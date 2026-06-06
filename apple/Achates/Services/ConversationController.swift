import Foundation
import AVFoundation

/// Drives hands-free conversation mode by wiring real-world signals into the
/// pure `ConversationMachine` and executing its intents against the on-device
/// STT (`SpeechService`), the shared `SpeechPlayer`, and the chat send path.
@MainActor
@Observable
final class ConversationController {
    private var machine = ConversationMachine()
    var state: ConversationState { machine.state }

    /// Non-blocking notice surfaced in the call screen (e.g. "No voice configured").
    var banner: String?

    /// Live partial transcript of the current utterance (for the call screen).
    var liveTranscript: String { speech.transcript }

    private let appState: AppState
    private let speech: SpeechService
    private var speechPlayer: SpeechPlayer { appState.speechPlayer }

    /// Pause of transcript-stability that ends a spoken turn. Tune on device.
    private let silenceThreshold: Duration = .milliseconds(1200)

    private var silenceTask: Task<Void, Never>?
    private var lastTranscript = ""
    private var interruptionObserver: NSObjectProtocol?

    init(appState: AppState, speech: SpeechService? = nil) {
        self.appState = appState
        self.speech = speech ?? SpeechService()
    }

    // MARK: - Lifecycle (called by the view)

    func begin() {
        observeInterruptions()
        apply(machine.handle(.begin))
    }

    func end() {
        apply(machine.handle(.endRequested))
    }

    /// From `.onChange(of: appState.isStreaming)` when it flips to false.
    func turnDidComplete() {
        apply(machine.handle(.turnCompleted(isPlaying: speechPlayer.isPlaying)))
    }

    /// From `.onChange(of: appState.speechPlayer.isPlaying)` when it flips to false.
    func playbackDidFinish() {
        apply(machine.handle(.playbackFinished))
    }

    /// From `.onChange(of: appState.connectionStatus)` when the socket drops. If
    /// it drops mid-turn the `done` event never arrives, so without this the loop
    /// would hang in `.sending`. Resolves to resume listening (STT is on-device).
    func connectionDidDrop() {
        guard state == .sending || state == .speaking else { return }
        banner = "Connection lost — resumed listening."
        apply(machine.handle(.turnFailed("connection lost")))
    }

    // MARK: - Intent execution

    private func apply(_ intents: [ConversationIntent]) {
        for intent in intents { execute(intent) }
    }

    private func execute(_ intent: ConversationIntent) {
        switch intent {
        case .startListening: startListening()
        case .stopListening:  stopListening()
        case .sendTurn(let text): sendTurn(text)
        case .stopPlayback:   speechPlayer.stop()
        case .teardown:       teardown()
        }
    }

    private func startListening() {
        lastTranscript = ""
        configureRecordSession()
        speech.onTranscriptUpdate = { [weak self] text in
            self?.handleTranscriptUpdate(text)
        }
        Task { [weak self] in
            guard let self else { return }
            do {
                try await self.speech.startRecording(continuous: true)
            } catch {
                self.apply(self.machine.handle(.startFailed(error.localizedDescription)))
            }
        }
    }

    private func stopListening() {
        silenceTask?.cancel()
        silenceTask = nil
        speech.onTranscriptUpdate = nil
        _ = speech.stopRecording()
    }

    private func sendTurn(_ text: String) {
        configurePlaybackSession()
        Task { [weak self] in await self?.appState.sendMessage(text) }
    }

    private func teardown() {
        stopListening()
        speechPlayer.stop()
        if let obs = interruptionObserver {
            NotificationCenter.default.removeObserver(obs)
            interruptionObserver = nil
        }
        #if os(iOS)
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
        #endif
    }

    // MARK: - Silence detection

    private func handleTranscriptUpdate(_ text: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        // Empty partials never end a turn — keep listening until there's speech.
        guard !trimmed.isEmpty else { return }
        lastTranscript = trimmed
        silenceTask?.cancel()
        silenceTask = Task { [weak self] in
            guard let self else { return }
            try? await Task.sleep(for: self.silenceThreshold)
            guard !Task.isCancelled else { return }
            let finalText = self.lastTranscript
            guard !finalText.isEmpty else { return }
            self.apply(self.machine.handle(.speechCaptured(finalText)))
        }
    }

    // MARK: - Audio session / interruptions

    private func configureRecordSession() {
        #if os(iOS)
        let session = AVAudioSession.sharedInstance()
        try? session.setCategory(.record, mode: .measurement, options: .duckOthers)
        try? session.setActive(true, options: .notifyOthersOnDeactivation)
        #endif
    }

    private func configurePlaybackSession() {
        #if os(iOS)
        let session = AVAudioSession.sharedInstance()
        try? session.setCategory(.playback, mode: .spokenAudio, options: [.duckOthers])
        try? session.setActive(true)
        #endif
    }

    private func observeInterruptions() {
        #if os(iOS)
        // Guard against a double-add: a second begin() without an intervening
        // teardown would otherwise leak the first observer (it fires forever).
        guard interruptionObserver == nil else { return }
        interruptionObserver = NotificationCenter.default.addObserver(
            forName: AVAudioSession.interruptionNotification,
            object: nil,
            queue: .main
        ) { [weak self] note in
            let raw = (note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt) ?? 0
            // Rebind self weakly at the closure boundary (not inside the Task) so
            // the @MainActor hop carries only Sendable state — keeps Swift 6
            // strict-concurrency clean.
            Task { @MainActor [weak self] in
                guard let self else { return }
                switch AVAudioSession.InterruptionType(rawValue: raw) {
                case .began: self.apply(self.machine.handle(.interrupted))
                case .ended: self.apply(self.machine.handle(.resumed))
                default: break
                }
            }
        }
        #endif
    }
}
