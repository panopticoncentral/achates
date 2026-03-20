import Foundation

enum MessageRole: String, Sendable, Codable {
    case user
    case assistant
}

enum ContentBlock: Identifiable, Sendable, Equatable {
    case text(String)
    case thinking(id: String, text: String, collapsed: Bool)
    case toolCall(id: String, name: String, status: ToolCallStatus, result: String?)

    var id: String {
        switch self {
        case .text(let t): return "text-\(t.hashValue)"
        case .thinking(let id, _, _): return "thinking-\(id)"
        case .toolCall(let id, _, _, _): return "tool-\(id)"
        }
    }
}

enum ToolCallStatus: String, Sendable, Equatable {
    case running
    case completed
    case failed
}

struct ChatMessage: Identifiable, Sendable, Equatable {
    let id: String
    let role: MessageRole
    var blocks: [ContentBlock]
    let timestamp: Date

    var textContent: String {
        blocks.compactMap { block in
            if case .text(let t) = block { return t }
            return nil
        }.joined()
    }

    init(id: String = UUID().uuidString, role: MessageRole, text: String, timestamp: Date = Date()) {
        self.id = id
        self.role = role
        self.blocks = [.text(text)]
        self.timestamp = timestamp
    }

    init(id: String = UUID().uuidString, role: MessageRole, blocks: [ContentBlock] = [], timestamp: Date = Date()) {
        self.id = id
        self.role = role
        self.blocks = blocks
        self.timestamp = timestamp
    }

    mutating func appendText(_ delta: String) {
        if case .text(let existing) = blocks.last {
            blocks[blocks.count - 1] = .text(existing + delta)
        } else {
            blocks.append(.text(delta))
        }
    }

    mutating func appendThinking(_ delta: String, thinkingId: String) {
        if let index = blocks.firstIndex(where: {
            if case .thinking(let id, _, _) = $0 { return id == thinkingId }
            return false
        }) {
            if case .thinking(let id, let existing, let collapsed) = blocks[index] {
                blocks[index] = .thinking(id: id, text: existing + delta, collapsed: collapsed)
            }
        } else {
            blocks.append(.thinking(id: thinkingId, text: delta, collapsed: false))
        }
    }

    mutating func collapseThinking(_ thinkingId: String) {
        if let index = blocks.firstIndex(where: {
            if case .thinking(let id, _, _) = $0 { return id == thinkingId }
            return false
        }) {
            if case .thinking(let id, let text, _) = blocks[index] {
                blocks[index] = .thinking(id: id, text: text, collapsed: true)
            }
        }
    }

    mutating func addToolCall(toolId: String, name: String) {
        blocks.append(.toolCall(id: toolId, name: name, status: .running, result: nil))
    }

    mutating func completeToolCall(toolId: String, result: String?, success: Bool) {
        if let index = blocks.firstIndex(where: {
            if case .toolCall(let id, _, _, _) = $0 { return id == toolId }
            return false
        }) {
            if case .toolCall(let id, let name, _, _) = blocks[index] {
                blocks[index] = .toolCall(
                    id: id,
                    name: name,
                    status: success ? .completed : .failed,
                    result: result
                )
            }
        }
    }
}
