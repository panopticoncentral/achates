import SwiftUI
import UniformTypeIdentifiers
#if os(iOS)
import UIKit
import PhotosUI
#else
import AppKit
#endif

struct ComposerView: View {
    @Environment(AppState.self) private var appState
    @Bindable var speechService: SpeechService
    @State private var text = ""
    @State private var attachments: [DraftAttachment] = []
    @State private var showDocumentPicker = false
    @FocusState private var isFocused: Bool
    #if os(macOS)
    @State private var composerHeight: CGFloat = 33
    #endif

    #if os(iOS)
    @State private var showSourceDialog = false
    @State private var showCamera = false
    @State private var showLibrary = false
    @State private var pickerItems: [PhotosPickerItem] = []
    #endif

    let onSend: (String, [DraftAttachment]) -> Void
    let onCancel: () -> Void

    private let maxAttachments = 4
    private static let maxPdfBytes = 32 * 1024 * 1024

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
        .confirmationDialog("Add Attachment", isPresented: $showSourceDialog, titleVisibility: .hidden) {
            if CameraPicker.isAvailable {
                Button("Take Photo") { showCamera = true }
            }
            Button("Choose from Library") { showLibrary = true }
            Button("Choose Document") { showDocumentPicker = true }
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
        .fileImporter(
            isPresented: $showDocumentPicker,
            allowedContentTypes: [.pdf],
            allowsMultipleSelection: true
        ) { result in
            switch result {
            case .success(let urls): addDocuments(urls)
            case .failure(let error): print("Document picker failed: \(error)")
            }
        }
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

            #if os(macOS)
            MacComposerTextView(
                text: $text,
                measuredHeight: $composerHeight,
                placeholder: "Message",
                minHeight: 33,
                maxHeight: 240,
                onSend: send
            )
            .frame(maxWidth: .infinity)
            .frame(height: composerHeight)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(
                RoundedRectangle(cornerRadius: 20, style: .continuous)
                    .fill(Color(.systemGray6))
            )
            .focused($isFocused)
            #else
            TextField("Message", text: $text, axis: .vertical)
                .textFieldStyle(.plain)
                .lineLimit(1...6)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(
                    RoundedRectangle(cornerRadius: 20, style: .continuous)
                        .fill(Color(.systemGray6))
                )
                .focused($isFocused)
                .onSubmit { send() }
            #endif

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
                .font(.system(size: 32))
                .foregroundStyle(attachments.count >= maxAttachments ? Color.gray : Color.blue)
                .frame(width: 36, height: 36)
        }
        .buttonStyle(.plain)
        .disabled(attachments.count >= maxAttachments)
        .accessibilityLabel("Add attachment")
        #else
        Menu {
            Button("Photo...") { openMacPhotoPicker() }
            Button("Document...") { showDocumentPicker = true }
        } label: {
            Image(systemName: "plus.circle.fill")
                .font(.system(size: 32))
                .foregroundStyle(attachments.count >= maxAttachments ? Color.gray : Color.blue)
                .frame(width: 36, height: 36)
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .disabled(attachments.count >= maxAttachments)
        .accessibilityLabel("Add attachment")
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
        #if os(macOS)
        composerHeight = 33
        #endif
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
        attachments.append(DraftAttachment(
            data: result.data,
            mime: "image/jpeg",
            thumbnail: result.thumbnail
        ))
    }

    private func addDocuments(_ urls: [URL]) {
        for url in urls {
            if attachments.count >= maxAttachments { break }
            let didStart = url.startAccessingSecurityScopedResource()
            defer { if didStart { url.stopAccessingSecurityScopedResource() } }
            guard let data = try? Data(contentsOf: url) else { continue }
            guard data.count <= Self.maxPdfBytes else {
                print("Skipping \(url.lastPathComponent): \(data.count) bytes exceeds 32 MB cap")
                continue
            }
            attachments.append(DraftAttachment(
                data: data,
                mime: "application/pdf",
                displayName: url.lastPathComponent
            ))
        }
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
    private func openMacPhotoPicker() {
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
            content

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

    @ViewBuilder
    private var content: some View {
        if attachment.isImage, let thumb = attachment.thumbnail {
            #if os(iOS)
            Image(uiImage: thumb)
                .resizable()
                .aspectRatio(contentMode: .fill)
                .frame(width: 64, height: 64)
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
            #else
            Image(nsImage: thumb)
                .resizable()
                .aspectRatio(contentMode: .fill)
                .frame(width: 64, height: 64)
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
            #endif
        } else {
            documentChip
        }
    }

    private var documentChip: some View {
        VStack(spacing: 4) {
            Image(systemName: "doc.fill")
                .font(.system(size: 22))
                .foregroundStyle(.white)
            Text(attachment.displayName ?? "Document")
                .font(.caption2)
                .foregroundStyle(.white)
                .lineLimit(2)
                .truncationMode(.middle)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 4)
        }
        .frame(width: 88, height: 64)
        .background(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(Color.accentColor.opacity(0.85))
        )
    }
}

#if os(macOS)
// SwiftUI controls the height of this view via a `@Binding<CGFloat>` that the
// coordinator updates in response to text changes and bounds changes. Crucially,
// this view does NOT implement `sizeThatFits` — height is reported asynchronously
// (via `DispatchQueue.main.async`), which decouples the height computation from
// the layout pass that produced the bounds. Reporting size synchronously from
// `sizeThatFits` previously caused a constraint update loop on macOS Tahoe:
// frame change → `NSHostingView.invalidateSafeAreaCornerInsets` → another layout
// pass → another (slightly different) `sizeThatFits` result → repeat, eventually
// tripping the window's "more update passes than views" assertion.
struct MacComposerTextView: NSViewRepresentable {
    @Binding var text: String
    @Binding var measuredHeight: CGFloat
    let placeholder: String
    let minHeight: CGFloat
    let maxHeight: CGFloat
    let onSend: () -> Void

    func makeNSView(context: Context) -> NSScrollView {
        let scrollView = NSScrollView()
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = false
        scrollView.drawsBackground = false
        scrollView.borderType = .noBorder
        scrollView.autohidesScrollers = true
        scrollView.scrollerStyle = .overlay
        scrollView.postsFrameChangedNotifications = true

        let textView = ComposerNSTextView()
        textView.delegate = context.coordinator
        textView.allowsUndo = true
        textView.isRichText = false
        textView.isEditable = true
        textView.font = .systemFont(ofSize: NSFont.systemFontSize)
        textView.drawsBackground = false
        textView.backgroundColor = .clear
        textView.textColor = .labelColor
        textView.insertionPointColor = .labelColor
        textView.textContainerInset = NSSize(width: 4, height: 6)
        textView.minSize = .zero
        textView.maxSize = NSSize(
            width: CGFloat.greatestFiniteMagnitude,
            height: CGFloat.greatestFiniteMagnitude
        )
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.autoresizingMask = [.width]
        if let container = textView.textContainer {
            container.widthTracksTextView = true
            container.heightTracksTextView = false
            container.containerSize = NSSize(
                width: 100,
                height: CGFloat.greatestFiniteMagnitude
            )
        }
        textView.string = text
        textView.placeholderString = placeholder
        textView.onSend = onSend

        scrollView.documentView = textView
        context.coordinator.observe(scrollView: scrollView)
        return scrollView
    }

    func updateNSView(_ nsView: NSScrollView, context: Context) {
        guard let textView = nsView.documentView as? ComposerNSTextView else { return }
        if textView.string != text {
            textView.string = text
            // Programmatic edits don't fire textDidChange — re-measure manually.
            context.coordinator.scheduleMeasure()
        }
        if textView.placeholderString != placeholder {
            textView.placeholderString = placeholder
        }
        textView.onSend = onSend
    }

    func makeCoordinator() -> Coordinator { Coordinator(parent: self) }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var parent: MacComposerTextView
        weak var scrollView: NSScrollView?

        init(parent: MacComposerTextView) {
            self.parent = parent
            super.init()
        }

        deinit {
            NotificationCenter.default.removeObserver(self)
        }

        func observe(scrollView: NSScrollView) {
            self.scrollView = scrollView
            NotificationCenter.default.addObserver(
                self,
                selector: #selector(scrollViewFrameChanged(_:)),
                name: NSView.frameDidChangeNotification,
                object: scrollView
            )
        }

        @objc private func scrollViewFrameChanged(_ note: Notification) {
            scheduleMeasure()
        }

        func textDidChange(_ notification: Notification) {
            guard let textView = notification.object as? NSTextView else { return }
            parent.text = textView.string
            scheduleMeasure()
        }

        // Always update height off the current layout pass, never inside it.
        // The async hop is what breaks the AppKit/SwiftUI feedback loop.
        func scheduleMeasure() {
            DispatchQueue.main.async { [weak self] in
                self?.measureNow()
            }
        }

        private func measureNow() {
            guard let scrollView,
                  let textView = scrollView.documentView as? ComposerNSTextView,
                  let layoutManager = textView.layoutManager,
                  let textContainer = textView.textContainer
            else { return }

            layoutManager.ensureLayout(for: textContainer)
            let usedRect = layoutManager.usedRect(for: textContainer)
            let totalHeight = usedRect.height + 2 * textView.textContainerInset.height + 4
            let clamped = min(max(totalHeight.rounded(.up), parent.minHeight), parent.maxHeight)

            // Guard against redundant writes so a height-driven frame change
            // (which fires frameDidChangeNotification again) settles immediately.
            if abs(parent.measuredHeight - clamped) >= 0.5 {
                parent.measuredHeight = clamped
            }
        }
    }
}

private final class ComposerNSTextView: NSTextView {
    var onSend: (() -> Void)?
    var placeholderString: String = "" {
        didSet {
            if oldValue != placeholderString { needsDisplay = true }
        }
    }

    override var intrinsicContentSize: NSSize {
        NSSize(width: NSView.noIntrinsicMetric, height: NSView.noIntrinsicMetric)
    }

    override func keyDown(with event: NSEvent) {
        // Return key (keyCode 36); Enter on numeric keypad (76) is treated the same
        if event.keyCode == 36 || event.keyCode == 76 {
            if event.modifierFlags.contains(.shift) {
                insertText("\n", replacementRange: selectedRange())
            } else {
                onSend?()
            }
            return
        }
        super.keyDown(with: event)
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        guard string.isEmpty, !placeholderString.isEmpty else { return }
        let attrs: [NSAttributedString.Key: Any] = [
            .font: font ?? .systemFont(ofSize: NSFont.systemFontSize),
            .foregroundColor: NSColor.placeholderTextColor
        ]
        let origin = NSPoint(x: textContainerInset.width + 5, y: textContainerInset.height)
        placeholderString.draw(at: origin, withAttributes: attrs)
    }
}
#endif
