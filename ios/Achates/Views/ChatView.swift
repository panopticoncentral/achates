import SwiftUI

struct ChatView: View {
    @Environment(AppState.self) private var appState
    let session: Session
    @State private var speechService = SpeechService()

    var body: some View {
        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(spacing: 12) {
                        ForEach(appState.messages) { message in
                            MessageBubble(message: message)
                                .id(message.id)
                        }
                    }
                    .padding(.horizontal, 12)
                    .padding(.vertical, 8)
                }
                .onChange(of: appState.messages.last?.id) { _, _ in
                    withAnimation(.easeOut(duration: 0.2)) {
                        if let lastId = appState.messages.last?.id {
                            proxy.scrollTo(lastId, anchor: .bottom)
                        }
                    }
                }
                .onChange(of: appState.messages.last?.textContent) { _, _ in
                    if let lastId = appState.messages.last?.id {
                        proxy.scrollTo(lastId, anchor: .bottom)
                    }
                }
            }

            Divider()

            ComposerView(speechService: speechService) { text in
                Task { await appState.sendMessage(text) }
            } onCancel: {
                Task { await appState.cancelStreaming() }
            }
        }
        .navigationTitle(session.title)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                connectionStatusIndicator
            }
        }
        .task {
            await appState.selectSession(session)
        }
    }

    @ViewBuilder
    private var connectionStatusIndicator: some View {
        switch appState.connectionStatus {
        case .connected:
            Circle()
                .fill(.green)
                .frame(width: 8, height: 8)
        case .connecting, .reconnecting:
            ProgressView()
                .controlSize(.mini)
        case .disconnected:
            Circle()
                .fill(.red)
                .frame(width: 8, height: 8)
        }
    }
}
