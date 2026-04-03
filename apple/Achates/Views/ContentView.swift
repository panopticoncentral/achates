import SwiftUI

struct ContentView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        Group {
            if appState.serverURL == nil {
                SettingsView()
            } else {
                #if os(macOS)
                macOSNavigation
                #else
                NavigationStack {
                    AgentListView()
                        .navigationDestination(for: Agent.self) { agent in
                            SessionListView(agent: agent)
                        }
                }
                #endif
            }
        }
    }

    #if os(macOS)
    @ViewBuilder
    private var macOSNavigation: some View {
        NavigationSplitView {
            AgentListView()
        } content: {
            if let agent = appState.currentAgent {
                SessionListView(agent: agent)
            } else {
                ContentUnavailableView("Select an Agent", systemImage: "bubble.left.and.bubble.right")
            }
        } detail: {
            if let agent = appState.currentAgent, appState.currentSessionId != nil {
                ChatView(agent: agent)
            } else {
                ContentUnavailableView("Select a Conversation", systemImage: "bubble.left")
            }
        }
    }
    #endif
}
