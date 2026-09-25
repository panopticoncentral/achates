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
    @Bindable var draft: ConversationDraft
    @State private var showDocumentPicker = false
    @State private var notice: String?
    @State private var isPreparingDictation = false
    @State private var isLoadingPhotos = false
    @State private var recordingTask: Task<Void, Never>?
    @FocusState private var isFocused: Bool
    #if os(macOS)
    @State private var composerHeight: CGFloat = 30
    #endif
    #if os(iOS)
    @State private var showSourceDialog = false
    @State private var showCamera = false
    @State private var showLibrary = false
    @State private var pickerItems: [PhotosPickerItem] = []
    #endif

    let onSend: (String, [DraftAttachment]) -> Void
    let onResubmit: (String, [DraftAttachment]) -> Void
    let onCancel: () -> Void

    private var text: String {
        get { draft.text }
        nonmutating set { draft.text = newValue }
    }
    private var attachments: [DraftAttachment] {
        get { draft.attachments }
        nonmutating set { draft.attachments = newValue }
    }
    private var isEditing: Bool { draft.pendingEdit != nil }

    private let maxAttachments = 4
    private static let maxPdfBytes = 32 * 1024 * 1024
    private static let maxTextBytes = 1 * 1024 * 1024

    var body: some View {
        VStack(spacing: 0) {
            if speechService.isRecording {
                recordingBanner
            }

            if isEditing {
                editingBanner
            }

            if !attachments.isEmpty {
                attachmentStrip
            }

            if let notice {
                InlineNotice(message: notice, actionTitle: "Dismiss", action: { self.notice = nil })
            }
            if isPreparingDictation || isLoadingPhotos {
                ProgressView(isPreparingDictation ? "Preparing dictation…" : "Loading photos…")
                    .font(.callout)
                    .padding(8)
            }
            inputRow
        }
        .frame(maxWidth: InterfaceMetrics.readingWidth)
        .frame(maxWidth: .infinity)
        .background(.bar)
        // Finder drags are the most natural way to attach on the Mac (and work
        // on iPad too). Images go through the normal resize path; PDFs/text
        // through the document path.
        .dropDestination(for: URL.self) { urls, _ in
            handleDroppedURLs(urls)
            return true
        }
        .onAppear { isFocused = true }
        .onChange(of: draft.pendingEdit) { _, _ in isFocused = true }
        .onDisappear {
            recordingTask?.cancel()
            _ = speechService.stopRecording()
        }
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
            allowedContentTypes: DraftAttachment.documentTypes,
            allowsMultipleSelection: true
        ) { result in
            switch result {
            case .success(let urls): addDocuments(urls)
            case .failure(let error): notice = "Couldn’t open the document. \(error.localizedDescription)"
            }
        }
    }

    private var editingBanner: some View {
        HStack(spacing: 8) {
            Image(systemName: "pencil")
                .foregroundStyle(.tint)
            Text("Editing message")
                .font(.subheadline.weight(.medium))
            Spacer()
            Button {
                cancelEdit()
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .symbolRenderingMode(.palette)
                    .foregroundStyle(.secondary, Color.messageSurface)
                    .font(.system(size: 18))
                    .frame(width: InterfaceMetrics.actionSize, height: InterfaceMetrics.actionSize)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Cancel editing")
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .background(Color.accentColor.opacity(0.08))
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
        .background(Color.messageSurface)
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
                text: $draft.text,
                measuredHeight: $composerHeight,
                isFocused: $isFocused,
                placeholder: "Message",
                minHeight: 30,
                maxHeight: 240,
                onSend: send,
                onEscape: {
                    // Esc lands in the focused text view before ChatView's
                    // .onKeyPress can see it; stop generation from here.
                    if isEditing { cancelEdit(); return true }
                    guard appState.isStreaming else { return false }
                    onCancel()
                    return true
                },
                onPasteImage: { data in
                    addAttachment(from: data)
                }
            )
            .frame(maxWidth: .infinity)
            .frame(height: composerHeight)
            .padding(.horizontal, 8)
            .padding(.vertical, 2)
            .background(
                RoundedRectangle(cornerRadius: 20, style: .continuous)
                    .fill(Color.messageSurface)
            )
            .overlay(RoundedRectangle(cornerRadius: 20).strokeBorder(.quaternary, lineWidth: 1))
            .focused($isFocused)
            #else
            TextField("Message", text: $draft.text, axis: .vertical)
                .textFieldStyle(.plain)
                .lineLimit(1...6)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(
                    RoundedRectangle(cornerRadius: 20, style: .continuous)
                        .fill(Color.messageSurface)
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
            Image(systemName: "plus")
                .font(.system(size: 24, weight: .medium))
                .foregroundStyle(attachments.count >= maxAttachments ? Color.secondary : Color.accentColor)
                .frame(width: InterfaceMetrics.actionSize, height: InterfaceMetrics.actionSize)
        }
        .buttonStyle(.plain)
        .disabled(attachments.count >= maxAttachments)
        .accessibilityLabel("Add attachment")
        .help("Add an attachment (up to four files)")
        #else
        Menu {
            Button("Photo...") { openMacPhotoPicker() }
            Button("Document...") { showDocumentPicker = true }
        } label: {
            Image(systemName: "plus")
                .font(.system(size: 24, weight: .medium))
                .foregroundStyle(attachments.count >= maxAttachments ? Color.secondary : Color.accentColor)
                .frame(width: InterfaceMetrics.actionSize, height: InterfaceMetrics.actionSize)
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .disabled(attachments.count >= maxAttachments)
        .accessibilityLabel("Add attachment")
        .help("Add an attachment (up to four files)")
        #endif
    }

    @ViewBuilder
    private var trailingButton: some View {
        if appState.isStreaming {
            Button(action: onCancel) {
                Image(systemName: "stop.circle.fill")
                    .font(.system(size: 24, weight: .medium))
                    .foregroundStyle(.red)
                    .frame(width: InterfaceMetrics.actionSize, height: InterfaceMetrics.actionSize)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Stop generating")
        } else if isInputEmpty || speechService.isRecording {
            Button(action: toggleRecording) {
                Image(systemName: speechService.isRecording ? "mic.fill" : "mic")
                    .font(.system(size: 20))
                    .foregroundStyle(speechService.isRecording ? .red : .accentColor)
                    .frame(width: InterfaceMetrics.actionSize, height: InterfaceMetrics.actionSize)
                    .background(
                        Circle()
                            .fill(speechService.isRecording ? Color.red.opacity(0.15) : Color.messageSurface)
                    )
            }
            .buttonStyle(.plain)
            .disabled(isPreparingDictation)
            .accessibilityLabel(speechService.isRecording ? "Finish dictation" : "Dictate message")
            .help(speechService.isRecording ? "Finish dictation" : "Dictate message")
        } else {
            Button(action: send) {
                Image(systemName: "arrow.up.circle.fill")
                    .font(.system(size: 24, weight: .medium))
                    .foregroundStyle(.tint)
                    .frame(width: InterfaceMetrics.actionSize, height: InterfaceMetrics.actionSize)
            }
            .buttonStyle(.plain)
            .disabled(!appState.canSubmitMessage)
            .accessibilityLabel(isEditing ? "Save and resubmit" : "Send message")
            .help(isEditing ? "Save and resubmit" : "Send message (Return)")
        }
    }

    private var isInputEmpty: Bool {
        text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && attachments.isEmpty
    }

    private func send() {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard appState.canSubmitMessage, !speechService.isRecording,
              !trimmed.isEmpty || !attachments.isEmpty else { return }
        #if os(iOS)
        UIImpactFeedbackGenerator(style: .light).impactOccurred()
        #endif
        if isEditing {
            onResubmit(trimmed, attachments)
        } else {
            onSend(trimmed, attachments)
        }
        draft.didSubmit()
        #if os(macOS)
        composerHeight = 30
        #endif
    }

    private func cancelEdit() {
        draft.endEditing()
        isFocused = true
    }

    private func toggleRecording() {
        if speechService.isRecording {
            let transcript = speechService.stopRecording()
            if !transcript.isEmpty {
                text = transcript
            }
        } else {
            recordingTask = Task {
                do {
                    isPreparingDictation = true
                    defer { isPreparingDictation = false }
                    try await speechService.startRecording()
                    if Task.isCancelled { _ = speechService.stopRecording() }
                } catch {
                    guard !Task.isCancelled else { return }
                    notice = "Couldn’t start dictation. \(error.localizedDescription) Check microphone and speech permissions in Settings."
                }
            }
        }
    }

    private func addAttachment(from data: Data) {
        guard attachments.count < maxAttachments else {
            notice = "You can attach up to four files per message."
            return
        }
        guard let result = ImageProcessor.normalize(data) else {
            notice = "This image couldn’t be opened. Try a different image."
            return
        }
        attachments.append(DraftAttachment(
            data: result.data,
            mime: "image/jpeg",
            thumbnail: result.thumbnail
        ))
    }

    /// Route dropped file URLs: images through the resize path, the rest
    /// through the document path (which enforces document size caps).
    private func handleDroppedURLs(_ urls: [URL]) {
        for url in urls {
            if attachments.count >= maxAttachments {
                notice = "You can attach up to four files. Additional files weren’t added."
                break
            }
            let isImage = UTType(filenameExtension: url.pathExtension)?.conforms(to: .image) ?? false
            if isImage {
                let didStart = url.startAccessingSecurityScopedResource()
                defer { if didStart { url.stopAccessingSecurityScopedResource() } }
                if let data = try? Data(contentsOf: url) {
                    addAttachment(from: data)
                } else { notice = "Couldn’t read \(url.lastPathComponent)." }
            } else {
                addDocuments([url])
            }
        }
    }

    private func addDocuments(_ urls: [URL]) {
        for url in urls {
            if attachments.count >= maxAttachments {
                notice = "You can attach up to four files. Additional files weren’t added."
                break
            }
            let fileType = UTType(filenameExtension: url.pathExtension)
            let isWorkbook = url.pathExtension.lowercased() == "xlsx"
            guard fileType?.conforms(to: .pdf) == true || fileType?.conforms(to: .text) == true
                || isWorkbook
                || ["md", "markdown", "txt", "log", "json", "xml", "csv"].contains(url.pathExtension.lowercased()) else {
                notice = "\(url.lastPathComponent) isn’t supported. Choose an image, PDF, Excel (.xlsx), or text file."
                continue
            }
            let didStart = url.startAccessingSecurityScopedResource()
            defer { if didStart { url.stopAccessingSecurityScopedResource() } }
            guard let data = try? Data(contentsOf: url) else {
                notice = "Couldn’t read \(url.lastPathComponent)."
                continue
            }

            let type = UTType(filenameExtension: url.pathExtension)
            let isPdf = type?.conforms(to: .pdf) ?? (url.pathExtension.lowercased() == "pdf")
            if isWorkbook {
                guard data.count <= DraftAttachment.maxWorkbookBytes else {
                    notice = "\(url.lastPathComponent) exceeds the 8 MB workbook limit."
                    continue
                }
                attachments.append(DraftAttachment(
                    data: data,
                    mime: DraftAttachment.workbookMime,
                    displayName: url.lastPathComponent
                ))
            } else if isPdf {
                guard data.count <= Self.maxPdfBytes else {
                    notice = "\(url.lastPathComponent) exceeds the 32 MB PDF limit."
                    continue
                }
                attachments.append(DraftAttachment(
                    data: data,
                    mime: "application/pdf",
                    displayName: url.lastPathComponent
                ))
            } else {
                // Treat everything else the picker allowed (public.text) as a text
                // file: it's inlined into the prompt server-side, so cap it small.
                guard data.count <= Self.maxTextBytes else {
                    notice = "\(url.lastPathComponent) exceeds the 1 MB text-file limit."
                    continue
                }
                attachments.append(DraftAttachment(
                    data: data,
                    mime: textMime(for: url),
                    displayName: url.lastPathComponent
                ))
            }
        }
    }

    /// Resolves a server-acceptable text mime for a picked file. The server accepts
    /// any `text/*` plus `application/json` / `application/xml`; anything else falls
    /// back to `text/plain` so the file is still inlined as text.
    private func textMime(for url: URL) -> String {
        if let mime = UTType(filenameExtension: url.pathExtension)?.preferredMIMEType,
           mime.hasPrefix("text/") || mime == "application/json" || mime == "application/xml" {
            return mime
        }
        switch url.pathExtension.lowercased() {
        case "csv": return "text/csv"
        case "json": return "application/json"
        case "xml": return "application/xml"
        case "md", "markdown": return "text/markdown"
        default: return "text/plain"
        }
    }

    #if os(iOS)
    private func loadPickerItems(_ items: [PhotosPickerItem]) async {
        guard !items.isEmpty else { return }
        isLoadingPhotos = true
        defer { pickerItems = []; isLoadingPhotos = false }
        for item in items {
            if attachments.count >= maxAttachments {
                notice = "You can attach up to four files. Additional files weren’t added."
                break
            }
            if let data = try? await item.loadTransferable(type: Data.self) {
                addAttachment(from: data)
            } else {
                notice = "One of the selected photos couldn’t be loaded."
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
                if attachments.count >= maxAttachments {
                notice = "You can attach up to four files. Additional files weren’t added."
                break
            }
                if let data = try? Data(contentsOf: url) {
                    addAttachment(from: data)
                } else { notice = "Couldn’t read \(url.lastPathComponent)." }
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
                    .frame(width: InterfaceMetrics.actionSize, height: InterfaceMetrics.actionSize)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .offset(x: 6, y: -6)
            .accessibilityLabel("Remove \(attachment.displayName ?? "image")")
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
            Image(systemName: DraftAttachment.documentSymbol(for: attachment.mime))
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
    var isFocused: FocusState<Bool>.Binding
    let placeholder: String
    let minHeight: CGFloat
    let maxHeight: CGFloat
    let onSend: () -> Void
    /// Return true when Esc was consumed (e.g. stop generation).
    var onEscape: () -> Bool = { false }
    /// Called with JPEG data when the user pastes an image instead of text.
    var onPasteImage: (Data) -> Void = { _ in }

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
        textView.setAccessibilityLabel("Message")
        textView.onPasteImage = onPasteImage

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
        textView.onPasteImage = onPasteImage
        context.coordinator.parent = self
        if isFocused.wrappedValue, let window = textView.window, window.firstResponder !== textView {
            window.makeFirstResponder(textView)
        }
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

        func textDidBeginEditing(_ notification: Notification) { parent.isFocused.wrappedValue = true }
        func textDidEndEditing(_ notification: Notification) { parent.isFocused.wrappedValue = false }

        func textDidChange(_ notification: Notification) {
            guard let textView = notification.object as? NSTextView else { return }
            parent.text = textView.string
            scheduleMeasure()
        }

        // Key handling lives here (not in keyDown) so the input-method pipeline
        // runs first: during Japanese/Chinese/Korean composition, Return must
        // commit the marked text, not send the message.
        func textView(_ textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
            switch commandSelector {
            case #selector(NSResponder.insertNewline(_:)):
                if textView.hasMarkedText() { return false }
                // Shift+Return inserts a newline (chat convention).
                if NSApp.currentEvent?.modifierFlags.contains(.shift) == true { return false }
                parent.onSend()
                return true
            case #selector(NSResponder.cancelOperation(_:)):
                return parent.onEscape()
            default:
                return false
            }
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
    var onPasteImage: ((Data) -> Void)?
    var placeholderString: String = "" {
        didSet {
            if oldValue != placeholderString { needsDisplay = true }
        }
    }

    override var intrinsicContentSize: NSSize {
        NSSize(width: NSView.noIntrinsicMetric, height: NSView.noIntrinsicMetric)
    }

    // ⌘V of a screenshot or copied image attaches it instead of doing nothing
    // (this is a plain-text view — super would silently drop the image).
    override func paste(_ sender: Any?) {
        let pasteboard = NSPasteboard.general
        // Only divert when there's no text representation to paste.
        let hasText = pasteboard.string(forType: .string)?.isEmpty == false
        if !hasText,
           let onPasteImage,
           let image = NSImage(pasteboard: pasteboard),
           let tiff = image.tiffRepresentation,
           let rep = NSBitmapImageRep(data: tiff),
           let jpeg = rep.representation(using: .jpeg, properties: [.compressionFactor: 0.85]) {
            onPasteImage(jpeg)
            return
        }
        super.paste(sender)
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
