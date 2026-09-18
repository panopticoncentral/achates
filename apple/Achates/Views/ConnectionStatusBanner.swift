import SwiftUI

struct ConnectionStatusBanner: View {
    @Environment(AppState.self) private var appState
    var body: some View {
        switch appState.connectionStatus {
        case .connected: EmptyView()
        case .connecting, .reconnecting:
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text("Connecting to server…").font(.callout)
                Spacer()
                Button("Cancel") { appState.disconnect() }.buttonStyle(.borderless)
            }
            .padding(12)
            .background(.bar)
        case .disconnected:
            InlineNotice(message: "Offline. Your draft is kept while you reconnect.", symbol: "wifi.slash", actionTitle: "Reconnect") {
                appState.connectToServer()
            }
        }
    }
}
