import SwiftUI

struct ContentView: View {
    @Environment(AppState.self) private var appState
    #if os(iOS)
    @Environment(\.horizontalSizeClass) private var sizeClass
    #endif
    @State private var isSettingUp: Bool

    init(isSettingUp: Bool? = nil) {
        _isSettingUp = State(initialValue: isSettingUp ?? (UserDefaults.standard.string(forKey: "achates_server_url") == nil))
    }

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
            if isSettingUp || appState.serverURL == nil {
                #if os(macOS)
                MacOnboardingView()
                #else
                NavigationStack {
                    SettingsView(isOnboarding: true)
                }
                #endif
            } else {
                #if os(macOS)
                splitNavigation
                #else
                if sizeClass == .regular {
                    splitNavigation
                } else {
                    NavigationStack(path: $appState.navigationPath) {
                        AgentListView()
                            .navigationDestination(for: Agent.self) { agent in
                                SessionListView(agent: agent)
                            }
                    }
                }
                #endif
            }
        }
        #if os(iOS)
        .onChange(of: sizeClass, initial: true) { _, size in
            appState.usesSplitNavigation = size == .regular
            if size != .regular, let agent = appState.currentAgent {
                var path = NavigationPath()
                path.append(agent)
                if let id = appState.currentSessionId { path.append(SessionSelection(agent: agent, sessionId: id)) }
                appState.navigationPath = path
            }
        }
        #endif
        .onChange(of: appState.connectionStatus) { _, status in
            if status == .connected { isSettingUp = false }
        }
        .alert("Something Went Wrong", isPresented: errorAlertBinding) {
            Button("OK") { appState.error = nil }
        } message: {
            Text(appState.error ?? "")
        }
    }

    @ViewBuilder
    private var splitNavigation: some View {
        NavigationSplitView {
            AgentListView()
                .navigationSplitViewColumnWidth(min: 180, ideal: 230, max: 320)
        } content: {
            if let agent = appState.currentAgent {
                SessionListView(agent: agent)
                    .navigationSplitViewColumnWidth(min: 220, ideal: 280, max: 380)
            } else {
                ContentUnavailableView("Select an Agent", systemImage: "bubble.left.and.bubble.right")
            }
        } detail: {
            if let agent = appState.currentAgent, appState.currentSessionId != nil {
                ChatView(agent: agent)
                    .id(appState.currentSessionId)
            } else {
                ContentUnavailableView("Select a Conversation", systemImage: "bubble.left")
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) { ConnectionStatusBanner() }
    }
}
