import Foundation

enum ConnectionStatus: String, Sendable {
    case disconnected
    case connecting
    case connected
    case reconnecting
}

@MainActor
final class WebSocketClient {
    private let appState: AppState
    private let router: DeviceCommandRouter

    private let pendingRequests = PendingRequestStore()
    private let webSocketHolder = WebSocketTaskHolder()
    private let lastSeqHolder = AtomicInt()
    private let reconnectAttemptsHolder = AtomicInt()
    private let shouldReconnectHolder = AtomicBool(true)

    init(appState: AppState) {
        self.appState = appState
        self.router = DeviceCommandRouter()
    }

    func connect(url: URL, agent: String) {
        shouldReconnectHolder.set(true)
        reconnectAttemptsHolder.set(0)
        appState.connectionStatus = .connecting

        // Build WebSocket URL: convert http(s) to ws(s) and append /ws
        let wsURL = url.appendingPathComponent("ws")
        var components = URLComponents(url: wsURL, resolvingAgainstBaseURL: false)!
        if components.scheme == "http" { components.scheme = "ws" }
        else if components.scheme == "https" { components.scheme = "wss" }
        var queryItems = components.queryItems ?? []
        let lastSeq = lastSeqHolder.get()
        if lastSeq > 0 {
            queryItems.append(URLQueryItem(name: "last_seq", value: String(lastSeq)))
        }
        components.queryItems = queryItems

        let session = URLSession(configuration: .default)
        let task = session.webSocketTask(with: components.url!)
        webSocketHolder.set(task)
        task.resume()

        Task { [weak self] in
            await self?.receiveLoop(task: task)
        }

        Task { [weak self] in
            await self?.performConnect()
        }
    }

    func disconnect() {
        shouldReconnectHolder.set(false)
        webSocketHolder.get()?.cancel(with: .normalClosure, reason: nil)
        webSocketHolder.set(nil)
        appState.connectionStatus = .disconnected
    }

    func sendRequest(method: String, params: [String: JSONValue]? = nil, timeout: Duration = .seconds(30)) async throws -> [String: JSONValue]? {
        guard let task = webSocketHolder.get() else {
            throw FrameError.notConnected
        }

        let frame = RequestFrame(method: method, params: params)
        let data = try JSONEncoder().encode(frame)
        let frameId = frame.id

        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<[String: JSONValue]?, Error>) in
            self.pendingRequests.set(frameId, continuation: continuation)

            Task { [weak self] in
                guard let self else { return }
                do {
                    try await task.send(.data(data))
                } catch {
                    if let cont = self.pendingRequests.remove(frameId) {
                        cont.resume(throwing: error)
                    }
                }
            }

            Task { [weak self] in
                guard let self else { return }
                try? await Task.sleep(for: timeout)
                if let cont = self.pendingRequests.remove(frameId) {
                    cont.resume(throwing: FrameError.timeout)
                }
            }
        }
    }

    func sendMessage(_ text: String) async {
        guard let agent = appState.currentAgent else {
            print("Cannot send message: no agent selected")
            return
        }
        do {
            let payload = try await sendRequest(method: "chat.send", params: [
                "text": .string(text),
                "agent": .string(agent.id),
            ])
            if let sessionId = payload?["session_id"]?.stringValue {
                let isNew = payload?["new_session"]?.boolValue ?? false
                appState.handleChatSendResponse(sessionId: sessionId, isNewSession: isNew)
            }
        } catch {
            print("Failed to send message: \(error)")
        }
    }

    func cancelStreaming() async {
        guard let agent = appState.currentAgent else { return }
        do {
            _ = try await sendRequest(method: "chat.cancel", params: [
                "agent": .string(agent.id),
            ])
        } catch {
            print("Failed to cancel: \(error)")
        }
    }

    // MARK: - Private

    private func performConnect() async {
        do {
            // Step 1: Handshake
            #if os(macOS)
            let clientId = "macos"
            let capabilities: [JSONValue] = [.string("location")]
            #else
            let clientId = "ios"
            let capabilities: [JSONValue] = [.string("location"), .string("camera")]
            #endif
            _ = try await sendRequest(method: "connect", params: [
                "client": .string(clientId),
                "version": .string("1.0"),
                "capabilities": .array(capabilities)
            ])
            reconnectAttemptsHolder.set(0)
            appState.connectionStatus = .connected

            // Step 2: Fetch agent list
            let agentsPayload = try await sendRequest(method: "agents.list")
            if let agentsList = agentsPayload?["agents"]?.arrayValue {
                appState.agents = agentsList.compactMap { val -> Agent? in
                    guard let dict = val.objectValue else { return nil }
                    return Agent.from(dict)
                }
                appState.updateAppBadge()
            }
        } catch {
            print("Connect handshake failed: \(error)")
            appState.connectionStatus = .disconnected
        }
    }

    private func receiveLoop(task: URLSessionWebSocketTask) async {
        while shouldReconnectHolder.get() {
            do {
                let message = try await task.receive()
                let data: Data
                switch message {
                case .data(let d):
                    data = d
                case .string(let s):
                    data = Data(s.utf8)
                @unknown default:
                    continue
                }

                let frame = try Frame.parse(data)
                await handleFrame(frame)

            } catch {
                if shouldReconnectHolder.get() {
                    await handleDisconnect()
                }
                return
            }
        }
    }

    private func handleFrame(_ frame: Frame) async {
        switch frame {
        case .response(let res):
            if let cont = pendingRequests.remove(res.id) {
                if res.ok {
                    cont.resume(returning: res.payload)
                } else {
                    let msg = res.error?.message ?? "Unknown error"
                    cont.resume(throwing: FrameError.serverError(msg))
                }
            }

        case .event(let evt):
            if let seq = evt.seq {
                lastSeqHolder.set(seq)
            }
            handleEvent(evt)

        case .request(let req):
            await handleServerRequest(req)
        }
    }

    private func handleEvent(_ evt: EventFrame) {
        let payload = evt.payload ?? [:]

        switch evt.event {
        case "text.delta":
            let delta = payload["delta"]?.stringValue ?? ""
            appState.appendTextDelta(delta)

        case "text.end":
            break

        case "thinking.delta":
            let delta = payload["delta"]?.stringValue ?? ""
            let thinkingId = payload["id"]?.stringValue ?? "default"
            appState.appendThinkingDelta(delta, thinkingId: thinkingId)

        case "thinking.end":
            let thinkingId = payload["id"]?.stringValue ?? "default"
            appState.collapseThinking(thinkingId: thinkingId)

        case "tool.start":
            let toolId = payload["id"]?.stringValue ?? UUID().uuidString
            let name = payload["name"]?.stringValue ?? "unknown"
            appState.addToolCall(toolId: toolId, name: name)

        case "tool.end":
            let toolId = payload["id"]?.stringValue ?? ""
            let result = payload["result"]?.stringValue
            let success = payload["success"]?.boolValue ?? true
            appState.completeToolCall(toolId: toolId, result: result, success: success)

        case "message.end":
            appState.finalizeStreamingMessage()

        case "done":
            appState.isStreaming = false
            appState.streamingMessageId = nil
            appState.markCurrentAgentAsRead()

        case "cron.result":
            let agent = payload["agent"]?.stringValue
            if agent == nil || agent == appState.currentAgent?.id {
                Task { await appState.loadTimeline() }
            }

        default:
            print("Unknown event: \(evt.event)")
        }
    }

    private func handleServerRequest(_ req: RequestFrame) async {
        let response = await router.handle(method: req.method, params: req.params ?? [:])

        let resFrame: ResponseFrame
        switch response {
        case .success(let payload):
            resFrame = ResponseFrame(id: req.id, ok: true, payload: payload)
        case .failure(let error):
            resFrame = ResponseFrame(id: req.id, ok: false, error: ResponseError(code: "error", message: error.localizedDescription))
        }

        if let task = webSocketHolder.get() {
            do {
                let data = try JSONEncoder().encode(resFrame)
                try await task.send(.data(data))
            } catch {
                print("Failed to send response: \(error)")
            }
        }
    }

    private func handleDisconnect() async {
        webSocketHolder.set(nil)
        appState.connectionStatus = .reconnecting

        let attempts = reconnectAttemptsHolder.increment()
        let delay = min(0.5 * pow(2.0, Double(attempts - 1)), 30.0)

        try? await Task.sleep(for: .milliseconds(Int(delay * 1000)))

        guard shouldReconnectHolder.get(), let url = appState.serverURL, let agent = appState.currentAgent else {
            appState.connectionStatus = .disconnected
            return
        }

        connect(url: url, agent: agent.id)
    }

}

// MARK: - Thread-safe helpers

private final class PendingRequestStore: Sendable {
    private let lock = NSLock()
    private nonisolated(unsafe) var store: [String: CheckedContinuation<[String: JSONValue]?, Error>] = [:]

    func set(_ id: String, continuation: CheckedContinuation<[String: JSONValue]?, Error>) {
        lock.lock()
        store[id] = continuation
        lock.unlock()
    }

    func remove(_ id: String) -> CheckedContinuation<[String: JSONValue]?, Error>? {
        lock.lock()
        let cont = store.removeValue(forKey: id)
        lock.unlock()
        return cont
    }
}

private final class WebSocketTaskHolder: Sendable {
    private let lock = NSLock()
    private nonisolated(unsafe) var task: URLSessionWebSocketTask?

    func set(_ newTask: URLSessionWebSocketTask?) {
        lock.lock()
        task = newTask
        lock.unlock()
    }

    func get() -> URLSessionWebSocketTask? {
        lock.lock()
        let t = task
        lock.unlock()
        return t
    }
}

private final class AtomicInt: Sendable {
    private let lock = NSLock()
    private nonisolated(unsafe) var value: Int = 0

    func get() -> Int {
        lock.lock()
        let v = value
        lock.unlock()
        return v
    }

    func set(_ newValue: Int) {
        lock.lock()
        value = newValue
        lock.unlock()
    }

    @discardableResult
    func increment() -> Int {
        lock.lock()
        value += 1
        let v = value
        lock.unlock()
        return v
    }
}

private final class AtomicBool: Sendable {
    private let lock = NSLock()
    private nonisolated(unsafe) var value: Bool

    init(_ initial: Bool) {
        self.value = initial
    }

    func get() -> Bool {
        lock.lock()
        let v = value
        lock.unlock()
        return v
    }

    func set(_ newValue: Bool) {
        lock.lock()
        value = newValue
        lock.unlock()
    }
}
