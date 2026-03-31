import SwiftUI
import UserNotifications

@main
struct AchatesApp: App {
    @State private var appState = AppState()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environment(appState)
                #if os(macOS)
                .frame(minWidth: 700, minHeight: 500)
                #endif
                .task {
                    _ = try? await UNUserNotificationCenter.current()
                        .requestAuthorization(options: [.badge])
                }
        }
        #if os(macOS)
        .defaultSize(width: 1000, height: 700)
        .commands {
            CommandGroup(after: .newItem) {
                Button("New Conversation") {
                    Task { await appState.addBreakAtEnd() }
                }
                .keyboardShortcut("n", modifiers: .command)
                .disabled(appState.currentAgent == nil)
            }
        }
        #endif

        #if os(macOS)
        Settings {
            SettingsView()
                .environment(appState)
        }
        #endif
    }
}
