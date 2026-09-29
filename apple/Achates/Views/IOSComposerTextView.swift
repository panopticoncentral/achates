#if os(iOS)
import SwiftUI
import UIKit

/// Keep Return handling in the text input so modifiers and the selection survive
/// hardware keyboards, including keyboards shared from a Mac.
struct IOSComposerTextView: UIViewRepresentable {
    @Binding var text: String
    @Binding var isFocused: Bool
    let onSend: () -> Void

    func makeUIView(context: Context) -> ComposerUITextView {
        let view = ComposerUITextView()
        view.backgroundColor = .clear
        view.font = .preferredFont(forTextStyle: .body)
        view.adjustsFontForContentSizeCategory = true
        view.textColor = .label
        view.textContainerInset = .zero
        view.textContainer.lineFragmentPadding = 0
        view.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        view.accessibilityLabel = "Message"
        view.returnKeyType = .send
        view.delegate = context.coordinator
        view.text = text
        view.onSend = onSend
        return view
    }

    func updateUIView(_ view: ComposerUITextView, context: Context) {
        context.coordinator.parent = self
        view.onSend = onSend
        if view.text != text {
            view.text = text
        }
        view.setNeedsDisplay()
        view.wantsFocus = isFocused
        if view.wantsFocus, view.window != nil, !view.isFirstResponder {
            view.becomeFirstResponder()
        } else if !view.wantsFocus, view.isFirstResponder {
            view.resignFirstResponder()
        }
    }

    func sizeThatFits(_ proposal: ProposedViewSize, uiView: ComposerUITextView, context: Context) -> CGSize? {
        guard let width = proposal.width, width > 0 else { return nil }
        let lineHeight = uiView.font?.lineHeight ?? 20
        let fitting = uiView.sizeThatFits(CGSize(width: width, height: .greatestFiniteMagnitude))
        return CGSize(width: width, height: min(max(fitting.height, lineHeight), lineHeight * 6).rounded(.up))
    }

    func makeCoordinator() -> Coordinator { Coordinator(parent: self) }

    final class Coordinator: NSObject, UITextViewDelegate {
        var parent: IOSComposerTextView

        init(parent: IOSComposerTextView) { self.parent = parent }

        func textViewDidChange(_ textView: UITextView) {
            parent.text = textView.text
            textView.invalidateIntrinsicContentSize()
            textView.setNeedsDisplay()
        }

        func textViewDidBeginEditing(_ textView: UITextView) { parent.isFocused = true }
        func textViewDidEndEditing(_ textView: UITextView) { parent.isFocused = false }
    }
}

final class ComposerUITextView: UITextView {
    var onSend: () -> Void = {}
    var wantsFocus = false

    override func didMoveToWindow() {
        super.didMoveToWindow()
        if window != nil, wantsFocus { becomeFirstResponder() }
    }

    override var keyCommands: [UIKeyCommand]? {
        // Let the input method handle Return while committing marked text.
        guard markedTextRange == nil else { return super.keyCommands }
        let newline = UIKeyCommand(input: "\r", modifierFlags: .shift, action: #selector(insertComposerNewline(_:)))
        newline.wantsPriorityOverSystemBehavior = true
        return [newline] + (super.keyCommands ?? [])
    }

    override func canPerformAction(_ action: Selector, withSender sender: Any?) -> Bool {
        if action == #selector(insertComposerNewline(_:)) { return markedTextRange == nil }
        return super.canPerformAction(action, withSender: sender)
    }

    override func insertText(_ text: String) {
        if (text == "\n" || text == "\r"), markedTextRange == nil {
            onSend()
        } else {
            super.insertText(text)
        }
    }

    @objc private func insertComposerNewline(_ command: UIKeyCommand) {
        guard markedTextRange == nil else { return }
        // UIKit replaces the selection and maintains undo and caret movement.
        // Bypass our unmodified Return override, which submits the draft.
        super.insertText("\n")
    }

    override func draw(_ rect: CGRect) {
        super.draw(rect)
        guard text.isEmpty else { return }
        ("Message" as NSString).draw(at: CGPoint(x: 0, y: textContainerInset.top), withAttributes: [
            .font: font ?? .preferredFont(forTextStyle: .body),
            .foregroundColor: UIColor.placeholderText
        ])
    }
}
#endif
