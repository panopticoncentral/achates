import SwiftUI

extension Font {
    // List-row typography. iOS uses Dynamic Type text styles so rows scale with
    // the user's text-size setting. macOS's semantic styles run ~4pt smaller
    // (`.subheadline` is 11pt there vs 15 on iOS) and the platform has minimal
    // Dynamic Type, so it uses fixed sizes matching the rows' original design.

    /// Primary line — agent / session name.
    static var listRowTitle: Font {
        #if os(macOS)
        .system(size: 15, weight: .semibold)
        #else
        .headline
        #endif
    }

    /// Secondary line — description / message preview.
    static var listRowSubtitle: Font {
        #if os(macOS)
        .system(size: 13)
        #else
        .subheadline
        #endif
    }

    /// Trailing caption — timestamps.
    static var listRowCaption: Font {
        #if os(macOS)
        .system(size: 12)
        #else
        .footnote
        #endif
    }
}
