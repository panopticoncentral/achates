import SwiftUI

extension Color {
    static var conversationCanvas: Color {
        #if os(macOS)
        Color(nsColor: .textBackgroundColor)
        #else
        Color(uiColor: .systemBackground)
        #endif
    }

    static var conversationInputSurface: Color {
        #if os(macOS)
        Color.primary.opacity(0.04)
        #else
        .messageSurface
        #endif
    }

    static var conversationReplySurface: Color {
        #if os(macOS)
        .clear
        #else
        .messageSurface
        #endif
    }

    /// Keep content readable for every system accent, including yellow and gray.
    static var outgoingMessageSurface: Color {
        #if os(macOS)
        Color.primary.opacity(0.06)
        #else
        .accentColor.opacity(0.16)
        #endif
    }

    static var messageSurface: Color {
        #if os(macOS)
        Color(nsColor: .controlBackgroundColor)
        #else
        Color(uiColor: .secondarySystemBackground)
        #endif
    }

    static var subtleSurface: Color {
        #if os(macOS)
        Color(nsColor: .textBackgroundColor)
        #else
        Color(uiColor: .tertiarySystemBackground)
        #endif
    }
}

enum InterfaceMetrics {
    static let readingWidth: CGFloat = 740
    static var messageContentInset: CGFloat {
        #if os(macOS)
        16
        #else
        12
        #endif
    }
    static var actionSize: CGFloat {
        #if os(iOS)
        44
        #else
        30
        #endif
    }
}

struct InlineNotice: View {
    let message: String
    var symbol = "exclamationmark.triangle"
    var actionTitle: String? = nil
    var action: (() -> Void)? = nil

    var body: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 12) { label; actionButton }
            VStack(alignment: .leading, spacing: 8) { label; actionButton }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary.opacity(0.5))
    }

    private var label: some View {
        Label(message, systemImage: symbol)
            .font(.callout)
            .frame(maxWidth: .infinity, alignment: .leading)
            .fixedSize(horizontal: false, vertical: true)
    }

    @ViewBuilder private var actionButton: some View {
        if let actionTitle, let action {
            Button(actionTitle, action: action)
                .buttonStyle(.bordered)
                .fixedSize()
        }
    }
}
