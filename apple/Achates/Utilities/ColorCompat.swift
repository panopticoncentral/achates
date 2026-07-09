#if os(macOS)
import AppKit

// AppKit has no systemGray5/6. Mirror UIKit's adaptive values so shared views
// render correctly in both appearances — a fixed light gray here means white
// text on near-white bubbles in dark mode.
extension NSColor {
    static var systemGray5: NSColor {
        adaptive(light: NSColor(srgbRed: 0.898, green: 0.898, blue: 0.918, alpha: 1.0),
                 dark: NSColor(srgbRed: 0.173, green: 0.173, blue: 0.180, alpha: 1.0))
    }

    static var systemGray6: NSColor {
        adaptive(light: NSColor(srgbRed: 0.949, green: 0.949, blue: 0.969, alpha: 1.0),
                 dark: NSColor(srgbRed: 0.110, green: 0.110, blue: 0.118, alpha: 1.0))
    }

    private static func adaptive(light: NSColor, dark: NSColor) -> NSColor {
        NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? dark : light
        }
    }
}
#endif
