import SwiftUI

struct SettingsView: View {
    @Environment(AppState.self) private var appState
    @State private var urlString = ""
    @State private var showError = false
    @State private var errorMessage = ""

    var body: some View {
        NavigationStack {
            Form {
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

                Section("Server") {
                    TextField("Server URL", text: $urlString, prompt: Text("ws://192.168.1.100:5000/ws/v2"))
                        .textContentType(.URL)
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                        .keyboardType(.URL)
                }

                Section {
                    Button(action: connect) {
                        HStack {
                            Spacer()
                            if appState.connectionStatus == .connecting {
                                ProgressView()
                                    .padding(.trailing, 8)
                            }
                            Text(appState.connectionStatus == .connecting ? "Connecting..." : "Connect")
                                .fontWeight(.semibold)
                            Spacer()
                        }
                    }
                    .disabled(urlString.isEmpty || appState.connectionStatus == .connecting)
                }

                if appState.connectionStatus == .connected {
                    Section {
                        Label("Connected", systemImage: "checkmark.circle.fill")
                            .foregroundStyle(.green)
                    }
                }
            }
            .navigationTitle("Settings")
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
        }
    }

    private func connect() {
        guard let url = URL(string: urlString) else {
            errorMessage = "Invalid URL"
            showError = true
            return
        }

        appState.saveServerURL(url)
        appState.connectToServer()
    }
}
