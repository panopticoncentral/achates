import SwiftUI

struct ThinkingView: View {
    let thinkingId: String
    let text: String
    let collapsed: Bool
    @State private var isExpanded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Button(action: {
                if collapsed {
                    withAnimation { isExpanded.toggle() }
                }
            }) {
                HStack(spacing: 5) {
                    Text(collapsed ? "Thought for a moment" : "Thinking...")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    if !collapsed {
                        ProgressView()
                            .controlSize(.mini)
                    }
                    if collapsed {
                        Image(systemName: isExpanded ? "chevron.down" : "chevron.right")
                            .font(.caption2.weight(.semibold))
                            .foregroundStyle(.secondary)
                    }
                }
                .contentShape(.rect)
                #if os(iOS)
                .frame(minHeight: collapsed ? 44 : nil, alignment: .leading)
                #endif
            }
            .buttonStyle(.plain)
            .allowsHitTesting(collapsed)
            .accessibilityAddTraits(collapsed ? .isButton : [])
            .accessibilityValue(collapsed ? (isExpanded ? "expanded" : "collapsed") : "")

            if isExpanded && collapsed {
                Text(text)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(
                        RoundedRectangle(cornerRadius: 6, style: .continuous)
                            .fill(Color.subtleSurface)
                    )
            }
        }
        .padding(.horizontal, InterfaceMetrics.messageContentInset)
        .padding(.vertical, 2)
    }
}
