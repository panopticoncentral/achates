import Foundation
import SwiftUI

@Observable
@MainActor
final class AppState {
    var connectionStatus: ConnectionStatus = .disconnected
    var serverURL: URL?
    var agents: [Agent] = []
    var currentAgent: Agent?
    var sessions: [Session] = []
    var currentSession: Session?
    var messages: [ChatMessage] = []
    var isStreaming = false
    var streamingMessageId: String?
    var client: WebSocketClient?
    var error: String?

    init() {
        if let urlString = UserDefaults.standard.string(forKey: "achates_server_url"),
           let url = URL(string: urlString) {
            serverURL = url
        }
    }

    func saveServerURL(_ url: URL) {
        serverURL = url
        UserDefaults.standard.set(url.absoluteString, forKey: "achates_server_url")
    }

    func connectToServer() {
        guard let url = serverURL else { return }
        if client == nil {
            client = WebSocketClient(appState: self)
        }
        // If no agent selected yet, connect with a placeholder — will update after handshake
        let agentId = currentAgent?.id ?? "default"
        client?.connect(url: url, agent: agentId)
    }

    func disconnect() {
        client?.disconnect()
        connectionStatus = .disconnected
        agents = []
        currentAgent = nil
        sessions = []
        currentSession = nil
        messages = []
    }

    func selectAgent(_ agent: Agent) async {
        currentAgent = agent
        sessions = []
        messages = []
        currentSession = nil

        guard client != nil else { return }

        do {
            let payload = try await client?.sendRequest(method: "sessions.list", params: [
                "agent": .string(agent.id)
            ])
            if let payload {
                sessions = Session.fromList(payload, agentId: agent.id)
            }
        } catch {
            self.error = "Failed to list sessions: \(error.localizedDescription)"
        }
    }

    func selectSession(_ session: Session) async {
        currentSession = session
        messages = []

        do {
            let payload = try await client?.sendRequest(method: "sessions.switch", params: [
                "agent": .string(session.agentId),
                "session_id": .string(session.id),
            ])

            if let messagesArray = payload?["messages"]?.arrayValue {
                messages = messagesArray.compactMap { parseMessage($0) }
            }
        } catch {
            self.error = "Failed to switch session: \(error.localizedDescription)"
        }
    }

    func createNewSession() async {
        guard let agent = currentAgent else { return }

        do {
            let payload = try await client?.sendRequest(method: "sessions.new", params: [
                "agent": .string(agent.id)
            ])
            if let payload, let session = Session.from(payload, agentId: agent.id) {
                sessions.insert(session, at: 0)
                currentSession = session
                messages = []
            }
        } catch {
            self.error = "Failed to create session: \(error.localizedDescription)"
        }
    }

    func deleteSession(_ session: Session) async {
        do {
            _ = try await client?.sendRequest(method: "sessions.delete", params: [
                "agent": .string(session.agentId),
                "session_id": .string(session.id),
            ])
            sessions.removeAll { $0.id == session.id }
            if currentSession?.id == session.id {
                currentSession = nil
                messages = []
            }
        } catch {
            self.error = "Failed to delete session: \(error.localizedDescription)"
        }
    }

    func renameSession(sessionId: String?, title: String) {
        let targetId = sessionId ?? currentSession?.id
        if let index = sessions.firstIndex(where: { $0.id == targetId }) {
            sessions[index].title = title
        }
        if currentSession?.id == targetId {
            currentSession?.title = title
        }
    }

    func sendMessage(_ text: String) async {
        guard client != nil, currentSession != nil else { return }

        let userMessage = ChatMessage(role: .user, text: text)
        messages.append(userMessage)
        isStreaming = true

        let assistantId = UUID().uuidString
        streamingMessageId = assistantId
        let assistantMessage = ChatMessage(id: assistantId, role: .assistant, blocks: [])
        messages.append(assistantMessage)

        await client?.sendMessage(text)
    }

    func cancelStreaming() async {
        await client?.cancelStreaming()
    }

    // MARK: - Streaming updates

    func appendTextDelta(_ delta: String) {
        guard let id = streamingMessageId,
              let index = messages.lastIndex(where: { $0.id == id }) else { return }
        messages[index].appendText(delta)
    }

    func appendThinkingDelta(_ delta: String, thinkingId: String) {
        guard let id = streamingMessageId,
              let index = messages.lastIndex(where: { $0.id == id }) else { return }
        messages[index].appendThinking(delta, thinkingId: thinkingId)
    }

    func collapseThinking(thinkingId: String) {
        guard let id = streamingMessageId,
              let index = messages.lastIndex(where: { $0.id == id }) else { return }
        messages[index].collapseThinking(thinkingId)
    }

    func addToolCall(toolId: String, name: String) {
        guard let id = streamingMessageId,
              let index = messages.lastIndex(where: { $0.id == id }) else { return }
        messages[index].addToolCall(toolId: toolId, name: name)
    }

    func completeToolCall(toolId: String, result: String?, success: Bool) {
        guard let id = streamingMessageId,
              let index = messages.lastIndex(where: { $0.id == id }) else { return }
        messages[index].completeToolCall(toolId: toolId, result: result, success: success)
    }

    func finalizeStreamingMessage() {
        // Message is complete but stream might continue (e.g., tool results leading to more text)
    }

    // MARK: - Helpers

    private func parseMessage(_ value: JSONValue) -> ChatMessage? {
        guard let dict = value.objectValue,
              let roleStr = dict["role"]?.stringValue,
              let role = MessageRole(rawValue: roleStr) else { return nil }

        let id = dict["id"]?.stringValue ?? UUID().uuidString
        let text = dict["text"]?.stringValue ?? ""
        var timestamp = Date()
        if let ts = dict["timestamp"]?.stringValue {
            timestamp = ISO8601DateFormatter().date(from: ts) ?? Date()
        }

        return ChatMessage(id: id, role: role, text: text, timestamp: timestamp)
    }
}
