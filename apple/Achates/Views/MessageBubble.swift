import SwiftUI
import MarkdownUI
import UniformTypeIdentifiers

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
    let message: ChatMessage
    var position: BubblePosition = .alone
    var agent: Agent? = nil
    var isLastAssistantMessage: Bool = false
    var isLastUserMessage: Bool = false
    var isStreaming: Bool = false
    var onRetry: (() -> Void)? = nil
    var onResubmit: (() -> Void)? = nil
    var onBeginEdit: (() -> Void)? = nil
    @AppStorage("show_message_costs") private var showMessageCosts = false
    @State private var fullscreenImageData: Data? = nil
    @State private var fullscreenImageURL: URL? = nil

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
                ForEach(visibleBlocks) { block in
                    blockView(block)
                }

                if showMessageCosts, message.role == .assistant, let usage = message.usage {
                    Text(formatCost(usage))
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .padding(.leading, 4)
                }

                if visibleBlocks.isEmpty && message.role == .assistant && (message.blocks.isEmpty || isStreaming) {
                    TypingIndicator()
                        .padding(.horizontal, 12)
                        .padding(.vertical, 10)
                        .background(
                            RoundedRectangle(cornerRadius: 18, style: .continuous)
                                .fill(Color(.systemGray5))
                        )
                }
            }

            if message.role == .assistant {
                Spacer(minLength: 48)
            }
        }
        .fullScreenImageViewer(imageData: $fullscreenImageData)
        .fullScreenImageViewer(imageURL: $fullscreenImageURL)
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

        case .remoteImage(_, let url):
            remoteImageBubble(url)
        }
    }

    @ViewBuilder
    private func imageBubble(_ data: Data) -> some View {
        #if os(iOS)
        if let uiImage = UIImage(data: data) {
            Image(uiImage: uiImage)
                .resizable()
                .aspectRatio(contentMode: .fit)
                .frame(maxWidth: 260)
                .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
                .onTapGesture { fullscreenImageData = data }
                .accessibilityLabel(message.role == .user ? "Image from you" : "Image from assistant")
                .accessibilityAddTraits(.isImage)
        }
        #else
        if let nsImage = NSImage(data: data) {
            Image(nsImage: nsImage)
                .resizable()
                .aspectRatio(contentMode: .fit)
                .frame(maxWidth: 260)
                .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
                .onTapGesture { fullscreenImageData = data }
                .accessibilityLabel(message.role == .user ? "Image from you" : "Image from assistant")
                .accessibilityAddTraits(.isImage)
        }
        #endif
    }

    @ViewBuilder
    private func remoteImageBubble(_ url: URL) -> some View {
        AsyncImage(url: url) { phase in
            switch phase {
            case .success(let image):
                image
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .frame(maxWidth: 260)
                    .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
                    .onTapGesture { fullscreenImageURL = url }
            case .failure:
                RoundedRectangle(cornerRadius: 18, style: .continuous)
                    .fill(Color.gray.opacity(0.2))
                    .frame(width: 120, height: 80)
                    .overlay {
                        Image(systemName: "photo")
                            .foregroundStyle(.secondary)
                    }
            default:
                ProgressView()
                    .frame(width: 120, height: 80)
            }
        }
        .accessibilityLabel("Image")
        .accessibilityAddTraits(.isImage)
    }

    private func textBubble(_ text: String) -> some View {
        HStack(spacing: 0) {
            Markdown(text)
                .markdownTextStyle {
                    if message.role == .user {
                        ForegroundColor(.white)
                    }
                }
                .markdownBlockStyle(\.heading1) { configuration in
                    configuration.label.markdownTextStyle { FontSize(.em(1.15)); FontWeight(.semibold) }
                }
                .markdownBlockStyle(\.heading2) { configuration in
                    configuration.label.markdownTextStyle { FontSize(.em(1.1)); FontWeight(.semibold) }
                }
                .markdownBlockStyle(\.heading3) { configuration in
                    configuration.label.markdownTextStyle { FontSize(.em(1.05)); FontWeight(.semibold) }
                }
                .markdownBlockStyle(\.codeBlock) { configuration in
                    configuration.label
                        .markdownTextStyle {
                            FontFamilyVariant(.monospaced)
                            FontSize(.em(0.9))
                        }
                        .padding(10)
                        .background(
                            RoundedRectangle(cornerRadius: 8)
                                .fill(message.role == .user
                                      ? Color.white.opacity(0.15)
                                      : Color(.systemGray6))
                        )
                }


        }
        .padding(.horizontal, 12)
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
                if let onResubmit {
                    Button {
                        onResubmit()
                    } label: {
                        Label("Resubmit", systemImage: "arrow.counterclockwise")
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
                        Label("Retry", systemImage: "arrow.counterclockwise")
                    }
                } else if let onRetry {
                    Button {
                        onRetry()
                    } label: {
                        Label("Retry", systemImage: "arrow.counterclockwise")
                    }
                }
            }
        }
        .accessibilityLabel(message.role == .user ? "You said: \(text)" : text)
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
        message.role == .user ? .accentColor : Color(.systemGray5)
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

    var body: some View {
        HStack(spacing: 4) {
            ForEach(0..<3, id: \.self) { i in
                Circle()
                    .fill(Color.secondary)
                    .frame(width: 7, height: 7)
                    .scaleEffect(animating ? 1.0 : 0.5)
                    .opacity(animating ? 1.0 : 0.4)
                    .animation(
                        .easeInOut(duration: 0.5)
                        .repeatForever(autoreverses: true)
                        .delay(Double(i) * 0.15),
                        value: animating
                    )
            }
        }
        .onAppear { animating = true }
    }
}


// MARK: - Fullscreen Image Viewer

private struct FullScreenImageViewer: View {
    let imageData: Data?
    let imageURL: URL?
    let onDismiss: () -> Void

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            if let data = imageData {
                #if os(iOS)
                if let uiImage = UIImage(data: data) {
                    Image(uiImage: uiImage)
                        .resizable()
                        .aspectRatio(contentMode: .fit)
                        .ignoresSafeArea()
                }
                #else
                if let nsImage = NSImage(data: data) {
                    Image(nsImage: nsImage)
                        .resizable()
                        .aspectRatio(contentMode: .fit)
                }
                #endif
            } else if let url = imageURL {
                AsyncImage(url: url) { phase in
                    if case .success(let image) = phase {
                        image
                            .resizable()
                            .aspectRatio(contentMode: .fit)
                            .ignoresSafeArea()
                    } else {
                        ProgressView().tint(.white)
                    }
                }
            }
        }
        .overlay(alignment: .topTrailing) {
            Button(action: onDismiss) {
                Image(systemName: "xmark.circle.fill")
                    .font(.title)
                    .symbolRenderingMode(.palette)
                    .foregroundStyle(.white, .white.opacity(0.3))
            }
            .buttonStyle(.plain)
            .padding()
        }
        .overlay(alignment: .bottomTrailing) {
            if let data = imageData {
                ShareLink(item: ImageTransferable(data: data), preview: SharePreview("Image")) {
                    Image(systemName: "square.and.arrow.up.circle.fill")
                        .font(.title)
                        .symbolRenderingMode(.palette)
                        .foregroundStyle(.white, .white.opacity(0.3))
                }
                .buttonStyle(.plain)
                .padding()
            }
        }
    }
}

private struct ImageTransferable: Transferable {
    let data: Data

    static var transferRepresentation: some TransferRepresentation {
        DataRepresentation(exportedContentType: .image) { item in
            item.data
        }
    }
}

private extension View {
    func fullScreenImageViewer(imageData: Binding<Data?>) -> some View {
        #if os(iOS)
        self.fullScreenCover(isPresented: .init(
            get: { imageData.wrappedValue != nil },
            set: { if !$0 { imageData.wrappedValue = nil } }
        )) {
            FullScreenImageViewer(imageData: imageData.wrappedValue, imageURL: nil) {
                imageData.wrappedValue = nil
            }
        }
        #else
        self.sheet(isPresented: .init(
            get: { imageData.wrappedValue != nil },
            set: { if !$0 { imageData.wrappedValue = nil } }
        )) {
            FullScreenImageViewer(imageData: imageData.wrappedValue, imageURL: nil) {
                imageData.wrappedValue = nil
            }
            .frame(minWidth: 600, minHeight: 500)
        }
        #endif
    }

    func fullScreenImageViewer(imageURL: Binding<URL?>) -> some View {
        #if os(iOS)
        self.fullScreenCover(isPresented: .init(
            get: { imageURL.wrappedValue != nil },
            set: { if !$0 { imageURL.wrappedValue = nil } }
        )) {
            FullScreenImageViewer(imageData: nil, imageURL: imageURL.wrappedValue) {
                imageURL.wrappedValue = nil
            }
        }
        #else
        self.sheet(isPresented: .init(
            get: { imageURL.wrappedValue != nil },
            set: { if !$0 { imageURL.wrappedValue = nil } }
        )) {
            FullScreenImageViewer(imageData: nil, imageURL: imageURL.wrappedValue) {
                imageURL.wrappedValue = nil
            }
            .frame(minWidth: 600, minHeight: 500)
        }
        #endif
    }
}
