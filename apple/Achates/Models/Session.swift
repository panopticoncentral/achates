import Foundation

/// A timeline segment — one session within the unified chat timeline.
struct TimelineSegment: Identifiable, Sendable, Equatable {
    let id: String
    let agentId: String
    let created: Date
    let updated: Date
    var messages: [ChatMessage]

    static func fromTimeline(_ payload: [String: JSONValue], agentId: String, serverURL: URL?) -> [TimelineSegment] {
        guard let segmentArray = payload["segments"]?.arrayValue else { return [] }
        return segmentArray.compactMap { value -> TimelineSegment? in
            guard let dict = value.objectValue else { return nil }
            return TimelineSegment.from(dict, agentId: agentId, serverURL: serverURL)
        }
    }

    static func from(_ dict: [String: JSONValue], agentId: String, serverURL: URL?) -> TimelineSegment? {
        guard let id = dict["id"]?.stringValue else { return nil }

        let created = parseDate(dict["created"]) ?? Date()
        let updated = parseDate(dict["updated"]) ?? Date()

        let messages: [ChatMessage]
        if let messagesArray = dict["messages"]?.arrayValue {
            messages = messagesArray.compactMap { parseMessage($0, serverURL: serverURL) }
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

    private static func parseMessage(_ value: JSONValue, serverURL: URL?) -> ChatMessage? {
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
                    case "image":
                        if let block = parseImageBlock(itemDict, serverURL: serverURL) {
                            blocks.append(block)
                        }
                    default:
                        break
                    }
                }
            }
            return ChatMessage(id: id, role: .assistant, blocks: blocks, timestamp: timestamp)
        case "tool_result":
            // Extract image from tool result if present
            if let imageUrl = dict["image_url"]?.stringValue,
               let serverURL,
               let fullURL = URL(string: imageUrl, relativeTo: serverURL) {
                return ChatMessage(
                    id: id,
                    role: .assistant,
                    blocks: [.remoteImage(id: UUID().uuidString, url: fullURL)],
                    timestamp: timestamp
                )
            }
            // Also check content array for image blocks
            if let content = dict["content"]?.arrayValue {
                for item in content {
                    guard let itemDict = item.objectValue,
                          let itemType = itemDict["type"]?.stringValue,
                          itemType == "image",
                          let block = parseImageBlock(itemDict, serverURL: serverURL) else { continue }
                    return ChatMessage(
                        id: id,
                        role: .assistant,
                        blocks: [block],
                        timestamp: timestamp
                    )
                }
            }
            return nil
        case "summary":
            let text = dict["summary"]?.stringValue ?? ""
            return ChatMessage(id: id, role: .assistant, text: text, timestamp: timestamp)
        default:
            return nil
        }
    }

    /// Parse an image content block, preferring URL-based loading over inline base64.
    private static func parseImageBlock(_ dict: [String: JSONValue], serverURL: URL?) -> ContentBlock? {
        // Prefer URL-based image (lightweight, fetched on demand)
        if let urlPath = dict["url"]?.stringValue,
           let serverURL,
           let fullURL = URL(string: urlPath, relativeTo: serverURL) {
            return .remoteImage(id: UUID().uuidString, url: fullURL)
        }
        // Fall back to inline base64 data
        if let b64 = dict["data"]?.stringValue,
           !b64.isEmpty,
           let data = Data(base64Encoded: b64) {
            let mimeType = dict["mime_type"]?.stringValue ?? "image/jpeg"
            return .image(id: UUID().uuidString, data: data, mimeType: mimeType)
        }
        return nil
    }
}
