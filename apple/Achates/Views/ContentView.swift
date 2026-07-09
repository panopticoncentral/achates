import SwiftUI

struct ContentView: View {
    @Environment(AppState.self) private var appState

    /// Presents `appState.error` — the app-wide surface for failures set anywhere
    /// in AppState (session load/delete/rename, resubmit, speech toggle, ...).
    private var errorAlertBinding: Binding<Bool> {
        Binding(
            get: { appState.error != nil },
            set: { if !$0 { appState.error = nil } }
        )
    }

    var body: some View {
        @Bindable var appState = appState
        Group {
            if appState.serverURL == nil {
                #if os(macOS)
                MacOnboardingView()
                #else
                NavigationStack {
                    SettingsView()
                }
                #endif
            } else {
                #if os(macOS)
                macOSNavigation
                #else
                NavigationStack(path: $appState.navigationPath) {
                    AgentListView()
                        .navigationDestination(for: Agent.self) { agent in
                            SessionListView(agent: agent)
                        }
                }
                #endif
            }
        }
        .alert("Something Went Wrong", isPresented: errorAlertBinding) {
            Button("OK") { appState.error = nil }
        } message: {
            Text(appState.error ?? "")
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
