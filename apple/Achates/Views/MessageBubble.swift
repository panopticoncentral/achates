import SwiftUI
import MarkdownUI
import UniformTypeIdentifiers
import QuickLook

enum BubblePosition {
    case alone, first, middle, last

    var topPadding: CGFloat {
        switch self {
        case .alone, .first: return 8
        case .middle, .last: return 2
        }
    }
}

struct MessageBubble: View {
    @Environment(AppState.self) private var appState
    let message: ChatMessage
    var position: BubblePosition = .alone
    var agent: Agent? = nil
    var isLastAssistantMessage: Bool = false
    var isLastUserMessage: Bool = false
    var isStreaming: Bool = false
    var onResubmit: (() -> Void)? = nil
    var onBeginEdit: (() -> Void)? = nil
    @AppStorage("show_message_costs") private var showMessageCosts = false
    @State private var confirmResubmit = false
    @State private var preview = AttachmentPreview()
    @State private var imageRetry = 0

    var body: some View {
        HStack(alignment: .bottom, spacing: 6) {
            if message.role == .assistant {
                if showAvatar, let agent {
                    AgentAvatar(agent: agent, size: 28)
                } else {
                    Color.clear.frame(width: 28, height: 28)
                }
            } else {
                Spacer(minLength: 48)
            }

            VStack(alignment: message.role == .user ? .trailing : .leading, spacing: 2) {
                ForEach(MessageBlockGroup.make(from: visibleBlocks)) { group in
                    if group.isActivity {
                        ActivityGroupView(group: group)
                    } else if let block = group.blocks.first {
                        blockView(block)
                    }
                }

                if showMessageCosts, message.role == .assistant, let usage = message.usage {
                    Text(formatCost(usage))
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .padding(.leading, 4)
                }

                if message.role == .assistant, let turnId = message.audioTurnId, !message.audioTranscript.isEmpty {
                    Button {
                        appState.speechPlayer.replay(turnId: turnId)
                    } label: {
                        Image(systemName: "play.circle")
                            .font(.body)
                            .foregroundStyle(.secondary)
                            .frame(width: InterfaceMetrics.actionSize, height: InterfaceMetrics.actionSize)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Replay audio")
                    .padding(.leading, 4)
                }

                if message.role == .assistant, let err = message.audioError {
                    HStack(spacing: 4) {
                        Image(systemName: "speaker.slash")
                        Text("Speech unavailable")
                    }
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .padding(.leading, 4)
                    .help(err)
                    .accessibilityLabel("Speech unavailable: \(err)")
                }

                // Only the actively-streaming message shows the indicator — a stale
                // empty assistant message (failed/aborted turn reloaded from disk)
                // must not animate "typing" forever.
                if visibleBlocks.isEmpty && message.role == .assistant && isStreaming {
                    TypingIndicator()
                        .padding(.horizontal, 12)
                        .padding(.vertical, 10)
                        .background(
                            RoundedRectangle(cornerRadius: 18, style: .continuous)
                                .fill(Color.messageSurface)
                        )
                        .accessibilityElement()
                        .accessibilityLabel(agent.map { "\($0.displayName) is typing" } ?? "Assistant is typing")
                }
            }

            if message.role == .assistant {
                Spacer(minLength: 48)
            }
        }
        // Cap line length on wide windows — unbounded bubbles read badly past
        // ~80 characters and nothing else constrains them on a big Mac display.
        .frame(maxWidth: 700, alignment: message.role == .user ? .trailing : .leading)
        .frame(maxWidth: .infinity, alignment: message.role == .user ? .trailing : .leading)
        .quickLookPreview($preview.url)
        .onChange(of: preview.url) { _, url in if url == nil { preview.clearFiles() } }
        .alert("Attachment Unavailable", isPresented: Binding(
            get: { preview.error != nil }, set: { if !$0 { preview.error = nil } }
        )) { Button("OK") { preview.error = nil } } message: { Text(preview.error ?? "") }
        .overlay {
            if preview.isLoading { ProgressView("Loading image…").padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12)) }
        }
    }

    private var showAvatar: Bool {
        position == .last || position == .alone
    }

    private var visibleBlocks: [ContentBlock] {
        message.blocks
    }

    @ViewBuilder
    private func blockView(_ block: ContentBlock) -> some View {
        switch block {
        case .text(_, let text):
            textBubble(text)

        case .thinking(let id, let text, let collapsed):
            ThinkingView(thinkingId: id, text: text, collapsed: collapsed)

        case .toolCall(let id, let name, let status, let result):
            ToolCallView(toolId: id, name: name, status: status, result: result)

        case .image(_, let data, _):
            imageBubble(data)

        case .document(_, let data, let name, let mime):
            Button { preview.show(data: data, name: name, mime: mime) } label: {
                documentChip(name: name, mime: mime)
            }
            .buttonStyle(.plain)
            .help("Preview attachment")

        case .agentTurn(let id, let agentName, let text, let collapsed):
            AgentTurnView(agentTurnId: id, agentName: agentName, text: text, collapsed: collapsed)

        case .remoteImage(_, let url):
            remoteImageBubble(url)
        }
    }

    @ViewBuilder
    private func imageBubble(_ data: Data) -> some View {
        if let image = PlatformImage(data: data) {
            Button { preview.show(data: data, name: nil, mime: "image/jpeg") } label: {
                Image(platformImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .frame(maxWidth: 320)
                    .clipShape(RoundedRectangle(cornerRadius: 16))
            }
            .buttonStyle(.plain)
            .accessibilityLabel(message.role == .user ? "Preview image from you" : "Preview image from assistant")
            .help("Preview image")
        }
    }

    /// Chip for a non-image attachment (PDF, text file) on a user message —
    /// mirrors the composer's document chip so the sent file stays visible.
    private func documentChip(name: String?, mime: String) -> some View {
        HStack(spacing: 8) {
            Image(systemName: mime == "application/pdf" ? "doc.richtext" : "doc.text")
                .font(.title3)
            VStack(alignment: .leading, spacing: 1) {
                Text(name ?? "Document")
                    .font(.subheadline.weight(.medium))
                    .lineLimit(1)
                Text(mime == "application/pdf" ? "PDF" : "Text")
                    .font(.caption2)
                    .opacity(0.7)
            }
        }
        .foregroundStyle(message.role == .user ? .white : .primary)
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .background(
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .fill(message.role == .user ? Color.accentColor : Color.messageSurface)
        )
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Attached document: \(name ?? "Document")")
    }

    @ViewBuilder
    private func remoteImageBubble(_ url: URL) -> some View {
        AsyncImage(url: url) { phase in
            switch phase {
            case .success(let image):
                Button { Task { await preview.show(remoteURL: url) } } label: {
                    image.resizable().aspectRatio(contentMode: .fit)
                        .frame(maxWidth: 320)
                        .clipShape(RoundedRectangle(cornerRadius: 16))
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Preview image")
            case .failure:
                Button { imageRetry += 1 } label: {
                    Label("Retry Image", systemImage: "arrow.clockwise")
                        .padding().background(Color.messageSurface, in: RoundedRectangle(cornerRadius: 12))
                }
                .buttonStyle(.plain)
            default:
                ProgressView()
                    .frame(width: 120, height: 80)
            }
        }
        .id(imageRetry)
    }

    private func textBubble(_ text: String) -> some View {
        HStack(spacing: 0) {
            Markdown(text)
                .conversationMarkdown()
        }
        .padding(.horizontal, InterfaceMetrics.messageContentInset)
        .padding(.vertical, 8)
        .background(bubbleShape.fill(bubbleColor))
        .textSelection(.enabled)
        .contextMenu {
            Button {
                copyToClipboard(text)
            } label: {
                Label("Copy", systemImage: "doc.on.doc")
            }

            if message.role == .user {
                if onResubmit != nil {
                    Button {
                        confirmResubmit = true
                    } label: {
                        Label("Resubmit From Here…", systemImage: "arrow.counterclockwise")
                    }
                }
                if isLastUserMessage, let onBeginEdit {
                    Button {
                        onBeginEdit()
                    } label: {
                        Label("Edit & Resubmit", systemImage: "pencil")
                    }
                }
            }

            if message.role == .assistant && isLastAssistantMessage {
                if let onResubmit {
                    Button {
                        onResubmit()
                    } label: {
                        Label("Regenerate Response", systemImage: "arrow.counterclockwise")
                    }
                }
            }
        }
        .confirmationDialog("Resubmit this message?", isPresented: $confirmResubmit, titleVisibility: .visible) {
            Button("Resubmit and Replace Later Messages", role: .destructive) { onResubmit?() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This replaces this message and everything after it with a new response.")
        }
        .accessibilityLabel(accessibilityText(text))
    }

    /// VoiceOver reading for a text bubble — attributes the speaker so
    /// interleaved user/assistant turns aren't ambiguous in the rotor.
    private func accessibilityText(_ text: String) -> String {
        if message.role == .user { return "You said: \(text)" }
        if let name = agent?.displayName { return "\(name) said: \(text)" }
        return text
    }

    private func copyToClipboard(_ text: String) {
        #if os(iOS)
        UIPasteboard.general.string = text
        #else
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        #endif
    }

    private func formatCost(_ usage: MessageUsage) -> String {
        let cost = usage.cost
        let tokens = usage.inputTokens + usage.outputTokens
        let costStr: String
        if cost < 0.01 {
            costStr = String(format: "$%.4f", cost)
        } else if cost < 1.0 {
            costStr = String(format: "$%.3f", cost)
        } else {
            costStr = String(format: "$%.2f", cost)
        }
        let tokenStr: String
        if tokens >= 1000 {
            tokenStr = String(format: "%.1fk tokens", Double(tokens) / 1000.0)
        } else {
            tokenStr = "\(tokens) tokens"
        }
        return "\(costStr) · \(tokenStr)"
    }

    private var bubbleColor: Color {
        message.role == .user ? .outgoingMessageSurface : .messageSurface
    }

    /// Messenger-style rounded rect with variable corner radii for grouped bubbles.
    private var bubbleShape: some Shape {
        let isUser = message.role == .user
        let large: CGFloat = 18
        let small: CGFloat = 4

        let topLeading: CGFloat
        let topTrailing: CGFloat
        let bottomLeading: CGFloat
        let bottomTrailing: CGFloat

        switch position {
        case .alone:
            topLeading = large; topTrailing = large
            bottomLeading = large; bottomTrailing = large
        case .first:
            topLeading = large; topTrailing = large
            bottomLeading = isUser ? large : small
            bottomTrailing = isUser ? small : large
        case .middle:
            topLeading = isUser ? large : small
            topTrailing = isUser ? small : large
            bottomLeading = isUser ? large : small
            bottomTrailing = isUser ? small : large
        case .last:
            topLeading = isUser ? large : small
            topTrailing = isUser ? small : large
            bottomLeading = large; bottomTrailing = large
        }

        return UnevenRoundedRectangle(
            topLeadingRadius: topLeading,
            bottomLeadingRadius: bottomLeading,
            bottomTrailingRadius: bottomTrailing,
            topTrailingRadius: topTrailing,
            style: .continuous
        )
    }
}

/// Animated three-dot typing indicator, like iMessage.
struct TypingIndicator: View {
    @State private var animating = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 4) {
            ForEach(0..<3, id: \.self) { i in
                Circle()
                    .fill(Color.secondary)
                    .frame(width: 7, height: 7)
                    .scaleEffect(reduceMotion || animating ? 1.0 : 0.5)
                    .opacity(reduceMotion || animating ? 1.0 : 0.4)
                    .animation(
                        reduceMotion ? nil : .easeInOut(duration: 0.5)
                        .repeatForever(autoreverses: true)
                        .delay(Double(i) * 0.15),
                        value: animating
                    )
            }
        }
        .onAppear { animating = true }
    }
}
