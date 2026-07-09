import SwiftUI
import UserNotifications

@main
struct AchatesApp: App {
    @State private var appState = AppState()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        #if os(macOS)
        // A single Window, not a WindowGroup: every window would share the one
        // AppState (one socket, one selection), so a second window just mirrors
        // and fights the first. This also frees ⌘N from File > New Window.
        Window("Achates", id: "main") {
            mainContent
                .frame(minWidth: 700, minHeight: 500)
        }
        .defaultSize(width: 1000, height: 700)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("New Conversation") {
                    Task {
                        if let agent = appState.currentAgent {
                            await appState.startNewConversation(for: agent)
                        }
                    }
                }
                .keyboardShortcut("n", modifiers: .command)
                .disabled(appState.currentAgent == nil)
            }
        }

        Settings {
            MacSettingsView()
                .environment(appState)
        }

        // Content management (memory, jobs, models) gets a real window instead
        // of pushes inside the settings pane. Also listed in the Window menu.
        Window("System", id: "system") {
            SystemWindowView()
                .environment(appState)
                .frame(minWidth: 640, minHeight: 420)
        }
        .defaultSize(width: 780, height: 540)
        #else
        WindowGroup {
            mainContent
        }
        #endif
    }

    private var mainContent: some View {
        ContentView()
            .environment(appState)
            .task {
                _ = try? await UNUserNotificationCenter.current()
                    .requestAuthorization(options: [.badge])
            }
            .onChange(of: scenePhase) { _, newPhase in
                appState.handleScenePhaseChange(newPhase)
            }
    }
}
