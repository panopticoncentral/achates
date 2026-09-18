import Foundation
import Observation

/// A draft belongs to a conversation, independently of navigation/view lifetime.
@Observable
@MainActor
final class ConversationDraft {
    struct Edit: Equatable {
        let text: String
        let attachments: [DraftAttachment]
    }

    var text = ""
    var attachments: [DraftAttachment] = []
    private(set) var pendingEdit: Edit?
    private var previousDraft: Edit?

    func beginEditing(_ edit: Edit) {
        if pendingEdit == nil {
            previousDraft = Edit(text: text, attachments: attachments)
        }
        pendingEdit = edit
        text = edit.text
        attachments = edit.attachments
    }

    func endEditing() {
        text = previousDraft?.text ?? ""
        attachments = previousDraft?.attachments ?? []
        previousDraft = nil
        pendingEdit = nil
    }

    func didSubmit() {
        if pendingEdit != nil {
            endEditing()
        } else {
            text = ""
            attachments = []
        }
    }
}

struct ConversationKey: Hashable {
    let server: String
    let agentID: String
    let sessionID: String
}
