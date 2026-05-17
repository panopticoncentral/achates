import SwiftUI
import MarkdownUI

/// Renders a turn spoken by *another* agent during an inter-agent chat.
/// Visually distinct from a normal assistant bubble: an accent-tinted card
/// with a labelled header so it's clear a different agent is talking.
struct AgentTurnView: View {
    let agentTurnId: String  // kept for ContentBlock.agentTurn API symmetry (siblings store an id too)
    let agentName: String
    let text: String
    let collapsed: Bool  // accepted for block-API symmetry; inter-agent turns always render expanded

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 4) {
                Image(systemName: "person.2.fill")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(.tint)
                Text(agentName)
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(.tint)
            }

            Markdown(text)
                .markdownTextStyle {
                    FontSize(.em(0.95))
                }
                .markdownBlockStyle(\.codeBlock) { configuration in
                    configuration.label
                        .markdownTextStyle {
                            FontFamilyVariant(.monospaced)
                            FontSize(.em(0.85))
                        }
                        .padding(8)
                        .background(
                            RoundedRectangle(cornerRadius: 6)
                                .fill(Color(.systemGray6))
                        )
                }
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(Color.accentColor.opacity(0.08))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .strokeBorder(Color.accentColor.opacity(0.25), lineWidth: 1)
        )
        .frame(maxWidth: .infinity, alignment: .leading)
        .textSelection(.enabled)
        .accessibilityLabel("\(agentName) said: \(text)")
    }
}
