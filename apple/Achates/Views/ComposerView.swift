import SwiftUI
#if os(iOS)
import UIKit
import PhotosUI
#else
import AppKit
import UniformTypeIdentifiers
#endif

struct ComposerView: View {
    @Environment(AppState.self) private var appState
    @Bindable var speechService: SpeechService
    @State private var text = ""
    @State private var attachments: [DraftAttachment] = []
    @FocusState private var isFocused: Bool

    #if os(iOS)
    @State private var showSourceDialog = false
    @State private var showCamera = false
    @State private var showLibrary = false
    @State private var pickerItems: [PhotosPickerItem] = []
    #endif

    let onSend: (String, [DraftAttachment]) -> Void
    let onCancel: () -> Void

    private let maxAttachments = 4

    var body: some View {
        VStack(spacing: 0) {
            if speechService.isRecording {
                recordingBanner
            }

            if !attachments.isEmpty {
                attachmentStrip
            }

            inputRow
        }
        .background(.bar)
        #if os(iOS)
        .confirmationDialog("Add Photo", isPresented: $showSourceDialog, titleVisibility: .hidden) {
            if CameraPicker.isAvailable {
                Button("Take Photo") { showCamera = true }
            }
            Button("Choose from Library") { showLibrary = true }
            Button("Cancel", role: .cancel) {}
        }
        .fullScreenCover(isPresented: $showCamera) {
            CameraPicker(
                onCapture: { data in
                    showCamera = false
                    addAttachment(from: data)
                },
                onCancel: { showCamera = false }
            )
            .ignoresSafeArea()
        }
        .photosPicker(
            isPresented: $showLibrary,
            selection: $pickerItems,
            maxSelectionCount: max(1, maxAttachments - attachments.count),
            selectionBehavior: .ordered,
            matching: .images
        )
        .onChange(of: pickerItems) { _, items in
            Task { await loadPickerItems(items) }
        }
        #endif
    }

    private var recordingBanner: some View {
        HStack {
            Image(systemName: "waveform")
                .foregroundStyle(.red)
                .symbolEffect(.variableColor)
            Text(speechService.transcript.isEmpty ? "Listening..." : speechService.transcript)
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .lineLimit(2)
            Spacer()
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .background(Color(.systemGray6))
    }

    private var attachmentStrip: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(attachments) { att in
                    AttachmentThumbnail(attachment: att) {
                        attachments.removeAll { $0.id == att.id }
                    }
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
        }
    }

    @ViewBuilder
    private var inputRow: some View {
        HStack(alignment: .bottom, spacing: 8) {
            attachmentButton

            TextField("Message", text: $text, axis: .vertical)
                .textFieldStyle(.plain)
                #if os(macOS)
                .lineLimit(1...12)
                #else
                .lineLimit(1...6)
                #endif
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(
                    RoundedRectangle(cornerRadius: 20, style: .continuous)
                        .fill(Color(.systemGray6))
                )
                .focused($isFocused)
                .onSubmit { send() }

            trailingButton
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    @ViewBuilder
    private var attachmentButton: some View {
        #if os(iOS)
        Button {
            showSourceDialog = true
        } label: {
            Image(systemName: "plus.circle.fill")
                .font(.system(size: 28))
                .foregroundStyle(attachments.count >= maxAttachments ? Color.gray : Color.blue)
                .frame(width: 36, height: 36)
        }
        .buttonStyle(.plain)
        .disabled(attachments.count >= maxAttachments)
        .accessibilityLabel("Add photo")
        #else
        Button {
            openMacFilePicker()
        } label: {
            Image(systemName: "plus.circle.fill")
                .font(.system(size: 28))
                .foregroundStyle(attachments.count >= maxAttachments ? Color.gray : Color.blue)
                .frame(width: 36, height: 36)
        }
        .buttonStyle(.plain)
        .disabled(attachments.count >= maxAttachments)
        .accessibilityLabel("Add photo")
        #endif
    }

    @ViewBuilder
    private var trailingButton: some View {
        if appState.isStreaming {
            Button(action: onCancel) {
                Image(systemName: "stop.circle.fill")
                    .font(.system(size: 32))
                    .foregroundStyle(.red)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Stop generating")
        } else if isInputEmpty {
            Button(action: toggleRecording) {
                Image(systemName: speechService.isRecording ? "mic.fill" : "mic")
                    .font(.system(size: 20))
                    .foregroundStyle(speechService.isRecording ? .red : .blue)
                    .frame(width: 36, height: 36)
                    .background(
                        Circle()
                            .fill(speechService.isRecording ? Color.red.opacity(0.15) : Color(.systemGray6))
                    )
            }
            .buttonStyle(.plain)
            .accessibilityLabel(speechService.isRecording ? "Stop recording" : "Start recording")
        } else {
            Button(action: send) {
                Image(systemName: "arrow.up.circle.fill")
                    .font(.system(size: 32))
                    .foregroundStyle(.blue)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Send message")
        }
    }

    private var isInputEmpty: Bool {
        text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && attachments.isEmpty
    }

    private func send() {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty || !attachments.isEmpty else { return }
        #if os(iOS)
        UIImpactFeedbackGenerator(style: .light).impactOccurred()
        #endif
        onSend(trimmed, attachments)
        text = ""
        attachments = []
    }

    private func toggleRecording() {
        if speechService.isRecording {
            let transcript = speechService.stopRecording()
            if !transcript.isEmpty {
                text = transcript
            }
        } else {
            Task {
                do {
                    try await speechService.startRecording()
                } catch {
                    print("Failed to start recording: \(error)")
                }
            }
        }
    }

    private func addAttachment(from data: Data) {
        guard attachments.count < maxAttachments,
              let result = ImageProcessor.normalize(data) else { return }
        attachments.append(DraftAttachment(data: result.data, thumbnail: result.thumbnail))
    }

    #if os(iOS)
    private func loadPickerItems(_ items: [PhotosPickerItem]) async {
        guard !items.isEmpty else { return }
        defer { pickerItems = [] }
        for item in items {
            if attachments.count >= maxAttachments { break }
            if let data = try? await item.loadTransferable(type: Data.self) {
                await MainActor.run { addAttachment(from: data) }
            }
        }
    }
    #endif

    #if os(macOS)
    private func openMacFilePicker() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.allowedContentTypes = [.image]
        panel.message = "Choose photos to attach"
        if panel.runModal() == .OK {
            for url in panel.urls {
                if attachments.count >= maxAttachments { break }
                if let data = try? Data(contentsOf: url) {
                    addAttachment(from: data)
                }
            }
        }
    }
    #endif
}

private struct AttachmentThumbnail: View {
    let attachment: DraftAttachment
    let onRemove: () -> Void

    var body: some View {
        ZStack(alignment: .topTrailing) {
            #if os(iOS)
            Image(uiImage: attachment.thumbnail)
                .resizable()
                .aspectRatio(contentMode: .fill)
                .frame(width: 64, height: 64)
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
            #else
            Image(nsImage: attachment.thumbnail)
                .resizable()
                .aspectRatio(contentMode: .fill)
                .frame(width: 64, height: 64)
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
            #endif

            Button(action: onRemove) {
                Image(systemName: "xmark.circle.fill")
                    .font(.system(size: 18))
                    .symbolRenderingMode(.palette)
                    .foregroundStyle(.white, .black.opacity(0.7))
                    .frame(width: 28, height: 28)
                    .contentShape(Circle())
            }
            .buttonStyle(.plain)
            .offset(x: 6, y: -6)
            .accessibilityLabel("Remove attachment")
        }
        .padding(4)
    }
}
