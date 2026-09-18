#if DEBUG && os(macOS)
import SwiftUI

/// Isolated data inside the real app scene, so UI tests exercise native toolbars.
/// No socket, stored preferences, or user conversations are touched.
struct AgentNavigationFixtureView: View {
    @State private var state: AppState = {
        let state = AppState()
        state.serverURL = URL(string: "https://ui-test.invalid")
        state.connectionStatus = .connected
        state.agents = ["Maya", "Atlas"].map { name in
            Agent(id: name.lowercased(), name: name.lowercased(), displayName: name,
                  description: "UI test agent", tools: [], lastMessage: nil,
                  lastActivity: nil, unreadCount: 0, avatarData: nil)
        }
        return state
    }()

    var body: some View {
        ContentView(isSettingUp: false)
            .environment(state)
            .task(id: state.currentAgent?.id) {
                guard let agent = state.currentAgent else { return }
                // Let the real selection and list-appearance tasks finish their
                // expected network-free failure before supplying fixture rows.
                do { try await Task.sleep(for: .milliseconds(100)) }
                catch { return }
                guard state.currentAgent?.id == agent.id else { return }
                state.sessionsLoadError = nil
                state.sessions = ["Planning", "Research"].map { title in
                    SessionInfo(id: "\(agent.id)-\(title)", title: "\(agent.displayName) \(title)",
                                preview: nil, created: Date(), updated: Date())
                }
            }
    }
}
#endif
