import SwiftUI

/// iOS settings screen (pushed on compact screens, presented in a sheet in the
/// iPad split layout, or shown at first run in ContentView). It deliberately does NOT
/// create its own NavigationStack — when pushed, a nested stack would double
/// the navigation bars and break swipe-back.
///
/// macOS uses `MacSettingsView` (tabbed Settings scene) and `SystemWindowView`
/// instead.
struct SettingsView: View {
    @Environment(AppState.self) private var appState
    var isOnboarding = false
    @AppStorage("show_message_costs") private var showMessageCosts = false
    @AppStorage("show_tool_activity") private var showToolActivity = false
    @State private var urlString = ""
    @State private var showError = false
    @State private var errorMessage = ""

    private var isConnected: Bool { appState.connectionStatus == .connected }

    var body: some View {
        formContent
            .navigationTitle(isOnboarding ? "Welcome" : "Settings")
    }

    @ViewBuilder
    private var formContent: some View {
        Form {
            #if os(iOS)
            if isOnboarding {
                Section {
                    VStack(alignment: .center, spacing: 16) {
                        Image(systemName: "bubble.left.and.bubble.right")
                            .font(.system(size: 48))
                            .foregroundStyle(.tint)
                        Text("Achates")
                            .font(.largeTitle.bold())
                        Text("Connect to your Achates server")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 24)
                    .listRowBackground(Color.clear)
                }
            }
            #endif

            Section {
                TextField("Server URL", text: $urlString, prompt: Text("http://192.168.1.100:5000"))
                    .textContentType(.URL)
                    .autocorrectionDisabled()
                    .onSubmit(connect)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
            } header: {
                Text("Server")
            } footer: {
                if let reason = appState.lastConnectionError,
                   appState.connectionStatus == .disconnected {
                    Text(reason)
                        .foregroundStyle(.red)
                }
            }

            Section {
                switch appState.connectionStatus {
                case .connected:
                    Label("Connected", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                    Button("Disconnect") { appState.disconnect() }
                case .connecting, .reconnecting:
                    HStack {
                        ProgressView().controlSize(.small)
                        Text("Connecting…")
                        Spacer()
                        Button("Cancel") { appState.disconnect() }
                    }
                case .disconnected:
                    Button("Connect", action: connect).disabled(urlString.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }

            if !isOnboarding {
                Section("Display") {
                    Toggle("Show message costs", isOn: $showMessageCosts)
                    Toggle("Show tool activity", isOn: $showToolActivity)
                }

                Section {
                    NavigationLink {
                        MemoryListView()
                    } label: {
                        Label("Memory", systemImage: "brain")
                    }

                    NavigationLink {
                        JobsView()
                    } label: {
                        Label("Scheduled Jobs", systemImage: "calendar.badge.clock")
                    }

                    NavigationLink {
                        DefaultModelsPage()
                    } label: {
                        Label("Default Models", systemImage: "cpu")
                    }
                } header: {
                    Text("Manage")
                } footer: {
                    if !isConnected {
                        Text("Connect to a server to manage memory, jobs, and models.")
                    }
                }
                .disabled(!isConnected)

                Section("About") {
                    HStack {
                        Text("Version")
                        Spacer()
                        Text(AppVersion.display)
                            .foregroundStyle(.secondary)
                    }
                }
            }
        }
        .alert("Connection Error", isPresented: $showError) {
            Button("OK") {}
        } message: {
            Text(errorMessage)
        }
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
        guard let url = ServerAddress.parse(urlString) else {
            errorMessage = "Enter a server address such as https://achates.example.com or http://192.168.1.100:5000."
            showError = true
            return
        }

        appState.saveServerURL(url)
        appState.connectToServer()
    }
}
