import SwiftUI
import MarkdownUI

extension View {
    func conversationMarkdown() -> some View {
        self
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
                            .fill(Color.subtleSurface)
                    )
            }
    }
}
