import SwiftUI

/// Full-width strip shown while the socket is down or reconnecting. Renders
/// nothing when connected. Used above the chat transcript and pinned beneath
/// the macOS sidebar so a dropped connection is visible outside the open
/// conversation too (the lists otherwise look live while frozen).
struct ConnectionStatusBanner: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        if appState.connectionStatus == .disconnected {
            HStack(spacing: 6) {
                Image(systemName: "wifi.slash")
                    .font(.caption)
                Text("No connection")
                    .font(.caption.weight(.medium))
            }
            .foregroundStyle(.white)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 6)
            .background(.red.opacity(0.85))
            .accessibilityLabel("Disconnected from server")
        } else if appState.connectionStatus == .reconnecting {
            HStack(spacing: 6) {
                ProgressView()
                    .controlSize(.mini)
                    .tint(.white)
                Text("Reconnecting...")
                    .font(.caption.weight(.medium))
            }
            .foregroundStyle(.white)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 6)
            .background(.orange.opacity(0.85))
            .accessibilityLabel("Reconnecting to server")
        }
    }
}
