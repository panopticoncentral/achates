import Foundation
import Observation

/// Live transcript and reply state belong to a conversation, not the selected view.
@Observable
@MainActor
final class ChatSessionState {
    var messages: [ChatMessage] = []
    var isStreaming = false
    var streamingMessageId: String?
    var failedSend: AppState.FailedSend?

    // MARK: - Streaming updates

    func appendTextDelta(_ delta: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].appendText(delta)
    }

    func appendThinkingDelta(_ delta: String, thinkingId: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].appendThinking(delta, thinkingId: thinkingId)
    }

    func collapseThinking(thinkingId: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].collapseThinking(thinkingId)
    }

    func appendImage(data: Data, mimeType: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].appendImage(data: data, mimeType: mimeType)
    }

    func addToolCall(toolId: String, name: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].addToolCall(toolId: toolId, name: name)
    }

    func completeToolCall(toolId: String, result: String?, success: Bool) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].completeToolCall(toolId: toolId, result: result, success: success)
    }

    func startAgentTurn(agentTurnId: String, agentName: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].appendAgentTurn("", agentTurnId: agentTurnId, agentName: agentName)
    }

    func appendAgentTurnDelta(_ delta: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].appendAgentTurnDelta(delta)
    }

    func endAgentTurn(_ text: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].endAgentTurn(text)
    }

    func finalizeStreamingMessage(usage: MessageUsage? = nil) {
        if let usage, let id = streamingMessageId, let index = lastMessageIndex(id: id) {
            messages[index].usage = usage
        }
    }

    /// Tag the currently-streaming assistant message with a turn id and append a
    /// sentence to its audio transcript. The first audio block of a turn binds
    /// the id; later blocks for the same turn append text only.
    func recordAudioMetadata(turnId: String, sentenceIndex: Int, text: String) {
        let index: Int? = {
            if let id = streamingMessageId, let i = lastMessageIndex(id: id) { return i }
            return messages.lastIndex(where: { $0.role == .assistant })
        }()
        guard let index else { return }
        if messages[index].audioTurnId == nil {
            messages[index].audioTurnId = turnId
        }
        if messages[index].audioTurnId == turnId {
            messages[index].audioTranscript.append(text)
        }
    }

    /// Surface an `audio.error` on whichever assistant message owns the turn,
    /// falling back to the latest assistant message when the turn id hasn't
    /// been bound yet (synth was unavailable before any sentence landed).
    func recordAudioError(turnId: String, message: String) {
        let index: Int? = {
            if let i = messages.lastIndex(where: { $0.role == .assistant && $0.audioTurnId == turnId }) {
                return i
            }
            if let id = streamingMessageId, let i = lastMessageIndex(id: id) { return i }
            return messages.lastIndex(where: { $0.role == .assistant })
        }()
        guard let index else { return }
        messages[index].audioError = message
    }

    // MARK: - Private helpers

    private func lastMessageIndex(id: String) -> Int? {
        messages.lastIndex(where: { $0.id == id })
    }
}
