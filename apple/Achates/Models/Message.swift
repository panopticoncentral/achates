import Foundation

struct MessageUsage: Sendable, Equatable {
    let inputTokens: Int
    let outputTokens: Int
    let cost: Double
}

enum MessageRole: String, Sendable, Codable {
    case user
    case assistant
}

enum ContentBlock: Identifiable, Sendable, Equatable {
    case text(id: String, String)
    case thinking(id: String, text: String, collapsed: Bool)
    case toolCall(id: String, name: String, status: ToolCallStatus, result: String?)
    case image(id: String, data: Data, mimeType: String)
    case agentTurn(id: String, agentName: String, text: String, collapsed: Bool)
    case remoteImage(id: String, url: URL)

    var id: String {
        switch self {
        case .text(let id, _): return "text-\(id)"
        case .thinking(let id, _, _): return "thinking-\(id)"
        case .toolCall(let id, _, _, _): return "tool-\(id)"
        case .image(let id, _, _): return "image-\(id)"
        case .agentTurn(let id, _, _, _): return "agent-\(id)"
        case .remoteImage(let id, _): return "image-\(id)"
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
    var usage: MessageUsage?

    var textContent: String {
        blocks.compactMap { block in
            if case .text(_, let t) = block { return t }
            return nil
        }.joined()
    }

    init(id: String = UUID().uuidString, role: MessageRole, text: String, timestamp: Date = Date()) {
        self.id = id
        self.role = role
        self.blocks = [.text(id: UUID().uuidString, text)]
        self.timestamp = timestamp
    }

    init(id: String = UUID().uuidString, role: MessageRole, blocks: [ContentBlock] = [], timestamp: Date = Date(), usage: MessageUsage? = nil) {
        self.id = id
        self.role = role
        self.blocks = blocks
        self.timestamp = timestamp
        self.usage = usage
    }

    mutating func appendText(_ delta: String) {
        if case .text(let id, let existing) = blocks.last {
            blocks[blocks.count - 1] = .text(id: id, existing + delta)
        } else {
            blocks.append(.text(id: UUID().uuidString, delta))
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

    mutating func appendImage(data: Data, mimeType: String) {
        blocks.append(.image(id: UUID().uuidString, data: data, mimeType: mimeType))
    }

    /// Begins a fresh agent-turn block. Always appends a new, uniquely-identified
    /// block — every utterance gets its own bubble. Uniqueness of `agentTurnId`
    /// is the caller's responsibility (the WebSocket layer mints a fresh UUID per
    /// `agent_turn.start`). Subsequent `appendAgentTurnDelta`/`endAgentTurn` calls
    /// target the most-recently-started block via `lastIndex`.
    mutating func appendAgentTurn(_ delta: String, agentTurnId: String, agentName: String) {
        blocks.append(.agentTurn(id: agentTurnId, agentName: agentName, text: delta, collapsed: false))
    }

    mutating func appendAgentTurnDelta(_ delta: String) {
        if let index = blocks.lastIndex(where: {
            if case .agentTurn = $0 { return true }
            return false
        }), case .agentTurn(let id, let name, let existing, let collapsed) = blocks[index] {
            blocks[index] = .agentTurn(id: id, agentName: name, text: existing + delta, collapsed: collapsed)
        }
    }

    /// Finalizes the most-recently-started agent-turn block. If `text` is
    /// non-empty it becomes the authoritative final text — this fills the
    /// no-delta initiator line (start+end, no deltas) and reconciles the
    /// streamed target line against the server's full text. If `text` is empty,
    /// the accumulated streamed text is preserved. Always collapses the block.
    mutating func endAgentTurn(_ text: String) {
        if let index = blocks.lastIndex(where: {
            if case .agentTurn = $0 { return true }
            return false
        }), case .agentTurn(let id, let name, let existing, _) = blocks[index] {
            blocks[index] = .agentTurn(id: id, agentName: name, text: text.isEmpty ? existing : text, collapsed: true)
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
