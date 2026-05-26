import AVFoundation
import Observation

/// One-shot MP3 player for the agent edit sheet's "Play sample" button. Kept
/// separate from `SpeechPlayer` (which handles streaming per-turn sentence
/// queues) so a sample preview can't collide with an active conversation's
/// audio queue. Holds one `AVAudioPlayer` at a time; starting a new sample
/// pre-empts the previous one.
@MainActor
@Observable
final class SpeechPreviewPlayer {
    private var player: AVAudioPlayer?
    private(set) var isPlaying = false
    /// Last error message, if any — surfaced to the user in the edit sheet.
    private(set) var lastError: String?

    init() {
        configureAudioSession()
    }

    private func configureAudioSession() {
        #if os(iOS)
        do {
            try AVAudioSession.sharedInstance().setCategory(
                .playback,
                mode: .spokenAudio,
                options: [.duckOthers]
            )
            try AVAudioSession.sharedInstance().setActive(true)
        } catch {
            // Non-fatal — playback may still work but routing degrades.
        }
        #endif
    }

    /// Decode the given base64 MP3 data and play it. Pre-empts any previous
    /// preview that's still playing. Errors are stored on `lastError`.
    func play(base64Mp3: String) {
        lastError = nil
        guard let data = Data(base64Encoded: base64Mp3), !data.isEmpty else {
            lastError = "Empty audio response from server."
            return
        }
        do {
            let p = try AVAudioPlayer(data: data, fileTypeHint: AVFileType.mp3.rawValue)
            // Stop any in-flight preview before kicking off the next one.
            player?.stop()
            player = p
            isPlaying = true
            p.play()
            // Best-effort completion detection so the UI can re-enable the
            // button. AVAudioPlayer's duration is known once it's prepared.
            let duration = p.duration
            if duration > 0 {
                Task { [weak self] in
                    try? await Task.sleep(for: .seconds(duration + 0.2))
                    await MainActor.run { self?.isPlaying = false }
                }
            } else {
                isPlaying = false
            }
        } catch {
            lastError = "Could not play audio: \(error.localizedDescription)"
            isPlaying = false
        }
    }

    func stop() {
        player?.stop()
        player = nil
        isPlaying = false
    }
}
