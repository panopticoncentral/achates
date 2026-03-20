import Foundation

/// A timeline segment — one session within the unified chat timeline.
struct TimelineSegment: Identifiable, Sendable, Equatable {
    let id: String
    let agentId: String
    let created: Date
    let updated: Date
    var messages: [ChatMessage]

    static func fromTimeline(_ payload: [String: JSONValue], agentId: String) -> [TimelineSegment] {
        guard let segmentArray = payload["segments"]?.arrayValue else { return [] }
        return segmentArray.compactMap { value -> TimelineSegment? in
            guard let dict = value.objectValue else { return nil }
            return TimelineSegment.from(dict, agentId: agentId)
        }
    }

    static func from(_ dict: [String: JSONValue], agentId: String) -> TimelineSegment? {
        guard let id = dict["id"]?.stringValue else { return nil }

        let created = parseDate(dict["created"]) ?? Date()
        let updated = parseDate(dict["updated"]) ?? Date()

        let messages: [ChatMessage]
        if let messagesArray = dict["messages"]?.arrayValue {
            messages = messagesArray.compactMap { parseMessage($0) }
        } else {
            messages = []
        }

        return TimelineSegment(
            id: id,
            agentId: agentId,
            created: created,
            updated: updated,
            messages: messages
        )
    }

    private static func parseDate(_ value: JSONValue?) -> Date? {
        guard let str = value?.stringValue else { return nil }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.date(from: str) ?? ISO8601DateFormatter().date(from: str)
    }

    private static func parseMessage(_ value: JSONValue) -> ChatMessage? {
        guard let dict = value.objectValue,
              let typeStr = dict["role"]?.stringValue else { return nil }

        let id = dict["id"]?.stringValue ?? UUID().uuidString
        var timestamp = Date()
        if let ts = dict["timestamp"]?.intValue {
            timestamp = Date(timeIntervalSince1970: Double(ts) / 1000.0)
        } else if let ts = dict["timestamp"]?.doubleValue {
            timestamp = Date(timeIntervalSince1970: ts / 1000.0)
        }

        switch typeStr {
        case "user":
            if dict["hidden"]?.boolValue == true { return nil }
            let text = dict["text"]?.stringValue ?? ""
            return ChatMessage(id: id, role: .user, text: text, timestamp: timestamp)
        case "assistant":
            // Parse content blocks from assistant message
            var blocks: [ContentBlock] = []
            if let content = dict["content"]?.arrayValue {
                for item in content {
                    guard let itemDict = item.objectValue,
                          let itemType = itemDict["type"]?.stringValue else { continue }
                    switch itemType {
                    case "text":
                        let text = itemDict["text"]?.stringValue ?? ""
                        blocks.append(.text(text))
                    case "thinking":
                        let text = itemDict["thinking"]?.stringValue ?? ""
                        let thinkingId = itemDict["id"]?.stringValue ?? UUID().uuidString
                        blocks.append(.thinking(id: thinkingId, text: text, collapsed: true))
                    case "tool_call":
                        let toolId = itemDict["id"]?.stringValue ?? UUID().uuidString
                        let name = itemDict["name"]?.stringValue ?? "unknown"
                        blocks.append(.toolCall(id: toolId, name: name, status: .completed, result: nil))
                    default:
                        break
                    }
                }
            }
            return ChatMessage(id: id, role: .assistant, blocks: blocks, timestamp: timestamp)
        case "tool_result":
            // Tool results are displayed inline with the assistant message, skip as standalone
            return nil
        case "summary":
            let text = dict["summary"]?.stringValue ?? ""
            return ChatMessage(id: id, role: .assistant, text: text, timestamp: timestamp)
        default:
            return nil
        }
    }
}
