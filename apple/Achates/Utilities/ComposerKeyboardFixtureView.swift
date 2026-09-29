#if DEBUG && os(iOS)
import SwiftUI

/// Exercises real keyboard dispatch without a server or saved user data.
struct ComposerKeyboardFixtureView: View {
    @State private var text = ""
    @State private var sent = ""
    @State private var focused = false

    var body: some View {
        VStack {
            Text(sent).accessibilityIdentifier("submitted-message")
            IOSComposerTextView(text: $text, isFocused: $focused) {
                sent = text
                text = ""
            }
            .frame(maxWidth: 400)
            .padding()
            .border(.secondary)
        }
        .onAppear { focused = true }
    }
}
#endif
