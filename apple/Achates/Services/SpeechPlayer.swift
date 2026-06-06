import AVFoundation
import Observation

/// Plays a stream of MP3 audio chunks (one per assistant sentence) in order
/// via AVQueuePlayer. Chunks arrive over the WebSocket as base64-encoded MP3
/// bytes inside `audio.block` events; each is written to a temp file and
/// enqueued. The temp files for a turn are retained until that turn is
/// purged (so the per-message replay button can play them back).
@MainActor
@Observable
final class SpeechPlayer {
    private var player: AVQueuePlayer?
    /// turnId → list of temp-file URLs played for that turn (in sentence order).
    /// Kept around after playback completes so the per-message replay button
    /// can re-enqueue them. Purged via `purge(turnId:)` or `purgeAll()`.
    private var turnArchive: [String: [URL]] = [:]
    private(set) var currentTurnId: String?

    /// True while the queue is actively playing the current turn. Flips to
    /// false when the queue drains (`currentItem` becomes nil). The
    /// ConversationController watches this to know when the agent's spoken
    /// reply has finished so it can resume listening.
    private(set) var isPlaying = false

    /// KVO on the queue player's `currentItem`; nil means the queue drained.
    private var currentItemObservation: NSKeyValueObservation?

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
            // Non-fatal — playback may still work but mixing/routing degrades.
        }
        #endif
    }

    /// Enqueue an MP3 chunk for the given turn. Called for every `audio.block`
    /// event. A new turnId starts a fresh queue (previous turns stay in the
    /// archive for replay).
    func enqueue(turnId: String, sentenceIndex: Int, mp3Data: Data) {
        if currentTurnId != turnId {
            // New turn — drain the existing queue and start fresh.
            player?.pause()
            player?.removeAllItems()
            player = nil
            currentTurnId = turnId
        }

        let url = makeTempURL(turnId: turnId, sentenceIndex: sentenceIndex)
        do {
            try mp3Data.write(to: url, options: .atomic)
        } catch {
            return
        }

        turnArchive[turnId, default: []].append(url)

        let item = AVPlayerItem(url: url)
        if let player {
            player.insert(item, after: nil)
            isPlaying = true
            if player.timeControlStatus != .playing {
                player.play()
            }
        } else {
            let p = AVQueuePlayer(items: [item])
            player = p
            observeDrain(of: p)
            isPlaying = true
            p.play()
        }
    }

    /// Replay all sentences from a previously-played turn. Used by the
    /// per-message replay button. Returns false if there's nothing archived
    /// for that turn (e.g. files were purged or the turn never produced audio).
    @discardableResult
    func replay(turnId: String) -> Bool {
        guard let urls = turnArchive[turnId], !urls.isEmpty else { return false }
        player?.pause()
        player?.removeAllItems()
        let items = urls.map { AVPlayerItem(url: $0) }
        let p = AVQueuePlayer(items: items)
        player = p
        currentTurnId = turnId
        observeDrain(of: p)
        isPlaying = true
        p.play()
        return true
    }

    /// Stop and clear the current queue without dropping the archive. Used
    /// when the user dismisses a session or the app suspends.
    func stop() {
        player?.pause()
        player?.removeAllItems()
        player = nil
        currentItemObservation?.invalidate()
        currentItemObservation = nil
        isPlaying = false
    }

    /// Drop archived temp files for a turn (e.g. when its session is deleted).
    func purge(turnId: String) {
        if let urls = turnArchive.removeValue(forKey: turnId) {
            for url in urls {
                try? FileManager.default.removeItem(at: url)
            }
        }
    }

    /// Drop everything — current playback and all archived temp files.
    /// Useful on disconnect / agent switch to keep the temp directory bounded.
    func purgeAll() {
        stop()
        for (_, urls) in turnArchive {
            for url in urls {
                try? FileManager.default.removeItem(at: url)
            }
        }
        turnArchive.removeAll()
        currentTurnId = nil
    }

    /// Watch the queue player's currentItem; when it becomes nil the queue has
    /// drained and the turn's audio is finished playing.
    private func observeDrain(of p: AVQueuePlayer) {
        currentItemObservation?.invalidate()
        currentItemObservation = p.observe(\.currentItem, options: [.new]) { [weak self] player, _ in
            Task { @MainActor in
                guard let self else { return }
                // Only react to *this* player draining.
                guard self.player === player else { return }
                if player.currentItem == nil {
                    self.isPlaying = false
                }
            }
        }
    }

    private func makeTempURL(turnId: String, sentenceIndex: Int) -> URL {
        let name = "achates-speech-\(turnId)-\(sentenceIndex).mp3"
        return FileManager.default.temporaryDirectory.appendingPathComponent(name)
    }
}
