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
        } detail: {
            if let agent = appState.currentAgent {
                ChatView(agent: agent)
                    .id(agent.id)
            } else {
                ContentUnavailableView("Select an Agent", systemImage: "bubble.left.and.bubble.right")
            }
        }
    }
    #endif
}
