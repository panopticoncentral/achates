#if os(iOS)
import XCTest
import SwiftUI
import UIKit
@testable import Achates

@MainActor
final class ComposerKeyboardTests: XCTestCase {
    func testShiftReturnInsertsAtCaretWithoutSending() throws {
        let view = ComposerUITextView()
        var sends = 0
        view.onSend = { sends += 1 }
        view.text = "firstsecond"
        view.selectedRange = NSRange(location: 5, length: 0)

        try shiftReturn(in: view)

        XCTAssertEqual(view.text, "first\nsecond")
        XCTAssertEqual(view.selectedRange, NSRange(location: 6, length: 0))
        XCTAssertEqual(sends, 0)
    }

    func testShiftReturnReplacesSelectionWithUnicodeBeforeIt() throws {
        let view = ComposerUITextView()
        view.onSend = { XCTFail("Shift+Return must not send") }
        view.text = "👋 replace this end"
        view.selectedRange = (view.text as NSString).range(of: "replace this")

        try shiftReturn(in: view)

        XCTAssertEqual(view.text, "👋 \n end")
        XCTAssertEqual(view.selectedRange, NSRange(location: 4, length: 0))
    }

    func testUnmodifiedReturnSendsWithoutChangingDraft() {
        let view = ComposerUITextView()
        var sends = 0
        view.onSend = { sends += 1 }
        view.text = "draft"

        view.insertText("\n")

        XCTAssertEqual(sends, 1)
        XCTAssertEqual(view.text, "draft")
    }

    func testMultilineInsertionDoesNotSend() {
        let view = ComposerUITextView()
        view.onSend = { XCTFail("Multiline input must not send") }

        view.insertText("first\nsecond")

        XCTAssertEqual(view.text, "first\nsecond")
    }

    func testReturnDuringCompositionDoesNotSend() throws {
        let view = ComposerUITextView()
        view.onSend = { XCTFail("Committing marked text must not send") }
        view.setMarkedText("に", selectedRange: NSRange(location: 1, length: 0))
        XCTAssertNotNil(view.markedTextRange)
        XCTAssertFalse(view.keyCommands?.contains { $0.action == NSSelectorFromString("insertComposerNewline:") } ?? false)

        view.insertText("\n")
    }

    func testHostedComposerUpdatesDraftAndResizes() async throws {
        let draft = ConversationDraft()
        draft.text = "firstsecond"
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let window = UIWindow(windowScene: scene)
        let host = UIHostingController(rootView: ComposerHarness(draft: draft))
        window.rootViewController = host
        window.makeKeyAndVisible()
        defer { window.isHidden = true }
        try await Task.sleep(for: .milliseconds(200))
        host.view.layoutIfNeeded()
        let view = try XCTUnwrap(findComposer(in: host.view))
        let initialHeight = view.bounds.height
        XCTAssertTrue(view.isFirstResponder)
        XCTAssertGreaterThan(initialHeight, 0)
        view.selectedRange = NSRange(location: 5, length: 0)

        try shiftReturn(in: view)
        try await Task.sleep(for: .milliseconds(100))
        host.view.layoutIfNeeded()

        XCTAssertEqual(draft.text, "first\nsecond")
        XCTAssertGreaterThan(view.bounds.height, initialHeight)

        draft.text = Array(repeating: "line", count: 12).joined(separator: "\n")
        try await Task.sleep(for: .milliseconds(100))
        host.view.layoutIfNeeded()
        XCTAssertEqual(view.text, draft.text)
        XCTAssertLessThanOrEqual(view.bounds.height, (try XCTUnwrap(view.font).lineHeight * 6).rounded(.up))
        XCTAssertTrue(view.isScrollEnabled)

        draft.didSubmit()
        try await Task.sleep(for: .milliseconds(100))
        host.view.layoutIfNeeded()
        XCTAssertEqual(view.text, "")
        XCTAssertEqual(view.bounds.height, initialHeight, accuracy: 1)
    }

    private func findComposer(in view: UIView) -> ComposerUITextView? {
        if let composer = view as? ComposerUITextView { return composer }
        return view.subviews.lazy.compactMap { self.findComposer(in: $0) }.first
    }

    private func shiftReturn(in view: ComposerUITextView) throws {
        let command = try XCTUnwrap(view.keyCommands?.first {
            $0.input == "\r" && $0.modifierFlags == .shift
        })
        XCTAssertTrue(command.wantsPriorityOverSystemBehavior)
        let action = try XCTUnwrap(command.action)
        XCTAssertTrue(view.canPerformAction(action, withSender: command))
        view.perform(action, with: command)
    }
}

private struct ComposerHarness: View {
    @Bindable var draft: ConversationDraft
    @State private var focused = false

    var body: some View {
        IOSComposerTextView(text: $draft.text, isFocused: $focused) {
            XCTFail("Shift+Return must not submit through the SwiftUI wrapper")
        }
        .frame(width: 300)
        .onAppear { focused = true }
    }
}
#endif
