import SwiftUI

struct ToolCallView: View {
    let toolId: String
    let name: String
    let status: ToolCallStatus
    let result: String?
    @State private var isExpanded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Button(action: {
                if result != nil {
                    withAnimation { isExpanded.toggle() }
                }
            }) {
                HStack(spacing: 8) {
                    statusIcon
                    Text(name)
                        .font(.caption)
                        .fontWeight(.medium)
                    Spacer()
                    if result != nil {
                        Image(systemName: isExpanded ? "chevron.down" : "chevron.right")
                            .font(.caption2)
                            .foregroundStyle(.tertiary)
                    }
                }
                .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)

            if isExpanded, let result {
                Text(result)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(
                        RoundedRectangle(cornerRadius: 6, style: .continuous)
                            .fill(Color(.systemGray6))
                    )
                    .lineLimit(20)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(Color(.systemGray6).opacity(0.5))
        )
    }

    @ViewBuilder
    private var statusIcon: some View {
        switch status {
        case .running:
            ProgressView()
                .controlSize(.mini)
        case .completed:
            Image(systemName: "checkmark.circle.fill")
                .font(.caption)
                .foregroundStyle(.green)
        case .failed:
            Image(systemName: "xmark.circle.fill")
                .font(.caption)
                .foregroundStyle(.red)
        }
    }
}
