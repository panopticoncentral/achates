import SwiftUI

struct ToolCallView: View {
    let toolId: String
    let name: String
    let status: ToolCallStatus
    let result: String?
    @State private var isExpanded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Button(action: {
                if result != nil {
                    withAnimation { isExpanded.toggle() }
                }
            }) {
                HStack(spacing: 5) {
                    if status == .running {
                        ProgressView()
                            .controlSize(.mini)
                    }
                    Text(label)
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                    if result != nil && status != .running {
                        Image(systemName: isExpanded ? "chevron.down" : "chevron.right")
                            .font(.system(size: 8, weight: .semibold))
                            .foregroundStyle(.quaternary)
                    }
                }
            }
            .buttonStyle(.plain)

            if isExpanded, let result {
                Text(result)
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
                    .padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(
                        RoundedRectangle(cornerRadius: 6, style: .continuous)
                            .fill(Color(.systemGray6))
                    )
                    .lineLimit(20)
            }
        }
        .padding(.leading, 4)
        .padding(.vertical, 2)
    }

    private var label: String {
        if status == .failed {
            return Self.failedLabel(for: name)
        }
        return status == .running
            ? Self.runningLabel(for: name)
            : Self.completedLabel(for: name)
    }

    private static func runningLabel(for tool: String) -> String {
        switch tool {
        case "web_search": return "Searching the web..."
        case "web_fetch": return "Reading webpage..."
        case "memory": return "Checking memory..."
        case "mail": return "Checking email..."
        case "calendar": return "Checking calendar..."
        case "todo": return "Checking tasks..."
        case "cost": return "Checking costs..."
        case "cron": return "Managing schedule..."
        case "imessage": return "Reading messages..."
        case "transcribe": return "Transcribing audio..."
        case "chat": return "Talking to another agent..."
        case "location": return "Getting location..."
        case "camera": return "Taking photo..."
        case "image": return "Generating image..."
        case "health": return "Checking health data..."
        case "session": return "Managing session..."
        case "profile": return "Updating profile..."
        default: return "Working..."
        }
    }

    private static func completedLabel(for tool: String) -> String {
        switch tool {
        case "web_search": return "Searched the web"
        case "web_fetch": return "Read webpage"
        case "memory": return "Checked memory"
        case "mail": return "Checked email"
        case "calendar": return "Checked calendar"
        case "todo": return "Checked tasks"
        case "cost": return "Checked costs"
        case "cron": return "Managed schedule"
        case "imessage": return "Read messages"
        case "transcribe": return "Transcribed audio"
        case "chat": return "Talked to another agent"
        case "location": return "Got location"
        case "camera": return "Took photo"
        case "image": return "Generated image"
        case "health": return "Checked health data"
        case "session": return "Managed session"
        case "profile": return "Updated profile"
        default: return "Used \(tool)"
        }
    }

    private static func failedLabel(for tool: String) -> String {
        switch tool {
        case "web_search": return "Web search failed"
        case "web_fetch": return "Failed to read webpage"
        case "memory": return "Memory check failed"
        case "mail": return "Email check failed"
        case "calendar": return "Calendar check failed"
        default: return "\(tool) failed"
        }
    }
}
