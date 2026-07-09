import SwiftUI

/// iOS settings screen (pushed from the agent list, or shown at first run
/// wrapped in a NavigationStack by ContentView). It deliberately does NOT
/// create its own NavigationStack — when pushed, a nested stack would double
/// the navigation bars and break swipe-back.
///
/// macOS uses `MacSettingsView` (tabbed Settings scene) and `SystemWindowView`
/// instead.
struct SettingsView: View {
    @Environment(AppState.self) private var appState
    @AppStorage("show_message_costs") private var showMessageCosts = false
    @AppStorage("show_tool_activity") private var showToolActivity = false
    @State private var urlString = ""
    @State private var showError = false
    @State private var errorMessage = ""

    private var isConnected: Bool { appState.connectionStatus == .connected }

    var body: some View {
        formContent
            .navigationTitle("Settings")
    }

    @ViewBuilder
    private var formContent: some View {
        Form {
            #if os(iOS)
            Section {
                VStack(alignment: .center, spacing: 16) {
                    Image(systemName: "bubble.left.and.bubble.right")
                        .font(.system(size: 48))
                        .foregroundStyle(.blue)
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
                if appState.connectionStatus == .connecting {
                    Button(action: { appState.disconnect() }) {
                        HStack {
                            Spacer()
                            Text("Cancel")
                                .fontWeight(.semibold)
                                .foregroundStyle(.red)
                            Spacer()
                        }
                    }
                } else {
                    Button(action: connect) {
                        HStack {
                            Spacer()
                            Text("Connect")
                                .fontWeight(.semibold)
                            Spacer()
                        }
                    }
                    .disabled(urlString.isEmpty)
                }
            }

            if isConnected {
                Section {
                    Label("Connected", systemImage: "checkmark.circle.fill")
                        .foregroundStyle(.green)

                    Button(action: { appState.disconnect() }) {
                        Text("Disconnect")
                            .foregroundStyle(.red)
                    }
                }
            }

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
                    DefaultModelsView()
                } label: {
                    Label("Default Models", systemImage: "cpu")
                }
            } header: {
                Text("System")
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
        guard let url = URL(string: urlString), url.scheme != nil else {
            errorMessage = "Invalid URL"
            showError = true
            return
        }

        appState.saveServerURL(url)
        appState.connectToServer()
    }
}
