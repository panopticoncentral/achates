import SwiftUI

#if os(macOS)

// MARK: - Settings scene (⌘,)

/// Native tabbed settings panes. Preferences only — Memory, Scheduled Jobs, and
/// Default Models are content management, not preferences; they live in the
/// System window (`SystemWindowView`), reachable from the sidebar toolbar and
/// the Window menu.
struct MacSettingsView: View {
    var body: some View {
        TabView {
            ConnectionPane()
                .tabItem { Label("Connection", systemImage: "network") }
            DisplayPane()
                .tabItem { Label("Display", systemImage: "eye") }
            AboutPane()
                .tabItem { Label("About", systemImage: "info.circle") }
        }
        // Grouped Forms are List-backed (scrollable), so they report no useful
        // ideal height — size the window explicitly like a classic settings pane.
        .frame(width: 480, height: 320)
    }
}

private struct ConnectionPane: View {
    @Environment(AppState.self) private var appState
    @State private var urlString = ""

    var body: some View {
        Form {
            Section {
                TextField("Server URL", text: $urlString, prompt: Text("http://192.168.1.100:5000"))
                    .textContentType(.URL)
                    .autocorrectionDisabled()
                    .onSubmit(connect)

                LabeledContent("Status") {
                    switch appState.connectionStatus {
                    case .connected:
                        Label("Connected", systemImage: "checkmark.circle.fill")
                            .foregroundStyle(.green)
                    case .connecting, .reconnecting:
                        HStack(spacing: 6) {
                            ProgressView().controlSize(.small)
                            Text("Connecting…")
                        }
                    case .disconnected:
                        Text("Not connected")
                            .foregroundStyle(.secondary)
                    }
                }

                HStack {
                    Spacer()
                    switch appState.connectionStatus {
                    case .connected:
                        Button("Disconnect", role: .destructive) {
                            appState.disconnect()
                        }
                    case .connecting, .reconnecting:
                        Button("Cancel") {
                            appState.disconnect()
                        }
                        .keyboardShortcut(.cancelAction)
                    case .disconnected:
                        Button("Connect", action: connect)
                            .buttonStyle(.borderedProminent)
                            .keyboardShortcut(.defaultAction)
                            .disabled(urlString.isEmpty)
                    }
                }
            } footer: {
                if let reason = appState.lastConnectionError,
                   appState.connectionStatus == .disconnected {
                    Text(reason)
                        .foregroundStyle(.red)
                }
            }
        }
        .formStyle(.grouped)
        .onAppear {
            if let url = appState.serverURL {
                urlString = url.absoluteString
            }
        }
        .onChange(of: appState.serverURL) { _, new in
            if let new { urlString = new.absoluteString }
        }
    }

    private func connect() {
        guard let url = URL(string: urlString), url.scheme != nil else {
            appState.lastConnectionError = "Invalid URL"
            return
        }
        appState.lastConnectionError = nil
        appState.saveServerURL(url)
        appState.connectToServer()
    }
}

private struct DisplayPane: View {
    @AppStorage("show_message_costs") private var showMessageCosts = false
    @AppStorage("show_tool_activity") private var showToolActivity = false

    var body: some View {
        Form {
            Toggle("Show message costs", isOn: $showMessageCosts)
            Toggle("Show tool activity", isOn: $showToolActivity)
        }
        .formStyle(.grouped)
    }
}

private struct AboutPane: View {
    var body: some View {
        Form {
            LabeledContent("Version", value: AppVersion.display)
        }
        .formStyle(.grouped)
    }
}

// MARK: - First-run onboarding

/// Shown as the main window's content until a server URL exists — a centered
/// connect card instead of an iOS-shaped settings form filling the window.
struct MacOnboardingView: View {
    @Environment(AppState.self) private var appState
    @State private var urlString = ""

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "bubble.left.and.bubble.right")
                .font(.system(size: 56))
                .foregroundStyle(.tint)
            Text("Achates")
                .font(.largeTitle.bold())
            Text("Connect to your Achates server to get started.")
                .foregroundStyle(.secondary)

            TextField("Server URL", text: $urlString, prompt: Text("http://192.168.1.100:5000"))
                .textFieldStyle(.roundedBorder)
                .textContentType(.URL)
                .autocorrectionDisabled()
                .frame(maxWidth: 340)
                .onSubmit(connect)

            Button("Connect", action: connect)
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .disabled(urlString.isEmpty)

            if let reason = appState.lastConnectionError {
                Text(reason)
                    .font(.footnote)
                    .foregroundStyle(.red)
            }
        }
        .padding(40)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func connect() {
        guard let url = URL(string: urlString), url.scheme != nil else {
            appState.lastConnectionError = "Invalid URL"
            return
        }
        appState.lastConnectionError = nil
        appState.saveServerURL(url)
        appState.connectToServer()
    }
}

// MARK: - System window

/// Content-management window: Memory, Scheduled Jobs, Default Models. Each
/// section gets a real, resizable surface instead of a push inside the tiny
/// settings window.
struct SystemWindowView: View {
    @Environment(AppState.self) private var appState

    enum SystemSection: String, CaseIterable, Identifiable {
        case memory, jobs, models
        var id: String { rawValue }

        var title: String {
            switch self {
            case .memory: "Memory"
            case .jobs: "Scheduled Jobs"
            case .models: "Default Models"
            }
        }

        var icon: String {
            switch self {
            case .memory: "brain"
            case .jobs: "calendar.badge.clock"
            case .models: "cpu"
            }
        }
    }

    @State private var selection: SystemSection? = .memory

    var body: some View {
        NavigationSplitView {
            List(SystemSection.allCases, selection: $selection) { section in
                Label(section.title, systemImage: section.icon)
                    .tag(section)
            }
            .navigationSplitViewColumnWidth(min: 180, ideal: 200, max: 260)
        } detail: {
            if appState.connectionStatus != .connected {
                ContentUnavailableView(
                    "Not Connected",
                    systemImage: "wifi.slash",
                    description: Text("Connect to a server to manage memory, jobs, and models.")
                )
            } else {
                // Each section hosts its own stack so Memory can push its editor.
                switch selection {
                case .memory, nil:
                    NavigationStack { MemoryListView() }
                case .jobs:
                    NavigationStack { JobsView() }
                case .models:
                    NavigationStack { DefaultModelsView() }
                }
            }
        }
        .navigationTitle("System")
    }
}

#endif

/// Version string shared by the About pane and the iOS settings form.
enum AppVersion {
    static var display: String {
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "–"
        let build = Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "–"
        return "\(version) (\(build))"
    }
}
