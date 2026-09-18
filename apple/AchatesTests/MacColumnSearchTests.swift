#if os(macOS)
import AppKit
import SwiftUI
import XCTest
@testable import Achates

@MainActor
final class MacColumnSearchTests: XCTestCase {
    func testSearchInsetDoesNotConsumeColumnHeight() async throws {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 280, height: 600),
                              styleMask: [.titled, .resizable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        let host = NSHostingView(rootView:
            Color.clear.safeAreaInset(edge: .top, spacing: 0) {
                ColumnSearchField(text: .constant(""), prompt: "Search loaded conversations")
                    .padding(10)
            })
        window.contentView = host
        window.makeKeyAndOrderFront(nil)
        defer { window.close() }

        for height in [600.0, 350.0] {
            window.setContentSize(NSSize(width: 280, height: height))
            try await Task.sleep(for: .milliseconds(150))
            host.layoutSubtreeIfNeeded()
            let field = try XCTUnwrap(findSearchField(in: host))
            XCTAssertGreaterThan(field.frame.width, 200)
            XCTAssertGreaterThan(field.frame.height, 15)
            XCTAssertLessThan(field.frame.height, 40, "An inset search field must not displace the list")
        }
    }

    private func findSearchField(in view: NSView) -> NSSearchField? {
        if let field = view as? NSSearchField { return field }
        return view.subviews.lazy.compactMap { self.findSearchField(in: $0) }.first
    }
}
#endif
