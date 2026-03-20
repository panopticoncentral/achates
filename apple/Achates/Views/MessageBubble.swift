import SwiftUI
import MarkdownUI

struct MessageBubble: View {
    let message: ChatMessage

    var body: some View {
        HStack(alignment: .top) {
            if message.role == .user {
                Spacer(minLength: 60)
            }

            VStack(alignment: message.role == .user ? .trailing : .leading, spacing: 6) {
                ForEach(message.blocks) { block in
                    blockView(block)
                }

                if message.blocks.isEmpty && message.role == .assistant {
                    ProgressView()
                        .controlSize(.small)
                        .frame(height: 20)
                }
            }

            if message.role == .assistant {
                Spacer(minLength: 60)
            }
        }
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
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .background(
                RoundedRectangle(cornerRadius: 18, style: .continuous)
                    .fill(message.role == .user ? Color.blue : Color(.systemGray5))
            )
            .textSelection(.enabled)
    }
}
