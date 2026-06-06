import XCTest
@testable import Achates

/// Exercises the pure turn-taking reducer for hands-free conversation mode.
/// No audio, no networking — same approach as AgentTurnStreamingTests.
final class ConversationMachineTests: XCTestCase {

    func testBeginEntersListening() {
        var m = ConversationMachine()
        let intents = m.handle(.begin)
        XCTAssertEqual(m.state, .listening)
        XCTAssertEqual(intents, [.startListening])
    }

    func testSpeechCapturedSendsTurn() {
        var m = ConversationMachine()
        _ = m.handle(.begin)
        let intents = m.handle(.speechCaptured("hello there"))
        XCTAssertEqual(m.state, .sending)
        XCTAssertEqual(intents, [.stopListening, .sendTurn("hello there")])
    }

    func testTurnCompletedWithNoAudioResumesListening() {
        var m = ConversationMachine()
        _ = m.handle(.begin)
        _ = m.handle(.speechCaptured("hi"))
        let intents = m.handle(.turnCompleted(isPlaying: false))
        XCTAssertEqual(m.state, .listening)
        XCTAssertEqual(intents, [.startListening])
    }

    func testTurnCompletedWhilePlayingEntersSpeaking() {
        var m = ConversationMachine()
        _ = m.handle(.begin)
        _ = m.handle(.speechCaptured("hi"))
        let intents = m.handle(.turnCompleted(isPlaying: true))
        XCTAssertEqual(m.state, .speaking)
        XCTAssertEqual(intents, [])
    }

    func testPlaybackFinishedResumesListening() {
        var m = ConversationMachine()
        _ = m.handle(.begin)
        _ = m.handle(.speechCaptured("hi"))
        _ = m.handle(.turnCompleted(isPlaying: true))
        let intents = m.handle(.playbackFinished)
        XCTAssertEqual(m.state, .listening)
        XCTAssertEqual(intents, [.startListening])
    }

    func testPlaybackDrainBeforeDoneIsIgnored() {
        var m = ConversationMachine()
        _ = m.handle(.begin)
        _ = m.handle(.speechCaptured("hi"))
        // Queue briefly drains between streamed sentences, before `done`.
        let intents = m.handle(.playbackFinished)
        XCTAssertEqual(m.state, .sending)
        XCTAssertEqual(intents, [])
    }

    func testInterruptionPausesAndResumes() {
        var m = ConversationMachine()
        _ = m.handle(.begin)
        let pause = m.handle(.interrupted)
        XCTAssertEqual(m.state, .paused)
        XCTAssertEqual(pause, [.stopListening, .stopPlayback])
        let resume = m.handle(.resumed)
        XCTAssertEqual(m.state, .listening)
        XCTAssertEqual(resume, [.startListening])
    }

    func testTurnFailedResumesListening() {
        var m = ConversationMachine()
        _ = m.handle(.begin)
        _ = m.handle(.speechCaptured("hi"))
        let intents = m.handle(.turnFailed("network drop"))
        XCTAssertEqual(m.state, .listening)
        XCTAssertEqual(intents, [.stopPlayback, .startListening])
    }

    func testStartFailedEndsInFailure() {
        var m = ConversationMachine()
        _ = m.handle(.begin)
        let intents = m.handle(.startFailed("mic denied"))
        XCTAssertEqual(m.state, .failed("mic denied"))
        XCTAssertEqual(intents, [.teardown])
    }

    func testEndRequestedTearsDownFromAnyState() {
        var m = ConversationMachine()
        _ = m.handle(.begin)
        _ = m.handle(.speechCaptured("hi"))
        _ = m.handle(.turnCompleted(isPlaying: true))
        let intents = m.handle(.endRequested)
        XCTAssertEqual(m.state, .ended)
        XCTAssertEqual(intents, [.stopListening, .stopPlayback, .teardown])
    }

    func testFullLoop() {
        var m = ConversationMachine()
        XCTAssertEqual(m.handle(.begin), [.startListening])
        XCTAssertEqual(m.handle(.speechCaptured("turn one")), [.stopListening, .sendTurn("turn one")])
        XCTAssertEqual(m.handle(.turnCompleted(isPlaying: true)), [])
        XCTAssertEqual(m.handle(.playbackFinished), [.startListening])
        XCTAssertEqual(m.state, .listening)
        // Second turn proceeds the same way.
        XCTAssertEqual(m.handle(.speechCaptured("turn two")), [.stopListening, .sendTurn("turn two")])
        XCTAssertEqual(m.state, .sending)
    }
}
