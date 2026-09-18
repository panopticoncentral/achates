import SwiftUI

struct InterfaceCommand {
    var isEnabled = true
    let perform: () -> Void
}

private struct SaveEditorKey: FocusedValueKey { typealias Value = InterfaceCommand }
private struct FindConversationKey: FocusedValueKey { typealias Value = InterfaceCommand }
private struct RenameConversationKey: FocusedValueKey { typealias Value = InterfaceCommand }

extension FocusedValues {
    var saveEditor: InterfaceCommand? {
        get { self[SaveEditorKey.self] }
        set { self[SaveEditorKey.self] = newValue }
    }
    var findConversation: InterfaceCommand? {
        get { self[FindConversationKey.self] }
        set { self[FindConversationKey.self] = newValue }
    }
    var renameConversation: InterfaceCommand? {
        get { self[RenameConversationKey.self] }
        set { self[RenameConversationKey.self] = newValue }
    }
}

struct EditorCommands: Commands {
    @FocusedValue(\.saveEditor) private var save
    @FocusedValue(\.findConversation) private var find
    @FocusedValue(\.renameConversation) private var rename

    var body: some Commands {
        CommandGroup(replacing: .saveItem) {
            Button("Save") { save?.perform() }
                .keyboardShortcut("s", modifiers: .command)
                .disabled(save?.isEnabled != true)
        }
        CommandGroup(after: .textEditing) {
            Button("Find in Conversation…") { find?.perform() }
                .keyboardShortcut("f", modifiers: .command)
                .disabled(find?.isEnabled != true)
        }
        CommandGroup(after: .newItem) {
            Button("Rename Conversation…") { rename?.perform() }
                .disabled(rename?.isEnabled != true)
        }
    }
}
