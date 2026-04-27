import Foundation

/// Lightweight session metadata for the session list.
struct SessionInfo: Identifiable, Sendable, Equatable {
    let id: String
    var title: String?
    let preview: String?
    let created: Date
    let updated: Date

    static func fromList(_ payload: [String: JSONValue]) -> [SessionInfo] {
        guard let arr = payload["sessions"]?.arrayValue else { return [] }
        return arr.compactMap { value -> SessionInfo? in
            guard let dict = value.objectValue else { return nil }
            return SessionInfo.from(dict: dict)
        }
    }

    static func from(dict: [String: JSONValue]) -> SessionInfo? {
        guard let id = dict["id"]?.stringValue else { return nil }
        return SessionInfo(
            id: id,
            title: dict["title"]?.stringValue,
            preview: dict["preview"]?.stringValue,
            created: parseDate(dict["created"]),
            updated: parseDate(dict["updated"])
        )
    }

    private static func parseDate(_ value: JSONValue?) -> Date {
        if let ms = value?.intValue ?? value?.doubleValue.map({ Int($0) }) {
            return Date(timeIntervalSince1970: Double(ms) / 1000.0)
        }
        return Date()
    }
}

/// Parse messages from a sessions.get response payload.
func parseSessionMessages(_ payload: [String: JSONValue], serverURL: URL?) -> [ChatMessage] {
    guard let messagesArray = payload["messages"]?.arrayValue else { return [] }
    return messagesArray.compactMap { parseMessage($0, serverURL: serverURL) }
}

func parseMessage(_ value: JSONValue, serverURL: URL?) -> ChatMessage? {
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
        var blocks: [ContentBlock] = []
        if let content = dict["content"]?.arrayValue {
            for item in content {
                guard let itemDict = item.objectValue,
                      let itemType = itemDict["type"]?.stringValue else { continue }
                switch itemType {
                case "text":
                    let text = itemDict["text"]?.stringValue ?? ""
                    blocks.append(.text(id: UUID().uuidString, text))
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
        if let errorText = dict["error"]?.stringValue, !errorText.isEmpty {
            blocks.append(.text(id: UUID().uuidString, "⚠️ \(errorText)"))
        }
        var parsedUsage: MessageUsage?
        if let usageDict = dict["usage"]?.objectValue,
           let input = usageDict["input"]?.intValue,
           let output = usageDict["output"]?.intValue,
           let costDict = usageDict["cost"]?.objectValue {
            let cost = costDict["total"]?.doubleValue ?? 0
            parsedUsage = MessageUsage(inputTokens: input, outputTokens: output, cost: cost)
        }
        return ChatMessage(id: id, role: .assistant, blocks: blocks, timestamp: timestamp, usage: parsedUsage)
    case "tool_result":
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

private func parseImageBlock(_ dict: [String: JSONValue], serverURL: URL?) -> ContentBlock? {
    if let urlPath = dict["url"]?.stringValue,
       let serverURL,
       let fullURL = URL(string: urlPath, relativeTo: serverURL) {
        return .remoteImage(id: UUID().uuidString, url: fullURL)
    }
    if let b64 = dict["data"]?.stringValue,
       !b64.isEmpty,
       let data = Data(base64Encoded: b64) {
        let mimeType = dict["mime_type"]?.stringValue ?? "image/jpeg"
        return .image(id: UUID().uuidString, data: data, mimeType: mimeType)
    }
    return nil
}
