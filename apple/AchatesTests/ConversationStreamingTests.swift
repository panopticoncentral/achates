import XCTest
@testable import Achates

@MainActor
final class ConversationStreamingTests: XCTestCase {
    private func agent(_ id: String = "maya") -> Agent {
        Agent(id: id, name: id, displayName: id, description: "", tools: [],
              lastMessage: nil, lastActivity: nil, unreadCount: 0, avatarData: nil)
    }

    private func state() -> AppState {
        let state = AppState()
        state.serverURL = URL(string: "https://streaming-test.invalid")
        state.currentAgent = agent()
        state.currentSessionId = "one"
        state.connectionStatus = .connected
        return state
    }

    private func beginReply(_ state: AppState, id: String) {
        state.isStreaming = true
        state.streamingMessageId = id
        state.messages = [ChatMessage(id: id, role: .assistant, blocks: [])]
    }

    private func event(_ name: String, session: String = "one", agent: String = "maya",
                       extra: [String: JSONValue] = [:]) -> EventFrame {
        var payload = extra
        payload["agent"] = .string(agent)
        payload["session_id"] = .string(session)
        return EventFrame(type: .evt, event: name, payload: payload, seq: nil)
    }

    func testBackgroundCompletionClearsStopButtonWhenReturning() {
        let state = state()
        let client = WebSocketClient(appState: state)
        beginReply(state, id: "reply-one")

        state.currentSessionId = "two"
        XCTAssertFalse(state.isStreaming)
        XCTAssertNil(state.streamingMessageId)
        XCTAssertTrue(state.canSubmitMessage)
        let visibleMessage = ChatMessage(role: .user, text: "Other conversation")
        state.messages = [visibleMessage]

        client.handleEvent(event("text.delta", extra: ["delta": .string("Finished reply")]))
        client.handleEvent(event("done"))
        XCTAssertEqual(state.messages.map(\.id), [visibleMessage.id])
        XCTAssertFalse(state.isStreaming)

        state.currentSessionId = "one"
        XCTAssertFalse(state.isStreaming)
        XCTAssertNil(state.streamingMessageId)
        XCTAssertTrue(state.canSubmitMessage)
        XCTAssertEqual(state.messages.first?.textContent, "Finished reply")
    }

    func testBackgroundDoneDoesNotStopAnotherConversationReply() {
        let state = state()
        let client = WebSocketClient(appState: state)
        beginReply(state, id: "reply-one")
        state.currentSessionId = "two"
        beginReply(state, id: "reply-two")

        client.handleEvent(event("done"))
        XCTAssertTrue(state.isStreaming)
        XCTAssertEqual(state.streamingMessageId, "reply-two")
        XCTAssertFalse(state.canSubmitMessage)
        client.handleEvent(event("text.delta", session: "two", extra: ["delta": .string("Second reply")]))
        client.handleEvent(event("done", session: "two"))
        XCTAssertFalse(state.isStreaming)
        XCTAssertEqual(state.messages.first?.textContent, "Second reply")
    }

    func testReturningDuringReplyPreservesTranscriptAndContinuesStreaming() async {
        let state = state()
        let client = WebSocketClient(appState: state)
        beginReply(state, id: "reply-one")
        client.handleEvent(event("text.delta", extra: ["delta": .string("Before ")]))
        state.currentSessionId = "two"
        client.handleEvent(event("text.delta", extra: ["delta": .string("during ")]))

        await state.openSession("one", for: agent())
        XCTAssertTrue(state.isStreaming)
        XCTAssertFalse(state.isLoadingHistory)
        XCTAssertNil(state.historyLoadError)
        XCTAssertEqual(state.streamingMessageId, "reply-one")
        XCTAssertEqual(state.messages.first?.textContent, "Before during ")
        client.handleEvent(event("text.delta", extra: ["delta": .string("after")]))
        client.handleEvent(event("message.end", extra: ["usage": .object([
            "input": .int(10), "output": .int(3), "cost": .double(0.01),
        ])]))
        // message.end may precede further work; only done ends the reply.
        XCTAssertTrue(state.isStreaming)
        client.handleEvent(event("done"))
        XCTAssertFalse(state.isStreaming)
        XCTAssertEqual(state.messages.first?.textContent, "Before during after")
        XCTAssertEqual(state.messages.first?.usage?.outputTokens, 3)
    }

    func testSwitchingAgentsKeepsSameSessionIDsIndependent() async {
        let state = state()
        let client = WebSocketClient(appState: state)
        beginReply(state, id: "maya-reply")
        await state.selectAgent(agent("atlas"))
        XCTAssertFalse(state.isStreaming)
        state.currentSessionId = "one"
        beginReply(state, id: "atlas-reply")
        client.handleEvent(event("done"))
        XCTAssertTrue(state.isStreaming)
        XCTAssertEqual(state.streamingMessageId, "atlas-reply")
        state.currentAgent = agent()
        XCTAssertFalse(state.isStreaming)
    }

    func testUnrelatedAndUnscopedEventsDoNotChangeReply() {
        let state = state()
        let client = WebSocketClient(appState: state)
        beginReply(state, id: "reply-one")
        client.handleEvent(event("done", session: "cron-session"))
        client.handleEvent(EventFrame(type: .evt, event: "done", payload: nil, seq: nil))
        client.handleEvent(event("text.delta", agent: "other", extra: ["delta": .string("Wrong reply")]))
        XCTAssertTrue(state.isStreaming)
        XCTAssertEqual(state.streamingMessageId, "reply-one")
        XCTAssertEqual(state.messages.first?.textContent, "")
    }

    func testServerIsolationAndDisconnectClearBackgroundReplyState() {
        let state = state()
        beginReply(state, id: "reply-one")
        let originalServer = state.serverURL
        state.serverURL = URL(string: "https://other-server.invalid")
        XCTAssertFalse(state.isStreaming)
        state.serverURL = originalServer
        XCTAssertTrue(state.isStreaming)
        state.currentSessionId = "two"
        state.disconnect()
        state.currentAgent = agent()
        state.currentSessionId = "one"
        XCTAssertFalse(state.isStreaming)
        XCTAssertNil(state.streamingMessageId)
        XCTAssertTrue(state.messages.isEmpty)
    }
}
