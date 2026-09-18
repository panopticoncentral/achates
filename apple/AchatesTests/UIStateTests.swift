import XCTest
@testable import Achates

@MainActor
final class UIStateTests: XCTestCase {
    func testDraftsAreIsolatedByConversationAndServer() {
        let state = AppState()
        state.serverURL = URL(string: "https://one.example.com")
        let first = state.draft(for: "maya", sessionID: "one")
        first.text = "Unfinished thought"
        let other = state.draft(for: "maya", sessionID: "two")
        XCTAssertTrue(other.text.isEmpty)
        XCTAssertTrue(first === state.draft(for: "maya", sessionID: "one"))
        state.serverURL = URL(string: "https://two.example.com")
        XCTAssertTrue(state.draft(for: "maya", sessionID: "one").text.isEmpty)
        XCTAssertEqual(first.text, "Unfinished thought")
    }

    func testCancelAndSubmitEditRestorePreviousDraftAndAttachments() {
        let draft = ConversationDraft()
        let attachment = DraftAttachment(data: Data([1, 2]), mime: "text/plain", displayName: "note.txt")
        draft.text = "Next question"
        draft.attachments = [attachment]
        draft.beginEditing(.init(text: "Earlier question", attachments: []))
        draft.text = "Edited earlier question"
        draft.endEditing()
        XCTAssertEqual(draft.text, "Next question")
        XCTAssertEqual(draft.attachments, [attachment])
        XCTAssertNil(draft.pendingEdit)
        draft.beginEditing(.init(text: "Earlier question", attachments: []))
        draft.didSubmit()
        XCTAssertEqual(draft.text, "Next question")
        XCTAssertEqual(draft.attachments, [attachment])
        draft.didSubmit()
        XCTAssertTrue(draft.text.isEmpty)
        XCTAssertTrue(draft.attachments.isEmpty)
    }

    func testStreamingAndOfflineSubmissionDoNotMutateMessages() async {
        let state = AppState()
        state.currentAgent = Agent(id: "maya", name: "maya", displayName: "Maya", description: "", tools: [], lastMessage: nil, lastActivity: nil, unreadCount: 0, avatarData: nil)
        state.currentSessionId = "one"
        state.connectionStatus = .connected
        state.isStreaming = true
        await state.sendMessage("Should remain a draft")
        XCTAssertTrue(state.messages.isEmpty)
        XCTAssertTrue(state.isStreaming)
        state.isStreaming = false
        state.connectionStatus = .disconnected
        await state.sendMessage("Offline draft")
        XCTAssertTrue(state.messages.isEmpty)
        XCTAssertFalse(state.isStreaming)
        state.connectionStatus = .connected
        state.isLoadingHistory = true
        XCTAssertFalse(state.canSubmitMessage)
        state.isLoadingHistory = false
        state.historyLoadError = "Load failed"
        XCTAssertFalse(state.canSubmitMessage)
    }

    func testFailedLoadsRemainDistinctFromEmptyResults() async {
        let state = AppState()
        let memoryLoaded = await state.loadMemories()
        let jobsLoaded = await state.loadJobs()
        await state.loadCostSummary(agent: "maya", period: "week")
        XCTAssertFalse(memoryLoaded)
        XCTAssertFalse(jobsLoaded)
        XCTAssertNotNil(state.memoryLoadError)
        XCTAssertNotNil(state.jobsLoadError)
        XCTAssertNotNil(state.costLoadErrors["maya:week"])
        XCTAssertNil(state.costSummary(agent: "maya", period: "week"))
    }

    func testOfflineRetryPreservesFailedMessage() async {
        let state = AppState()
        let message = ChatMessage(role: .user, blocks: [.text(id: "text", "Keep this message")])
        state.messages = [message]
        let failed = AppState.FailedSend(messageId: message.id, text: "Keep this message", attachments: [])
        state.failedSend = failed
        state.connectionStatus = .disconnected
        await state.retryFailedSend()
        XCTAssertEqual(state.failedSend, failed)
        XCTAssertEqual(state.messages.map(\.id), [message.id])
    }

    func testServerAddressNormalizationAndValidation() {
        XCTAssertEqual(ServerAddress.parse(" 192.168.1.100:5000 ")?.absoluteString, "http://192.168.1.100:5000")
        XCTAssertEqual(ServerAddress.parse("https://example.com")?.scheme, "https")
        XCTAssertNil(ServerAddress.parse("file:///tmp/example"))
        XCTAssertNil(ServerAddress.parse("not an address"))
        XCTAssertNil(ServerAddress.parse("https://"))
    }

    func testManualPauseSurvivesSystemResumeAndTurnCompletion() {
        var machine = ConversationMachine()
        _ = machine.handle(.begin)
        _ = machine.handle(.speechCaptured("hello"))
        XCTAssertEqual(machine.handle(.pauseRequested), [.stopListening])
        XCTAssertEqual(machine.handle(.resumed), [])
        XCTAssertEqual(machine.handle(.turnCompleted(isPlaying: false)), [])
        XCTAssertEqual(machine.state, .paused)
        XCTAssertEqual(machine.handle(.resumeRequested), [.startListening])
        XCTAssertEqual(machine.state, .listening)
    }

    func testFailedVoiceCanRetry() {
        var machine = ConversationMachine()
        _ = machine.handle(.begin)
        _ = machine.handle(.startFailed("Permission denied"))
        XCTAssertEqual(machine.handle(.retryRequested), [.startListening])
        XCTAssertEqual(machine.state, .listening)
    }

    func testNonIntegralHourScheduleKeepsMinutes() {
        let label = CronJobInfo.Schedule.every(minutes: 90).displayString
        let formatter = DateComponentsFormatter()
        formatter.allowedUnits = [.day, .hour, .minute, .second]
        formatter.unitsStyle = .full
        XCTAssertEqual(label, "Every " + formatter.string(from: 5400)!)
        XCTAssertNotEqual(label, CronJobInfo.Schedule.every(minutes: 60).displayString)
    }
}
