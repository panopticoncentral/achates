import Foundation

extension ContentBlock {
    var isActivity: Bool {
        switch self {
        case .thinking, .toolCall: return true
        default: return false
        }
    }
}

/// A display-only run. The first block keeps its identity as more activity arrives.
struct MessageBlockGroup: Identifiable, Equatable {
    var blocks: [ContentBlock]
    var id: String { blocks[0].id }
    var isActivity: Bool { blocks[0].isActivity }

    var toolCount: Int {
        blocks.filter { if case .toolCall = $0 { return true }; return false }.count
    }

    var failureCount: Int {
        blocks.filter { if case .toolCall(_, _, .failed, _) = $0 { return true }; return false }.count
    }

    var runningBlock: ContentBlock? {
        blocks.last {
            switch $0 {
            case .toolCall(_, _, .running, _), .thinking(_, _, false): return true
            default: return false
            }
        }
    }

    var summary: String {
        let count = toolCount
        let label = count > 0 ? "\(count) tool call\(count == 1 ? "" : "s")" : "Thinking"
        return failureCount > 0 ? "\(label) · \(failureCount) failed" : label
    }

    static func make(from blocks: [ContentBlock]) -> [Self] {
        var groups: [Self] = []
        for block in blocks {
            if block.isActivity, groups.last?.isActivity == true {
                groups[groups.count - 1].blocks.append(block)
            } else {
                groups.append(Self(blocks: [block]))
            }
        }
        return groups
    }
}

/// History stores each model iteration separately; live streaming uses one message.
/// Join activity at those boundaries for the same presentation without changing history.
struct PresentedMessage {
    var message: ChatMessage
    var sourceIDs: [String]

    static func make(from messages: [ChatMessage]) -> [Self] {
        var result: [Self] = []
        for message in messages {
            if let previous = result.last,
               previous.message.role == .assistant, message.role == .assistant,
               previous.message.blocks.last?.isActivity == true,
               message.blocks.first?.isActivity == true,
               message.timestamp.timeIntervalSince(previous.message.timestamp) < 300,
               !hasAudio(previous.message), !hasAudio(message) {
                let index = result.count - 1
                result[index].message.blocks.append(contentsOf: message.blocks)
                result[index].sourceIDs.append(message.id)
                if let usage = message.usage {
                    let prior = result[index].message.usage
                    result[index].message.usage = MessageUsage(
                        inputTokens: (prior?.inputTokens ?? 0) + usage.inputTokens,
                        outputTokens: (prior?.outputTokens ?? 0) + usage.outputTokens,
                        cost: (prior?.cost ?? 0) + usage.cost)
                }
            } else {
                result.append(Self(message: message, sourceIDs: [message.id]))
            }
        }
        return result
    }

    private static func hasAudio(_ message: ChatMessage) -> Bool {
        message.audioTurnId != nil || !message.audioTranscript.isEmpty || message.audioError != nil
    }
}
