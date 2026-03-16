import SwiftUI

struct ContentView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        Group {
            if appState.serverURL == nil {
                SettingsView()
            } else {
                NavigationStack {
                    AgentListView()
                }
            }
        }
    }
}
