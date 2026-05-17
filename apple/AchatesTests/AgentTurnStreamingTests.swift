import XCTest
@testable import Achates

/// Locks the two live-render defects fixed in the inter-agent chat redesign:
/// (1) a no-delta utterance (initiator's own line: start+end, no deltas) must
/// show its `text` live, and (2) multiple utterances by the same speaker in one
/// initiator turn must render as distinct bubbles instead of merging.
///
/// These exercise the pure `ChatMessage` model mutators directly — no UI, no
/// networking — mirroring the exact sequence the WebSocket layer drives.
final class AgentTurnStreamingTests: XCTestCase {

    /// Pulls the `.agentTurn` blocks out of a message as (text, collapsed) tuples.
    private func agentTurns(_ message: ChatMessage) -> [(text: String, collapsed: Bool)] {
        message.blocks.compactMap { block in
            if case .agentTurn(_, _, let text, let collapsed) = block {
                return (text, collapsed)
            }
            return nil
        }
    }

    // Defect 1: a no-delta utterance (start then end with full text, no deltas)
    // must surface its text live, not render as an empty bubble.
    func testNoDeltaUtteranceShowsEndText() {
        var message = ChatMessage(role: .assistant, blocks: [])
        message.appendAgentTurn("", agentTurnId: "u1", agentName: "Claire")
        message.endAgentTurn("the full reply")

        let turns = agentTurns(message)
        XCTAssertEqual(turns.count, 1)
        XCTAssertEqual(turns[0].text, "the full reply")
        XCTAssertTrue(turns[0].collapsed)
    }

    // Defect 1, target path: a streamed utterance reconciles to the full text
    // carried by `agent_turn.end`. Also: an empty `end` text must NOT blank an
    // already-streamed block — the accumulated deltas are preserved.
    func testStreamedUtteranceReconcilesAndEmptyEndKeepsDeltas() {
        var streamed = ChatMessage(role: .assistant, blocks: [])
        streamed.appendAgentTurn("", agentTurnId: "u1", agentName: "Claire")
        streamed.appendAgentTurnDelta("hel")
        streamed.appendAgentTurnDelta("lo")
        streamed.endAgentTurn("hello")

        let turns = agentTurns(streamed)
        XCTAssertEqual(turns.count, 1)
        XCTAssertEqual(turns[0].text, "hello")
        XCTAssertTrue(turns[0].collapsed)

        // Separate block: empty end text must keep the accumulated delta text.
        var keepDeltas = ChatMessage(role: .assistant, blocks: [])
        keepDeltas.appendAgentTurn("", agentTurnId: "u2", agentName: "Claire")
        keepDeltas.appendAgentTurnDelta("partial")
        keepDeltas.endAgentTurn("")

        let kept = agentTurns(keepDeltas)
        XCTAssertEqual(kept.count, 1)
        XCTAssertEqual(kept[0].text, "partial")
        XCTAssertTrue(kept[0].collapsed)
    }

    // Defect 2: two sequential utterances by the same speaker (distinct ids, as
    // the fixed WebSocketClient now mints) must render as two separate bubbles,
    // not merge into one.
    func testMultiRoundUtterancesStayDistinct() {
        var message = ChatMessage(role: .assistant, blocks: [])

        message.appendAgentTurn("", agentTurnId: "id-A", agentName: "Claire")
        message.appendAgentTurnDelta("first")
        message.endAgentTurn("first")

        message.appendAgentTurn("", agentTurnId: "id-B", agentName: "Claire")
        message.appendAgentTurnDelta("second")
        message.endAgentTurn("second")

        let turns = agentTurns(message)
        XCTAssertEqual(turns.count, 2)
        XCTAssertEqual(turns.map(\.text), ["first", "second"])
        XCTAssertTrue(turns.allSatisfy(\.collapsed))
    }
}
