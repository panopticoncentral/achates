import XCTest
@testable import Achates

final class MessagePresentationTests: XCTestCase {
    func testWorkbookHistoryRestoresOriginalBytesAndExcelPresentation() {
        let bytes = Data([0x50, 0x4b, 0x03, 0x04])
        let message = parseMessage(.object([
            "role": .string("user"), "text": .string("Review"),
            "content": .array([.object([
                "type": .string("workbook"), "data": .string(bytes.base64EncodedString()),
                "file_name": .string("sample.xlsx"), "mime_type": .string(DraftAttachment.workbookMime),
                "preview": .string("Model-only workbook preview")
            ])])
        ]), serverURL: nil)
        XCTAssertEqual(message?.blocks.count, 2)
        guard case .document(_, let data, let name, let mime) = message?.blocks.last else {
            return XCTFail("Workbook must remain a document attachment after history reload")
        }
        XCTAssertEqual(data, bytes)
        XCTAssertEqual(name, "sample.xlsx")
        XCTAssertEqual(mime, DraftAttachment.workbookMime)
        XCTAssertEqual(DraftAttachment.documentLabel(for: mime), "Excel workbook")
        XCTAssertTrue(DraftAttachment.documentTypes.contains(DraftAttachment.workbookType))
    }

    func testAlternatingThinkingAndToolsCollapseWithoutLosingDetails() {
        let blocks: [ContentBlock] = [
            .thinking(id: "thought-1", text: "First thought", collapsed: true),
            .toolCall(id: "tool-1", name: "notebook", status: .completed, result: "First result"),
            .thinking(id: "thought-2", text: "Second thought", collapsed: true),
            .toolCall(id: "tool-2", name: "memory", status: .failed, result: "Unavailable"),
            .text(id: "answer", "Here is the answer.")
        ]
        let groups = MessageBlockGroup.make(from: blocks)
        XCTAssertEqual(groups.count, 2)
        XCTAssertEqual(groups[0].summary, "2 tool calls · 1 failed")
        XCTAssertNil(groups[0].runningBlock)
        XCTAssertEqual(groups.flatMap(\.blocks), blocks)
    }

    func testStreamingRunKeepsIdentityAndUpdatesCurrentActivity() {
        var message = ChatMessage(role: .assistant)
        message.appendThinking("Considering", thinkingId: "thought")
        let firstID = MessageBlockGroup.make(from: message.blocks)[0].id
        message.collapseThinking("thought")
        message.addToolCall(toolId: "tool", name: "notebook")
        var group = MessageBlockGroup.make(from: message.blocks)[0]
        XCTAssertEqual(group.id, firstID)
        XCTAssertEqual(group.runningBlock, message.blocks.last)
        message.completeToolCall(toolId: "tool", result: "Found it", success: true)
        message.appendThinking("Next step", thinkingId: "next-thought")
        group = MessageBlockGroup.make(from: message.blocks)[0]
        XCTAssertEqual(group.id, firstID)
        XCTAssertEqual(group.runningBlock, message.blocks.last)
        message.collapseThinking("next-thought")
        XCTAssertNil(MessageBlockGroup.make(from: message.blocks)[0].runningBlock)
    }

    func testVisibleContentAlwaysBreaksActivityRuns() {
        let separators: [ContentBlock] = [
            .text(id: "text", "An update"),
            .image(id: "image", data: Data(), mimeType: "image/png"),
            .document(id: "doc", data: Data(), name: "Notes", mime: "text/plain"),
            .remoteImage(id: "remote", url: URL(string: "https://example.com/image.png")!),
            .agentTurn(id: "agent", agentName: "Maya", text: "An answer", collapsed: true)
        ]
        for separator in separators {
            let blocks: [ContentBlock] = [
                .toolCall(id: "before", name: "notebook", status: .completed, result: nil),
                separator,
                .toolCall(id: "after", name: "memory", status: .running, result: nil)
            ]
            let groups = MessageBlockGroup.make(from: blocks)
            XCTAssertEqual(groups.count, 3)
            XCTAssertEqual(groups.flatMap(\.blocks), blocks)
        }
        XCTAssertTrue(MessageBlockGroup.make(from: []).isEmpty)
    }

    func testHistoryIterationsJoinLikeLiveContentAndRetainUsageAndSourceIDs() {
        let date = Date()
        let first = ChatMessage(id: "first", role: .assistant, blocks: [
            .thinking(id: "thought", text: "Checking", collapsed: true),
            .toolCall(id: "tool-1", name: "notebook", status: .completed, result: nil)
        ], timestamp: date, usage: MessageUsage(inputTokens: 10, outputTokens: 20, cost: 0.1))
        let second = ChatMessage(id: "second", role: .assistant, blocks: [
            .toolCall(id: "tool-2", name: "memory", status: .completed, result: "Found it"),
            .text(id: "answer", "The answer")
        ], timestamp: date, usage: MessageUsage(inputTokens: 30, outputTokens: 40, cost: 0.2))
        let presented = PresentedMessage.make(from: [first, second])
        XCTAssertEqual(presented.count, 1)
        XCTAssertEqual(presented[0].message.id, first.id)
        XCTAssertEqual(presented[0].sourceIDs, [first.id, second.id])
        XCTAssertEqual(presented[0].message.blocks, first.blocks + second.blocks)
        XCTAssertEqual(presented[0].message.usage?.inputTokens, 40)
        XCTAssertEqual(presented[0].message.usage?.outputTokens, 60)
        XCTAssertEqual(presented[0].message.usage!.cost, 0.3, accuracy: 0.000001)
        XCTAssertEqual(MessageBlockGroup.make(from: presented[0].message.blocks).count, 2)
    }

    func testUserMessagesTimeGapsAndAudioRemainSeparate() {
        let date = Date()
        let first = ChatMessage(role: .assistant, blocks: [
            .toolCall(id: "tool", name: "notebook", status: .completed, result: nil)
        ], timestamp: date)
        let second = ChatMessage(role: .assistant, blocks: [
            .thinking(id: "thought", text: "Checking", collapsed: true)
        ], timestamp: date)
        XCTAssertEqual(PresentedMessage.make(from: [first, ChatMessage(role: .user, text: "Next"), second]).count, 3)
        let later = ChatMessage(role: .assistant, blocks: second.blocks, timestamp: date.addingTimeInterval(301))
        XCTAssertEqual(PresentedMessage.make(from: [first, later]).count, 2)
        var audio = second
        audio.audioTurnId = "audio"
        XCTAssertEqual(PresentedMessage.make(from: [first, audio]).count, 2)
        XCTAssertEqual(PresentedMessage.make(from: [audio, first]).count, 2)
    }
}
