import SwiftUI
import MarkdownUI

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

    var body: some View {
        HStack(alignment: .bottom, spacing: 6) {
            if message.role == .assistant {
                // Small avatar on last message of a group, invisible spacer otherwise
                if showAvatar, let agent {
                    AgentAvatar(agent: agent, size: 28)
                } else {
                    Color.clear.frame(width: 28, height: 28)
                }
            } else {
                Spacer(minLength: 48)
            }

            VStack(alignment: message.role == .user ? .trailing : .leading, spacing: 2) {
                ForEach(message.blocks) { block in
                    blockView(block)
                }

                if message.blocks.isEmpty && message.role == .assistant {
                    ProgressView()
                        .controlSize(.small)
                        .frame(height: 20)
                        .padding(.leading, 4)
                }
            }

            if message.role == .assistant {
                Spacer(minLength: 48)
            }
        }
    }

    private var showAvatar: Bool {
        position == .last || position == .alone
    }

    @ViewBuilder
    private func blockView(_ block: ContentBlock) -> some View {
        switch block {
        case .text(let text):
            textBubble(text)

        case .thinking(let id, let text, let collapsed):
            ThinkingView(thinkingId: id, text: text, collapsed: collapsed)

        case .toolCall(let id, let name, let status, let result):
            ToolCallView(toolId: id, name: name, status: status, result: result)
        }
    }

    private func textBubble(_ text: String) -> some View {
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
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background(bubbleShape.fill(bubbleColor))
            .textSelection(.enabled)
    }

    private var bubbleColor: Color {
        message.role == .user ? .blue : Color(.systemGray5)
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
