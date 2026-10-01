import SwiftUI
import MarkdownUI

extension View {
    func conversationMarkdown() -> some View {
        self
            #if os(macOS)
            .markdownTextStyle(\.text) {
                FontFamily(.system())
                FontSize(ConversationTypography.macBodySize)
                FontWeight(.regular)
            }
            .markdownBlockStyle(\.paragraph) { configuration in
                configuration.label
                    .fixedSize(horizontal: false, vertical: true)
                    .relativeLineSpacing(.em(0.25))
                    .markdownMargin(top: .zero, bottom: .em(0.85))
            }
            #endif
            .markdownBlockStyle(\.heading1) { configuration in
                configuration.label
                    .markdownTextStyle { FontSize(.em(1.15)); FontWeight(.semibold) }
                    #if os(macOS)
                    .markdownMargin(top: .em(0.8), bottom: .em(0.35))
                    #endif
            }
            .markdownBlockStyle(\.heading2) { configuration in
                configuration.label
                    .markdownTextStyle { FontSize(.em(1.1)); FontWeight(.semibold) }
                    #if os(macOS)
                    .markdownMargin(top: .em(0.8), bottom: .em(0.35))
                    #endif
            }
            .markdownBlockStyle(\.heading3) { configuration in
                configuration.label
                    .markdownTextStyle { FontSize(.em(1.05)); FontWeight(.semibold) }
                    #if os(macOS)
                    .markdownMargin(top: .em(0.8), bottom: .em(0.35))
                    #endif
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
                            .fill(Color.subtleSurface)
                    )
            }
    }
}
