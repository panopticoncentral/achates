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
    var usesSplitNavigation = false
    var isStreaming: Bool {
        get { currentConversation.isStreaming }
        set { currentConversation.isStreaming = newValue }
    }
    var streamingMessageId: String? {
        get { currentConversation.streamingMessageId }
        set { currentConversation.streamingMessageId = newValue }
    }
    var client: WebSocketClient?
    var error: String?

    /// Why the last connect attempt failed, for the settings/onboarding UI.
    /// Cleared on a successful handshake.
    var lastConnectionError: String?

    /// Plays back streamed assistant-speech audio. One instance app-wide;
    /// turns are tagged so per-message replay can reconstruct a finished turn.
    let speechPlayer = SpeechPlayer()

    /// Sessions for the currently selected agent
    var sessions: [SessionInfo] = []
    var hasMoreSessions = false
    var isLoadingSessions = false
    var sessionsLoadError: String?
    var isLoadingHistory = false
    var historyLoadError: String?
    var memoryLoadError: String?
    var jobsLoadError: String?
    var costLoadErrors: [String: String] = [:]
    @ObservationIgnored private var historyRequestID = UUID()
    @ObservationIgnored private var sessionListRequestID = UUID()
    @ObservationIgnored private var wasBackgrounded = false
    @ObservationIgnored private var resyncRequestID = UUID()
    @ObservationIgnored private var drafts: [ConversationKey: ConversationDraft] = [:]

    @ObservationIgnored private var conversations: [ConversationKey: ChatSessionState] = [:]
    private let unselectedConversation = ChatSessionState()

    var currentConversation: ChatSessionState {
        guard let agentID = currentAgent?.id, let sessionID = currentSessionId else {
            return unselectedConversation
        }
        let key = ConversationKey(server: serverURL?.absoluteString ?? "", agentID: agentID, sessionID: sessionID)
        if let conversation = conversations[key] { return conversation }
        let conversation = ChatSessionState()
        conversations[key] = conversation
        return conversation
    }

    /// Route broadcasts only to conversations this client has opened.
    func conversation(agentID: String, sessionID: String) -> ChatSessionState? {
        let key = ConversationKey(server: serverURL?.absoluteString ?? "", agentID: agentID, sessionID: sessionID)
        return conversations[key]
    }

    private func discardInactiveConversations() {
        let visible = currentConversation
        // Retain background replies, but avoid caching every transcript and attachment
        // opened during a long app session. Completed chats reload from the server.
        conversations = conversations.filter {
            $0.value === visible || $0.value.isStreaming || $0.value.failedSend != nil
        }
    }

    func draft(for agentID: String, sessionID: String) -> ConversationDraft {
        let key = ConversationKey(server: serverURL?.absoluteString ?? "", agentID: agentID, sessionID: sessionID)
        if let draft = drafts[key] { return draft }
        let draft = ConversationDraft()
        drafts[key] = draft
        return draft
    }

    var canSubmitMessage: Bool {
        connectionStatus == .connected && currentAgent != nil && currentSessionId != nil
            && !isStreaming && !isLoadingHistory && historyLoadError == nil
    }

    /// Messages for the currently open session
    var messages: [ChatMessage] {
        get { currentConversation.messages }
        set { currentConversation.messages = newValue }
    }
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
        if serverURL != url {
            disconnect()
            client = nil
            memories = []
            jobs = []
            costSummaries = [:]
            memoryLoadError = nil
            jobsLoadError = nil
            costLoadErrors = [:]
        }
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
        historyRequestID = UUID()
        sessionListRequestID = UUID()
        isLoadingHistory = false
        isLoadingSessions = false
        connectionStatus = .disconnected
        agents = []
        currentAgent = nil
        sessions = []
        messages = []
        currentSessionId = nil
        conversations.removeAll()
        isStreaming = false
        streamingMessageId = nil
        failedSend = nil
    }

    func markCurrentSessionAsRead() {
        guard let agent = currentAgent, let sessionId = currentSessionId, client != nil else { return }

        // Optimistically clear this row's dot immediately.
        if let index = sessions.firstIndex(where: { $0.id == sessionId }), sessions[index].unread > 0 {
            sessions[index].unread = 0
        }

        Task {
            _ = try? await client?.sendRequest(method: "chat.read", params: [
                "agent": .string(agent.id),
                "session_id": .string(sessionId),
            ])
            // Reading one session may leave others unread — recompute the agent-level
            // dot/badge from the server rather than assuming zero.
            await refreshAgents()
            updateAppBadge()
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
            sessions = []
            hasMoreSessions = false
            historyRequestID = UUID()
            isLoadingHistory = false
            historyLoadError = nil
        }
        currentAgent = agent
        discardInactiveConversations()
        await loadSessions(for: agent)
    }

    func loadSessions(for agent: Agent) async {
        let requestID = UUID()
        sessionListRequestID = requestID
        isLoadingSessions = true
        sessionsLoadError = nil
        defer { if sessionListRequestID == requestID { isLoadingSessions = false } }
        do {
            guard let client else { throw FrameError.notConnected }
            let payload = try await client.sendRequest(method: "sessions.list", params: [
                "agent": .string(agent.id),
            ])
            guard sessionListRequestID == requestID, currentAgent?.id == agent.id else { return }
            guard let payload else { throw AgentEditError.invalidResponse }
            sessions = SessionInfo.fromList(payload)
            hasMoreSessions = payload["has_more"]?.boolValue ?? false
        } catch {
            if sessionListRequestID == requestID {
                sessionsLoadError = "Couldn't load conversations. \(error.localizedDescription)"
            }
        }
    }

    func loadMoreSessions(for agent: Agent) async {
        guard client != nil, hasMoreSessions, let oldest = sessions.last else { return }

        let beforeMs = Int(oldest.updated.timeIntervalSince1970 * 1000)
        let requestID = sessionListRequestID

        do {
            let payload = try await client?.sendRequest(method: "sessions.list", params: [
                "agent": .string(agent.id),
                "before": .int(beforeMs),
            ])
            guard requestID == sessionListRequestID, currentAgent?.id == agent.id else { return }
            if let payload {
                let older = SessionInfo.fromList(payload)
                hasMoreSessions = payload["has_more"]?.boolValue ?? false
                sessions.append(contentsOf: older)
            }
        } catch {
            if sessionListRequestID == requestID {
                sessionsLoadError = "Couldn't load conversations. \(error.localizedDescription)"
            }
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

    /// Create a session and open it through the normal open path — the single
    /// entry point for ⌘N, the toolbar button, and the empty-state button, so
    /// read-marking and speech-state sync aren't skipped by any of them.
    func startNewConversation(for agent: Agent) async {
        guard let sessionId = await createSession(for: agent) else { return }
        #if os(iOS)
        if usesSplitNavigation { await openSession(sessionId, for: agent) }
        else { navigationPath.append(SessionSelection(agent: agent, sessionId: sessionId)) }
        #else
        await openSession(sessionId, for: agent)
        #endif
    }

    func openSession(_ sessionId: String, for agent: Agent) async {
        let requestID = UUID()
        historyRequestID = requestID
        currentAgent = agent
        currentSessionId = sessionId
        discardInactiveConversations()
        // Keep the live transcript when returning before the reply finishes.
        if isStreaming {
            historyLoadError = nil
            isLoadingHistory = false
            markCurrentSessionAsRead()
            return
        }
        messages = []
        failedSend = nil
        historyLoadError = nil
        isLoadingHistory = true
        defer { if historyRequestID == requestID { isLoadingHistory = false } }
        do {
            guard let client else { throw FrameError.notConnected }
            let payload = try await client.sendRequest(method: "sessions.get", params: [
                "agent": .string(agent.id),
                "session_id": .string(sessionId),
            ])
            guard historyRequestID == requestID, currentSessionId == sessionId else { return }
            guard let payload else { throw AgentEditError.invalidResponse }
            messages = parseSessionMessages(payload, serverURL: serverURL)
            currentConversation.applyTurnOutcome(payload)
            markCurrentSessionAsRead()
        } catch {
            if historyRequestID == requestID {
                historyLoadError = "Couldn't load this conversation. \(error.localizedDescription)"
            }
        }
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

    /// Speech-enabled state for the currently open session, or false when no
    /// session is open. The nav-bar toggle reads this.
    var currentSpeechEnabled: Bool {
        guard let id = currentSessionId else { return false }
        return sessions.first(where: { $0.id == id })?.speechEnabled ?? false
    }

    /// Toggle speech for the currently open session via `session.set_speech`.
    /// Updates local state on success so the toolbar icon reflects the new
    /// value without waiting for the broadcast round-trip.
    func toggleSpeechForCurrentSession() async {
        guard let agent = currentAgent,
              let sessionId = currentSessionId,
              let client else { return }
        let newState = !currentSpeechEnabled
        do {
            _ = try await client.sendRequest(method: "session.set_speech", params: [
                "agent": .string(agent.id),
                "session_id": .string(sessionId),
                "enabled": .bool(newState),
            ])
            if let index = sessions.firstIndex(where: { $0.id == sessionId }) {
                sessions[index].speechEnabled = newState
            }
        } catch {
            self.error = "Failed to toggle speech: \(error.localizedDescription)"
        }
    }

    /// Set speech on/off for the currently open session via `session.set_speech`.
    /// Used by conversation mode to force speech on while a call is active and
    /// restore the prior value on exit.
    func setSpeechForCurrentSession(_ enabled: Bool) async {
        guard let agent = currentAgent,
              let sessionId = currentSessionId,
              let client else { return }
        do {
            _ = try await client.sendRequest(method: "session.set_speech", params: [
                "agent": .string(agent.id),
                "session_id": .string(sessionId),
                "enabled": .bool(enabled),
            ])
            if let index = sessions.firstIndex(where: { $0.id == sessionId }) {
                sessions[index].speechEnabled = enabled
            }
        } catch {
            self.error = "Failed to set speech: \(error.localizedDescription)"
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

    func loadDefaultModels() async throws -> (base: String?, thinking: String?) {
        guard let payload = try await client?.sendRequest(method: "config.get_models") else {
            throw AgentEditError.notConnected
        }
        return (payload["base"]?.stringValue, payload["thinking"]?.stringValue)
    }

    func saveDefaultModels(base: String?, thinking: String?) async throws {
        guard let client else { throw AgentEditError.notConnected }
        _ = try await client.sendRequest(method: "config.set_models", params: [
            "base": .string(base ?? ""),
            "thinking": .string(thinking ?? ""),
        ])
    }

    /// Cached cost summaries keyed by "agent:period". Filled by `loadCostSummary`.
    var costSummaries: [String: CostSummary] = [:]

    func costSummary(agent: String, period: String) -> CostSummary? {
        costSummaries["\(agent):\(period)"]
    }

    func loadCostSummary(agent: String, period: String) async {
        let key = "\(agent):\(period)"
        costLoadErrors[key] = nil
        do {
            guard let client else { throw FrameError.notConnected }
            guard let payload = try await client.sendRequest(method: "costs.summary", params: [
                "agent": .string(agent), "period": .string(period),
            ]), let summary = CostSummary.from(payload) else { throw AgentEditError.invalidResponse }
            costSummaries[key] = summary
        } catch {
            costLoadErrors[key] = "Couldn't load costs. \(error.localizedDescription)"
        }
    }

    /// Invalidate cached cost summaries so the next read refetches from the server.
    /// Called when a chat turn finishes (the cost ledger was just updated).
    func invalidateCostSummaries() {
        costSummaries.removeAll()
    }

    // MARK: - Memory

    @discardableResult
    func loadMemories() async -> Bool {
        memoryLoadError = nil
        do {
            guard let client else { throw FrameError.notConnected }
            guard let payload = try await client.sendRequest(method: "memory.list") else {
                throw AgentEditError.invalidResponse
            }
            memories = MemoryInfo.fromList(payload)
            return true
        } catch {
            memoryLoadError = "Couldn't load memories. \(error.localizedDescription)"
            return false
        }
    }

    func loadMemory(scope: String) async throws -> String {
        guard let client else { throw AgentEditError.notConnected }
        let payload = try await client.sendRequest(method: "memory.get", params: [
            "scope": .string(scope),
        ])
        return payload?["content"]?.stringValue ?? ""
    }

    // A nil client must throw here, not no-op: `try await client?.send...` returns
    // nil on disconnect and the caller's UI reads that as a successful save/delete.
    func saveMemory(scope: String, content: String) async throws {
        guard let client else { throw AgentEditError.notConnected }
        _ = try await client.sendRequest(method: "memory.set", params: [
            "scope": .string(scope),
            "content": .string(content),
        ])
    }

    // MARK: - Scheduled jobs

    @discardableResult
    func loadJobs() async -> Bool {
        jobsLoadError = nil
        do {
            guard let client else { throw FrameError.notConnected }
            guard let payload = try await client.sendRequest(method: "jobs.list") else {
                throw AgentEditError.invalidResponse
            }
            jobs = CronJobInfo.fromList(payload)
            return true
        } catch {
            jobsLoadError = "Couldn't load jobs. \(error.localizedDescription)"
            return false
        }
    }

    func setJobEnabled(agent: String, jobId: String, enabled: Bool) async throws {
        guard let client else { throw AgentEditError.notConnected }
        _ = try await client.sendRequest(method: "jobs.update", params: [
            "agent": .string(agent),
            "id": .string(jobId),
            "enabled": .bool(enabled),
        ])
    }

    func deleteJob(agent: String, jobId: String) async throws {
        guard let client else { throw AgentEditError.notConnected }
        _ = try await client.sendRequest(method: "jobs.delete", params: [
            "agent": .string(agent),
            "id": .string(jobId),
        ])
    }

    func runJob(agent: String, jobId: String, skipNext: Bool = false) async throws {
        guard let client else { throw AgentEditError.notConnected }
        _ = try await client.sendRequest(method: "jobs.run", params: [
            "agent": .string(agent),
            "id": .string(jobId),
            "skip_next": .bool(skipNext),
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
        if phase == .background {
            wasBackgrounded = true
            return
        }
        guard phase == .active, serverURL != nil else { return }
        let needsFreshSocket = wasBackgrounded
        wasBackgrounded = false
        // A suspended socket can still report connected. Replace only the transport;
        // AppState.disconnect() would discard the selected chat and live transcript.
        if needsFreshSocket {
            client?.disconnect()
            connectToServer()
        } else if connectionStatus != .connected {
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
        // History must not depend on the first page of the list, or on its success.
        // Fetch it first so a slow list request cannot delay foreground recovery.
        if let sessionId = currentSessionId {
            await reloadCurrentSessionMessages(agent: agent, sessionId: sessionId)
        }
        guard currentAgent?.id == agent.id else { return }
        await loadSessions(for: agent)
    }

    private func reloadCurrentSessionMessages(agent: Agent, sessionId: String) async {
        guard let client else { return }
        let conversation = currentConversation
        let turnRevision = conversation.turnRevision
        let wasStreaming = conversation.isStreaming
        let requestID = UUID()
        resyncRequestID = requestID
        do {
            let payload = try await client.sendRequest(method: "sessions.get", params: [
                "agent": .string(agent.id),
                "session_id": .string(sessionId),
            ])
            if let payload, resyncRequestID == requestID, self.client === client,
               currentAgent?.id == agent.id,
               currentSessionId == sessionId, currentConversation === conversation,
               conversation.turnRevision == turnRevision,
               payload["is_running"]?.boolValue == false || (!wasStreaming && !isStreaming) {
                messages = parseSessionMessages(payload, serverURL: serverURL)
                currentConversation.applyTurnOutcome(payload)
                historyLoadError = nil
            }
        } catch {
            // Best-effort resync; surface as transient error only if user is on the chat
        }
    }

    // MARK: - Send message

    /// A chat.send that never reached the server. Drives the retry banner in ChatView.
    struct FailedSend: Equatable {
        let messageId: String
        let text: String
        let attachments: [DraftAttachment]
    }

    var failedSend: FailedSend? {
        get { currentConversation.failedSend }
        set { currentConversation.failedSend = newValue }
    }

    func sendMessage(_ text: String, attachments: [DraftAttachment] = []) async {
        guard canSubmitMessage else { return }
        let conversation = currentConversation
        conversation.clearInterruption()
        failedSend = nil

        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        var blocks: [ContentBlock] = []
        if !trimmed.isEmpty {
            blocks.append(.text(id: UUID().uuidString, trimmed))
        }
        blocks.append(contentsOf: attachments.map(Self.echoBlock))

        let userMessage = ChatMessage(role: .user, blocks: blocks)
        messages.append(userMessage)
        isStreaming = true

        let assistantId = UUID().uuidString
        streamingMessageId = assistantId
        let assistantMessage = ChatMessage(id: assistantId, role: .assistant, blocks: [])
        messages.append(assistantMessage)

        do {
            guard let client else { throw FrameError.notConnected }
            try await client.sendMessage(trimmed, attachments: attachments)
        } catch {
            // The send never reached the server: drop the placeholder (or it renders
            // as a typing indicator forever), keep the user's bubble, offer retry.
            conversation.messages.removeAll { $0.id == assistantId }
            conversation.isStreaming = false
            conversation.streamingMessageId = nil
            conversation.failedSend = FailedSend(messageId: userMessage.id, text: trimmed, attachments: attachments)
        }
    }

    /// Re-send a failed chat.send: the failed bubble is dropped and the same
    /// text/attachments go through `sendMessage` again (which re-appends it).
    func retryFailedSend() async {
        guard canSubmitMessage, let failed = failedSend else { return }
        failedSend = nil
        messages.removeAll { $0.id == failed.messageId }
        await sendMessage(failed.text, attachments: failed.attachments)
    }

    /// Continue from completed work without rewinding or resending attachments.
    func continueResponse() async {
        guard canSubmitMessage, currentConversation.canContinue,
              let client, let agent = currentAgent, let sessionId = currentSessionId else { return }
        let conversation = currentConversation
        let previousNotice = conversation.interruption
        conversation.clearInterruption()
        conversation.isStreaming = true
        let id = UUID().uuidString
        conversation.streamingMessageId = id
        conversation.messages.append(ChatMessage(id: id, role: .assistant, blocks: []))
        do {
            _ = try await client.sendRequest(method: "chat.continue", params: [
                "agent": .string(agent.id), "session_id": .string(sessionId),
            ])
        } catch {
            conversation.isStreaming = false
            conversation.streamingMessageId = nil
            conversation.messages.removeAll { $0.id == id }
            conversation.canContinue = true
            conversation.interruption = "Couldn't continue: \(error.localizedDescription) \(previousNotice ?? "")"
        }
    }

    /// The most recent user message in the open session, if any.
    var lastUserMessage: ChatMessage? {
        messages.last(where: { $0.role == .user })
    }

    /// The transcript echo for an outgoing attachment. PDFs/text files must NOT
    /// be echoed as images — image decoding fails on their bytes and the sent
    /// document silently vanishes from the transcript.
    private static func echoBlock(for attachment: DraftAttachment) -> ContentBlock {
        if attachment.isImage {
            return .image(id: UUID().uuidString, data: attachment.data, mimeType: attachment.mime)
        }
        return .document(
            id: UUID().uuidString,
            data: attachment.data,
            name: attachment.displayName,
            mime: attachment.mime)
    }

    /// Reconstruct DraftAttachments from a stored user message's attachment blocks.
    /// Remote-only images (no local bytes) are dropped — the user can re-attach.
    func draftAttachments(from message: ChatMessage) -> [DraftAttachment] {
        message.blocks.compactMap { block in
            switch block {
            case .image(_, let data, let mimeType):
                return DraftAttachment(data: data, mime: mimeType)
            case .document(_, let data, let name, let mime):
                return DraftAttachment(data: data, mime: mime, displayName: name)
            default:
                return nil
            }
        }
    }

    /// Rewind the latest turn — drop the last assistant response and any tool blocks,
    /// optionally replace the user prompt's text/attachments, then stream a fresh response.
    /// Pass `text: nil` / `attachments: nil` to preserve the original values.
    func resubmitLast(text: String?, attachments: [DraftAttachment]?) async {
        guard canSubmitMessage, client != nil else { return }
        guard let originalIndex = messages.lastIndex(where: { $0.role == .user }) else { return }
        await performResubmit(at: originalIndex, promptIndex: nil, text: text, attachments: attachments)
    }

    /// Rewind to an earlier user prompt identified by its message id — drop it and
    /// everything after it, then re-stream a fresh response. The original prompt's
    /// text/attachments are preserved unchanged.
    func resubmit(promptIndex: Int, messageId: String) async {
        guard canSubmitMessage, client != nil else { return }
        guard let originalIndex = messages.firstIndex(where: { $0.id == messageId }),
              messages[originalIndex].role == .user else { return }
        await performResubmit(at: originalIndex, promptIndex: promptIndex, text: nil, attachments: nil)
    }

    /// Shared tail for resubmit: optimistically truncate the local message list at
    /// `index`, re-append the (possibly edited) user message, add a streaming
    /// placeholder, and ask the server to rewind. `promptIndex` is the 0-based
    /// user-turn ordinal sent to the server (nil = latest turn).
    private func performResubmit(at index: Int, promptIndex: Int?, text: String?, attachments: [DraftAttachment]?) async {
        let conversation = currentConversation
        conversation.clearInterruption()
        let original = messages[index]
        let trimmedNewText = text?.trimmingCharacters(in: .whitespacesAndNewlines)
        let effectiveText = trimmedNewText ?? original.textContent
        let effectiveAttachments = attachments ?? draftAttachments(from: original)

        // Drop the existing user turn (and everything after it) locally.
        messages.removeSubrange(index..<messages.count)

        // Re-append the (possibly edited) user message.
        var blocks: [ContentBlock] = []
        if !effectiveText.isEmpty {
            blocks.append(.text(id: UUID().uuidString, effectiveText))
        }
        blocks.append(contentsOf: effectiveAttachments.map(Self.echoBlock))
        messages.append(ChatMessage(role: .user, blocks: blocks))

        // Start streaming placeholder.
        isStreaming = true
        let assistantId = UUID().uuidString
        streamingMessageId = assistantId
        messages.append(ChatMessage(id: assistantId, role: .assistant, blocks: []))

        do {
            try await client?.resubmit(promptIndex: promptIndex, text: text, attachments: attachments)
        } catch {
            conversation.isStreaming = false
            conversation.streamingMessageId = nil
            // The local rewind already happened but the server refused; drop the
            // placeholder and resync so the transcript reflects server truth.
            conversation.messages.removeAll { $0.id == assistantId }
            guard currentConversation === conversation else { return }
            self.error = "Failed to resubmit: \(error.localizedDescription)"
            if let agent = currentAgent, let sessionId = currentSessionId {
                await reloadCurrentSessionMessages(agent: agent, sessionId: sessionId)
            }
        }
    }

    func cancelStreaming() async {
        await client?.cancelStreaming()
    }
}
