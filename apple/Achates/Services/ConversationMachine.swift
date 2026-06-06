/// Pure, UI-free state machine for hands-free conversation mode. The
/// `ConversationController` translates real-world signals (silence timer,
/// stream-done events, playback completion, audio interruptions, the End
/// button) into `ConversationEvent`s and executes the returned
/// `ConversationIntent`s. Keeping the decision logic here makes it
/// unit-testable without audio or networking.
nonisolated enum ConversationState: Equatable {
    case idle
    case listening
    case sending      // user turn submitted; awaiting/streaming the reply, no audio yet
    case speaking     // reply audio is playing
    case paused       // system audio interruption in effect
    case ended        // user ended the conversation
    case failed(String)
}

nonisolated enum ConversationEvent: Equatable {
    case begin
    case speechCaptured(String)          // silence timer fired with a non-empty final transcript
    case turnCompleted(isPlaying: Bool)  // `done` arrived; whether speech is still playing
    case playbackFinished                // speech player queue drained
    case interrupted                     // AVAudioSession interruption began
    case resumed                         // interruption ended
    case turnFailed(String)              // agent/network error mid-turn
    case startFailed(String)             // mic / recognizer unavailable
    case endRequested                    // user tapped End
}

nonisolated enum ConversationIntent: Equatable {
    case startListening
    case stopListening
    case sendTurn(String)
    case stopPlayback
    case teardown
}

struct ConversationMachine {
    private(set) var state: ConversationState = .idle

    @discardableResult
    mutating func handle(_ event: ConversationEvent) -> [ConversationIntent] {
        switch (state, event) {
        case (.idle, .begin):
            state = .listening
            return [.startListening]

        case (.listening, .speechCaptured(let text)):
            state = .sending
            return [.stopListening, .sendTurn(text)]

        case (.sending, .turnCompleted(let isPlaying)):
            if isPlaying {
                state = .speaking
                return []
            }
            state = .listening
            return [.startListening]

        case (.speaking, .playbackFinished):
            state = .listening
            return [.startListening]

        // Playback can briefly drain between streamed sentences before `done`;
        // ignore it while still sending — `turnCompleted` is the authority.
        case (.sending, .playbackFinished):
            return []

        case (.listening, .interrupted),
             (.sending, .interrupted),
             (.speaking, .interrupted):
            state = .paused
            return [.stopListening, .stopPlayback]

        case (.paused, .resumed):
            state = .listening
            return [.startListening]

        // Mid-turn agent/network failure: surface upstream, resume listening.
        case (.sending, .turnFailed), (.speaking, .turnFailed):
            state = .listening
            return [.stopPlayback, .startListening]

        case (.idle, .startFailed(let msg)),
             (.listening, .startFailed(let msg)),
             (.paused, .startFailed(let msg)):
            state = .failed(msg)
            return [.teardown]

        case (_, .endRequested):
            state = .ended
            return [.stopListening, .stopPlayback, .teardown]

        default:
            return []
        }
    }
}
