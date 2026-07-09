import XCTest
@testable import Achates

/// Pins the branch selection of the shared compact-timestamp helper used by the
/// agent and session list rows. Assertions compare against the same locale-aware
/// `.formatted()` calls the implementation uses, so they verify WHICH branch is
/// taken without hardcoding a locale/24h-specific string (the bug this replaces
/// was a hardcoded "h:mm a" that ignored 24-hour locales).
final class ChatListLabelTests: XCTestCase {
    private let cal = Calendar(identifier: .gregorian)

    /// A fixed "now": 2026-07-08 14:30 local.
    private var now: Date {
        cal.date(from: DateComponents(year: 2026, month: 7, day: 8, hour: 14, minute: 30))!
    }

    func testTodayShowsShortenedTime() {
        let date = cal.date(byAdding: .hour, value: -2, to: now)!
        XCTAssertEqual(
            date.chatListLabel(relativeTo: now, calendar: cal),
            date.formatted(date: .omitted, time: .shortened)
        )
    }

    func testYesterdayShowsLiteral() {
        let date = cal.date(byAdding: .day, value: -1, to: now)!
        XCTAssertEqual(date.chatListLabel(relativeTo: now, calendar: cal), "Yesterday")
    }

    func testWithinWeekShowsAbbreviatedWeekday() {
        let date = cal.date(byAdding: .day, value: -3, to: now)!
        XCTAssertEqual(
            date.chatListLabel(relativeTo: now, calendar: cal),
            date.formatted(.dateTime.weekday(.abbreviated))
        )
    }

    func testOlderShowsNumericDate() {
        let date = cal.date(byAdding: .day, value: -30, to: now)!
        XCTAssertEqual(
            date.chatListLabel(relativeTo: now, calendar: cal),
            date.formatted(date: .numeric, time: .omitted)
        )
    }
}
