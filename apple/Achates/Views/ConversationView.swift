import SwiftUI

/// Full-screen hands-free conversation ("call") screen with a live transcript.
/// Renders the agent, a state indicator, the recent turns, and the live partial
/// transcript of the current utterance. Reuses the normal session — every spoken
/// turn is an ordinary message in `appState.messages`.
struct ConversationView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss
    let agent: Agent

    @State private var controller: ConversationController?

    var body: some View {
        VStack(spacing: 16) {
            header
            transcript
            Spacer(minLength: 8)
            if let banner = controller?.banner {
                Text(banner)
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal)
            }
            stateIndicator
            endButton
        }
        .padding()
        .task {
            let c = ConversationController(appState: appState)
            controller = c
            c.begin()
        }
        .onDisappear { controller?.end() }
        .onChange(of: appState.isStreaming) { _, streaming in
            guard !streaming else { return }
            // Surface a one-time note if the just-finished turn produced no audio.
            // Gate on banner == nil so a multi-turn text-only session doesn't
            // re-set it on every turn.
            if controller?.banner == nil,
               appState.messages.last(where: { $0.role == .assistant })?.audioTurnId == nil {
                controller?.banner = "No voice configured for \(liveAgent.displayName) — replies are text-only. Set a voice in the agent's settings."
            }
            controller?.turnDidComplete()
        }
        .onChange(of: appState.speechPlayer.isPlaying) { _, playing in
            guard !playing else { return }
            controller?.playbackDidFinish()
        }
        .onChange(of: appState.connectionStatus) { _, status in
            if status != .connected { controller?.connectionDidDrop() }
        }
    }

    private var liveAgent: Agent {
        appState.agents.first { $0.id == agent.id } ?? agent
    }

    private var header: some View {
        VStack(spacing: 8) {
            AgentAvatar(agent: liveAgent, size: 88)
            Text(liveAgent.displayName)
                .font(.title2.weight(.semibold))
        }
        .padding(.top, 24)
    }

    private var transcript: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 10) {
                    ForEach(appState.messages) { message in
                        transcriptLine(message)
                            .id(message.id)
                    }
                    // Live partial transcript of what the user is currently saying.
                    if let live = controller?.liveTranscript, !live.isEmpty,
                       controller?.state == .listening {
                        Text(live)
                            .font(.body)
                            .foregroundStyle(.tertiary)
                            .frame(maxWidth: .infinity, alignment: .trailing)
                            .id("live-partial")
                    }
                }
                .padding(.horizontal, 4)
            }
            .onChange(of: appState.messages.count) { _, _ in
                if let last = appState.messages.last {
                    withAnimation(.easeOut(duration: 0.2)) {
                        proxy.scrollTo(last.id, anchor: .bottom)
                    }
                }
            }
            // Keep the growing live-partial line in view while the user speaks.
            .onChange(of: controller?.liveTranscript) { _, _ in
                proxy.scrollTo("live-partial", anchor: .bottom)
            }
        }
        .frame(maxHeight: .infinity)
    }

    @ViewBuilder
    private func transcriptLine(_ message: ChatMessage) -> some View {
        let isUser = message.role == .user
        let text = message.textContent
        if !text.isEmpty {
            Text(text)
                .font(.body)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(
                    RoundedRectangle(cornerRadius: 16, style: .continuous)
                        .fill(isUser ? Color.blue.opacity(0.15) : Color(.systemGray6))
                )
                .frame(maxWidth: .infinity, alignment: isUser ? .trailing : .leading)
        }
    }

    private var stateIndicator: some View {
        let (label, symbol, tint) = statePresentation
        return HStack(spacing: 10) {
            Image(systemName: symbol)
                .symbolEffect(.variableColor.iterative, isActive: controller?.state == .listening || controller?.state == .speaking)
                .font(.title3)
                .foregroundStyle(tint)
            Text(label)
                .font(.headline)
                .foregroundStyle(.secondary)
        }
        .frame(height: 28)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(label)
    }

    private var statePresentation: (String, String, Color) {
        switch controller?.state {
        case .listening: return ("Listening", "waveform", .blue)
        case .sending:   return ("Thinking", "ellipsis", .secondary)
        case .speaking:  return ("Speaking", "speaker.wave.2.fill", .green)
        case .paused:    return ("Paused", "pause.circle", .orange)
        case .failed:    return ("Unavailable", "exclamationmark.triangle", .red)
        default:         return ("…", "circle", .secondary)
        }
    }

    private var endButton: some View {
        Button {
            dismiss()
        } label: {
            Image(systemName: "phone.down.fill")
                .font(.title2)
                .foregroundStyle(.white)
                .frame(width: 64, height: 64)
                .background(Circle().fill(.red))
        }
        .buttonStyle(.plain)
        .padding(.bottom, 12)
        .accessibilityLabel("End conversation")
    }
}
