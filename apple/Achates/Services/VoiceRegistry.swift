import Foundation
import Observation

/// Caches the list of TTS voice ids returned by the server's `voices.list`
/// RPC. Used by the agent edit sheet's voice picker. The list reflects what
/// the configured TTS sidecar advertises; it is empty when speech is not
/// configured on the server, which the picker treats as a graceful
/// "voice unavailable" state.
@MainActor
@Observable
final class VoiceRegistry {
    var voices: [String] = []
    var isLoading = false
    private var lastFetched: Date?
    /// Sheet-scoped cache lifetime — refresh on next open if older than this.
    private let cacheLifetime: TimeInterval = 60

    private let appState: AppState

    init(appState: AppState) {
        self.appState = appState
    }

    func loadIfStale() async {
        if let lastFetched, Date().timeIntervalSince(lastFetched) < cacheLifetime, !voices.isEmpty {
            return
        }
        await refresh()
    }

    func refresh() async {
        guard let client = appState.client else {
            voices = []
            return
        }
        isLoading = true
        defer { isLoading = false }
        do {
            let payload = try await client.sendRequest(method: "voices.list")
            if let arr = payload?["voices"]?.arrayValue {
                voices = arr.compactMap(\.stringValue)
                lastFetched = Date()
            } else {
                voices = []
            }
        } catch {
            // Silent degrade — empty list keeps the picker in a graceful "no voices" state.
            voices = []
        }
    }
}
