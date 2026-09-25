import SwiftUI

struct ActivityGroupView: View {
    let group: MessageBlockGroup
    @State private var isExpanded = false

    var body: some View {
        // Keep the same view identity when a single step grows into a run.
        if group.blocks.count == 1, let block = group.blocks.first {
            activityView(block)
        } else {
            VStack(alignment: .leading, spacing: 2) {
                Button {
                    withAnimation { isExpanded.toggle() }
                } label: {
                    HStack(spacing: 5) {
                        if group.runningBlock != nil {
                            ProgressView().controlSize(.mini)
                        }
                        VStack(alignment: .leading, spacing: 2) {
                            Text(group.summary)
                            if let running = group.runningBlock {
                                Text(runningLabel(running))
                            }
                        }
                        if group.failureCount > 0 {
                            Image(systemName: "exclamationmark.circle")
                                .foregroundStyle(.orange)
                        }
                        Image(systemName: isExpanded ? "chevron.down" : "chevron.right")
                            .font(.caption2.weight(.semibold))
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    #if os(iOS)
                    .frame(minHeight: 44, alignment: .leading)
                    #endif
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityValue(isExpanded ? "expanded" : "collapsed")
                .accessibilityHint("Show or hide activity details")
                .padding(.horizontal, InterfaceMetrics.messageContentInset)

                if isExpanded {
                    VStack(alignment: .leading, spacing: 2) {
                        ForEach(group.blocks) { block in
                            activityView(block)
                        }
                    }
                    .padding(.leading, 8)
                }
            }
            .padding(.vertical, 2)
        }
    }

    private func runningLabel(_ block: ContentBlock) -> String {
        if case .toolCall(_, let name, _, _) = block {
            return ToolCallView.runningLabel(for: name)
        }
        return "Thinking..."
    }

    @ViewBuilder
    private func activityView(_ block: ContentBlock) -> some View {
        switch block {
        case .thinking(let id, let text, let collapsed):
            ThinkingView(thinkingId: id, text: text, collapsed: collapsed)
        case .toolCall(let id, let name, let status, let result):
            ToolCallView(toolId: id, name: name, status: status, result: result)
        default:
            EmptyView()
        }
    }
}
