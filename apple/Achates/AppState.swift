import Foundation
import SwiftUI
import UserNotifications

enum AgentEditError: LocalizedError {
    case notConnected
    case invalidResponse
    case reloadWarning(String)

    var errorDescription: String? {
        switch self {
        case .notConnected: return "Not connected to server."
        case .invalidResponse: return "Invalid response from server."
        case .reloadWarning(let msg): return msg
        }
    }
}

/// Navigation target for opening a specific session.
struct SessionSelection: Hashable {
    let agent: Agent
    let sessionId: String
}

@Observable
@MainActor
final class AppState {
    var connectionStatus: ConnectionStatus = .disconnected
    var serverURL: URL?
    var agents: [Agent] = []
    var currentAgent: Agent?
    var navigationPath = NavigationPath()
    var isStreaming = false
    var streamingMessageId: String?
    var client: WebSocketClient?
    var error: String?

    /// Sessions for the currently selected agent
    var sessions: [SessionInfo] = []
    var hasMoreSessions = false

    /// Messages for the currently open session
    var messages: [ChatMessage] = []
    var currentSessionId: String?

    /// Memory files across all agents (loaded on demand by MemoryListView).
    var memories: [MemoryInfo] = []

    /// Scheduled jobs across all agents (loaded on demand by JobsView).
    var jobs: [CronJobInfo] = []

    /// Bumped after `memories` is reloaded in response to a server `memory.updated`
    /// event. Carries the affected scope so detail views can detect concurrent edits.
    var memoryUpdateEvent: MemoryUpdateSignal?

    /// Bumped after `jobs` is reloaded in response to a server `jobs.updated` event.
    var jobsUpdateEvent: UUID?

    struct MemoryUpdateSignal: Equatable, Sendable {
        let scope: String
        let id: UUID
    }

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
        let agentId = currentAgent?.id ?? "default"
        client?.connect(url: url, agent: agentId)
    }

    func disconnect() {
        client?.disconnect()
        connectionStatus = .disconnected
        agents = []
        currentAgent = nil
        sessions = []
        messages = []
        currentSessionId = nil
    }

    func markCurrentAgentAsRead() {
        guard let agent = currentAgent, client != nil else { return }

        let latestTimestamp: Int? = messages.reversed().compactMap { msg -> Int? in
            return Int(msg.timestamp.timeIntervalSince1970 * 1000)
        }.first

        guard let ts = latestTimestamp else { return }

        if let index = agents.firstIndex(where: { $0.id == agent.id }), agents[index].unreadCount > 0 {
            agents[index].unreadCount = 0
            updateAppBadge()
        }

        Task {
            _ = try? await client?.sendRequest(method: "chat.read", params: [
                "agent": .string(agent.id),
                "timestamp": .int(ts),
            ])
        }
    }

    func updateAppBadge() {
        let total = agents.reduce(0) { $0 + $1.unreadCount }
        Task {
            try? await UNUserNotificationCenter.current().setBadgeCount(total)
        }
    }

    // MARK: - Session management

    /// Switch the currently selected agent. Clears session-view state
    /// (current session id and messages) when the agent actually changes
    /// so a previously-open chat from a different agent doesn't linger
    /// in the detail panel. Then loads the new agent's session list.
    func selectAgent(_ agent: Agent) async {
        if currentAgent?.id != agent.id {
            currentSessionId = nil
            messages = []
        }
        currentAgent = agent
        await loadSessions(for: agent)
    }

    func loadSessions(for agent: Agent) async {
        guard client != nil else { return }

        do {
            let payload = try await client?.sendRequest(method: "sessions.list", params: [
                "agent": .string(agent.id),
            ])
            if let payload {
                sessions = SessionInfo.fromList(payload)
                hasMoreSessions = payload["has_more"]?.boolValue ?? false
            }
        } catch {
            self.error = "Failed to load sessions: \(error.localizedDescription)"
        }
    }

    func loadMoreSessions(for agent: Agent) async {
        guard client != nil, hasMoreSessions, let oldest = sessions.last else { return }

        let beforeMs = Int(oldest.updated.timeIntervalSince1970 * 1000)

        do {
            let payload = try await client?.sendRequest(method: "sessions.list", params: [
                "agent": .string(agent.id),
                "before": .int(beforeMs),
            ])
            if let payload {
                let older = SessionInfo.fromList(payload)
                hasMoreSessions = payload["has_more"]?.boolValue ?? false
                sessions.append(contentsOf: older)
            }
        } catch {
            self.error = "Failed to load sessions: \(error.localizedDescription)"
        }
    }

    func createSession(for agent: Agent) async -> String? {
        guard client != nil else { return nil }

        do {
            let payload = try await client?.sendRequest(method: "sessions.create", params: [
                "agent": .string(agent.id),
            ])
            if let id = payload?["id"]?.stringValue {
                // Insert at the top of the list
                let newSession = SessionInfo(
                    id: id,
                    title: nil,
                    preview: nil,
                    created: Date(),
                    updated: Date()
                )
                sessions.insert(newSession, at: 0)
                return id
            }
        } catch {
            self.error = "Failed to create session: \(error.localizedDescription)"
        }
        return nil
    }

    func openSession(_ sessionId: String, for agent: Agent) async {
        guard client != nil else { return }
        currentSessionId = sessionId
        messages = []

        do {
            let payload = try await client?.sendRequest(method: "sessions.get", params: [
                "agent": .string(agent.id),
                "session_id": .string(sessionId),
            ])
            if let payload {
                messages = parseSessionMessages(payload, serverURL: serverURL)
            }
        } catch {
            self.error = "Failed to load session: \(error.localizedDescription)"
        }

        markCurrentAgentAsRead()
    }

    func deleteSession(_ sessionId: String, for agent: Agent) async {
        guard client != nil else { return }

        do {
            _ = try await client?.sendRequest(method: "sessions.delete", params: [
                "agent": .string(agent.id),
                "session_id": .string(sessionId),
            ])
            sessions.removeAll { $0.id == sessionId }
            if currentSessionId == sessionId {
                currentSessionId = nil
                messages = []
            }
        } catch {
            self.error = "Failed to delete session: \(error.localizedDescription)"
        }
    }

    func renameSession(_ sessionId: String, title: String, for agent: Agent) async {
        guard client != nil else { return }

        do {
            _ = try await client?.sendRequest(method: "sessions.rename", params: [
                "agent": .string(agent.id),
                "session_id": .string(sessionId),
                "title": .string(title),
            ])
            if let index = sessions.firstIndex(where: { $0.id == sessionId }) {
                sessions[index].title = title
            }
        } catch {
            self.error = "Failed to rename session: \(error.localizedDescription)"
        }
    }

    func deleteAllSessions(for agent: Agent) async {
        guard client != nil else { return }

        do {
            _ = try await client?.sendRequest(method: "sessions.delete_all", params: [
                "agent": .string(agent.id),
            ])
            sessions = []
            messages = []
            currentSessionId = nil
        } catch {
            self.error = "Failed to clear sessions: \(error.localizedDescription)"
        }
    }

    func updateSessionTitle(sessionId: String, title: String) {
        if let index = sessions.firstIndex(where: { $0.id == sessionId }) {
            sessions[index].title = title
        }
    }

    /// Upsert a session into the visible list. Updates fields if it already exists,
    /// inserts at the top otherwise. Re-sorts by `updated` desc so the list always
    /// reflects most-recent activity first. Only mutates state for the agent the
    /// list currently shows.
    func upsertSession(agentId: String, info: SessionInfo) {
        guard agentId == currentAgent?.id else { return }
        if let index = sessions.firstIndex(where: { $0.id == info.id }) {
            sessions[index] = info
        } else {
            sessions.append(info)
        }
        sessions.sort { $0.updated > $1.updated }
    }

    // MARK: - Agent management

    func loadAgentConfig(_ agent: Agent) async throws -> AgentEditModel {
        guard let payload = try await client?.sendRequest(method: "agent.get", params: [
            "agent": .string(agent.id),
        ]) else {
            throw AgentEditError.notConnected
        }
        guard let model = AgentEditModel.from(payload) else {
            throw AgentEditError.invalidResponse
        }
        return model
    }

    func saveAgentConfig(_ agent: Agent, config: AgentEditModel, original: AgentEditModel) async throws {
        var currentAgentId = agent.id

        if config.displayName != original.displayName && !config.displayName.isEmpty {
            guard let renamePayload = try await client?.sendRequest(method: "agent.rename", params: [
                "agent": .string(agent.id),
                "name": .string(config.displayName),
            ]) else {
                throw AgentEditError.notConnected
            }
            if let newId = renamePayload["id"]?.stringValue {
                currentAgentId = newId
            }
            await refreshAgents()
        }

        guard let payload = try await client?.sendRequest(method: "agent.update",
            params: config.toParams(agentId: currentAgentId)
        ) else {
            throw AgentEditError.notConnected
        }
        if let warning = payload["warning"]?.stringValue {
            throw AgentEditError.reloadWarning(warning)
        }
        await refreshAgents()
    }

    func deleteAgent(_ agent: Agent) async throws {
        guard let client else { throw AgentEditError.notConnected }
        _ = try await client.sendRequest(method: "agent.delete", params: [
            "agent": .string(agent.id),
        ])
        // Server broadcasts agents.changed, which triggers refreshAgents() in WebSocketClient.
        // Clear the current selection locally so the UI doesn't keep pointing at the deleted agent
        // before the broadcast lands.
        if currentAgent?.id == agent.id {
            currentAgent = nil
            currentSessionId = nil
            sessions = []
            messages = []
        }
    }

    func generateAvatar(_ agent: Agent, prompt: String, referenceImage: Data? = nil) async throws -> Data {
        var params: [String: JSONValue] = [
            "agent": .string(agent.id),
            "prompt": .string(prompt),
        ]
        if let imageData = referenceImage {
            params["image"] = .string(imageData.base64EncodedString())
        }
        guard let payload = try await client?.sendRequest(method: "agent.generate_avatar",
            params: params,
            timeout: .seconds(120)
        ) else {
            throw AgentEditError.notConnected
        }
        guard let b64 = payload["image"]?.stringValue,
              let data = Data(base64Encoded: b64) else {
            throw AgentEditError.reloadWarning("No image data returned")
        }
        return data
    }

    func loadAvailableTools() async throws -> [ToolInfo] {
        guard let payload = try await client?.sendRequest(method: "tools.list") else {
            throw AgentEditError.notConnected
        }
        guard case .array(let items) = payload["tools"] else { return [] }
        return items.compactMap { item -> ToolInfo? in
            guard let obj = item.objectValue,
                  let name = obj["name"]?.stringValue else { return nil }
            let label = obj["label"]?.stringValue ?? name
            return ToolInfo(name: name, label: label)
        }
    }

    func loadAvailableModels() async throws -> [ModelInfo] {
        guard let payload = try await client?.sendRequest(method: "models.list") else {
            throw AgentEditError.notConnected
        }
        return ModelInfo.fromList(payload)
    }

    /// Cached cost summaries keyed by "agent:period". Filled by `loadCostSummary`.
    var costSummaries: [String: CostSummary] = [:]

    func costSummary(agent: String, period: String) -> CostSummary? {
        costSummaries["\(agent):\(period)"]
    }

    func loadCostSummary(agent: String, period: String) async {
        guard let payload = try? await client?.sendRequest(method: "costs.summary", params: [
            "agent": .string(agent),
            "period": .string(period),
        ]),
              let summary = CostSummary.from(payload) else { return }
        costSummaries["\(agent):\(period)"] = summary
    }

    /// Invalidate cached cost summaries so the next read refetches from the server.
    /// Called when a chat turn finishes (the cost ledger was just updated).
    func invalidateCostSummaries() {
        costSummaries.removeAll()
    }

    // MARK: - Memory

    func loadMemories() async {
        guard let payload = try? await client?.sendRequest(method: "memory.list")
        else { return }
        memories = MemoryInfo.fromList(payload)
    }

    func loadMemory(scope: String) async -> String {
        guard let payload = try? await client?.sendRequest(method: "memory.get", params: [
            "scope": .string(scope),
        ]) else { return "" }
        return payload["content"]?.stringValue ?? ""
    }

    func saveMemory(scope: String, content: String) async throws {
        _ = try await client?.sendRequest(method: "memory.set", params: [
            "scope": .string(scope),
            "content": .string(content),
        ])
    }

    // MARK: - Scheduled jobs

    func loadJobs() async {
        guard let payload = try? await client?.sendRequest(method: "jobs.list")
        else { return }
        jobs = CronJobInfo.fromList(payload)
    }

    func setJobEnabled(agent: String, jobId: String, enabled: Bool) async throws {
        _ = try await client?.sendRequest(method: "jobs.update", params: [
            "agent": .string(agent),
            "id": .string(jobId),
            "enabled": .bool(enabled),
        ])
    }

    func deleteJob(agent: String, jobId: String) async throws {
        _ = try await client?.sendRequest(method: "jobs.delete", params: [
            "agent": .string(agent),
            "id": .string(jobId),
        ])
    }

    func runJob(agent: String, jobId: String) async throws {
        _ = try await client?.sendRequest(method: "jobs.run", params: [
            "agent": .string(agent),
            "id": .string(jobId),
        ])
    }

    // MARK: - Event handlers (called from WebSocketClient)

    func handleMemoryUpdated(scope: String) {
        Task {
            await loadMemories()
            memoryUpdateEvent = MemoryUpdateSignal(scope: scope, id: UUID())
        }
    }

    func handleJobsUpdated() {
        Task {
            await loadJobs()
            jobsUpdateEvent = UUID()
        }
    }

    func handleAgentRenamed(oldId: String?, newId: String?) async {
        let wasCurrentAgent = currentAgent?.id == oldId
        await refreshAgents()
        if wasCurrentAgent, let newId, let updated = agents.first(where: { $0.id == newId }) {
            currentAgent = updated
        }
    }

    func refreshAgents() async {
        guard let client, let payload = try? await client.sendRequest(method: "agents.list") else { return }
        agents = Agent.fromList(payload)
        if let current = currentAgent,
           let updated = agents.first(where: { $0.id == current.id }) {
            currentAgent = updated
        } else {
            // currentAgent was deleted (or never set); fall back to the first available agent.
            currentAgent = agents.first
        }
    }

    func handleScenePhaseChange(_ phase: ScenePhase) {
        guard phase == .active, serverURL != nil else { return }
        // iOS commonly suspends or kills the websocket on background; on foreground
        // make sure we're connected and that the visible view reflects current server state.
        if connectionStatus != .connected {
            connectToServer()
        } else {
            Task { await resyncCurrentView() }
        }
    }

    /// After a (re)connect or app foreground, re-fetch what the user is currently looking
    /// at. Events broadcast while the socket was down are gone — only an explicit pull
    /// guarantees the UI matches the server.
    func resyncCurrentView() async {
        guard let agent = currentAgent else { return }
        await loadSessions(for: agent)
        if let sessionId = currentSessionId,
           sessions.contains(where: { $0.id == sessionId }) {
            await reloadCurrentSessionMessages(agent: agent, sessionId: sessionId)
        }
    }

    private func reloadCurrentSessionMessages(agent: Agent, sessionId: String) async {
        guard let client else { return }
        do {
            let payload = try await client.sendRequest(method: "sessions.get", params: [
                "agent": .string(agent.id),
                "session_id": .string(sessionId),
            ])
            if let payload, !isStreaming {
                messages = parseSessionMessages(payload, serverURL: serverURL)
            }
        } catch {
            // Best-effort resync; surface as transient error only if user is on the chat
        }
    }

    // MARK: - Send message

    func sendMessage(_ text: String, attachments: [DraftAttachment] = []) async {
        guard client != nil, currentAgent != nil, currentSessionId != nil else { return }

        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        var blocks: [ContentBlock] = []
        if !trimmed.isEmpty {
            blocks.append(.text(id: UUID().uuidString, trimmed))
        }
        for attachment in attachments {
            blocks.append(.image(id: UUID().uuidString, data: attachment.data, mimeType: "image/jpeg"))
        }

        let userMessage = ChatMessage(role: .user, blocks: blocks)
        messages.append(userMessage)
        isStreaming = true

        let assistantId = UUID().uuidString
        streamingMessageId = assistantId
        let assistantMessage = ChatMessage(id: assistantId, role: .assistant, blocks: [])
        messages.append(assistantMessage)

        await client?.sendMessage(trimmed, attachments: attachments)
    }

    /// The most recent user message in the open session, if any.
    var lastUserMessage: ChatMessage? {
        messages.last(where: { $0.role == .user })
    }

    /// Reconstruct DraftAttachments from a stored user message's image blocks.
    /// Remote-only images (no local bytes) are dropped — the user can re-attach.
    func draftAttachments(from message: ChatMessage) -> [DraftAttachment] {
        message.blocks.compactMap { block in
            if case .image(_, let data, let mimeType) = block {
                return DraftAttachment(data: data, mime: mimeType)
            }
            return nil
        }
    }

    /// Rewind the latest turn — drop the last assistant response and any tool blocks,
    /// optionally replace the user prompt's text/attachments, then stream a fresh response.
    /// Pass `text: nil` / `attachments: nil` to preserve the original values.
    func resubmitLast(text: String?, attachments: [DraftAttachment]?) async {
        guard client != nil, currentAgent != nil, currentSessionId != nil else { return }
        guard let originalIndex = messages.lastIndex(where: { $0.role == .user }) else { return }

        let original = messages[originalIndex]
        let trimmedNewText = text?.trimmingCharacters(in: .whitespacesAndNewlines)
        let effectiveText = trimmedNewText ?? original.textContent
        let effectiveAttachments = attachments ?? draftAttachments(from: original)

        // Drop the existing user turn (and everything after it) locally.
        messages.removeSubrange(originalIndex..<messages.count)

        // Re-append the (possibly edited) user message.
        var blocks: [ContentBlock] = []
        if !effectiveText.isEmpty {
            blocks.append(.text(id: UUID().uuidString, effectiveText))
        }
        for attachment in effectiveAttachments {
            blocks.append(.image(id: UUID().uuidString, data: attachment.data, mimeType: "image/jpeg"))
        }
        messages.append(ChatMessage(role: .user, blocks: blocks))

        // Start streaming placeholder.
        isStreaming = true
        let assistantId = UUID().uuidString
        streamingMessageId = assistantId
        messages.append(ChatMessage(id: assistantId, role: .assistant, blocks: []))

        do {
            try await client?.resubmit(text: text, attachments: attachments)
        } catch {
            isStreaming = false
            streamingMessageId = nil
            self.error = "Failed to resubmit: \(error)"
        }
    }

    func cancelStreaming() async {
        await client?.cancelStreaming()
    }

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

    func finalizeStreamingMessage(usage: MessageUsage? = nil) {
        if let usage, let id = streamingMessageId, let index = lastMessageIndex(id: id) {
            messages[index].usage = usage
        }
    }

    // MARK: - Private helpers

    private func lastMessageIndex(id: String) -> Int? {
        messages.lastIndex(where: { $0.id == id })
    }
}
