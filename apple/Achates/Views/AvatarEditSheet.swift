import SwiftUI
import PhotosUI

struct AvatarEditSheet: View {
    let agent: Agent
    let hasAvatar: Bool
    @Binding var newAvatarData: Data?
    @Binding var removeAvatar: Bool
    let resizeAvatar: (Data) -> Data?
    let onGenerate: (String, Data?) async throws -> Data

    @Environment(\.dismiss) private var dismiss
    @State private var localImageData: Data?
    @State private var localRemove = false
    @State private var photoItem: PhotosPickerItem?
    @State private var promptText = ""
    @State private var isGenerating = false
    @State private var errorMessage: String?
    @State private var showError = false
    @State private var generateTask: Task<Void, Never>?

    /// The image to display: local working copy > existing agent avatar
    private var displayImage: Data? {
        if let localImageData { return localImageData }
        if !localRemove, let data = agent.avatarData { return data }
        return nil
    }

    /// The image to use as AI reference
    private var referenceImage: Data? {
        displayImage
    }

    private var hasLocalChanges: Bool {
        localImageData != nil || localRemove
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 24) {
                    avatarPreview
                        .padding(.top, 20)

                    actionButtons

                    Divider()
                        .padding(.horizontal)

                    generateSection
                }
                .padding(.bottom, 32)
            }
            .navigationTitle("Profile Photo")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        generateTask?.cancel()
                        dismiss()
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") {
                        commitChanges()
                        dismiss()
                    }
                    .fontWeight(.semibold)
                    .disabled(!hasLocalChanges)
                }
            }
            .alert("Error", isPresented: $showError) {
                Button("OK") {}
            } message: {
                Text(errorMessage ?? "Unknown error")
            }
            .onAppear {
                // Initialize from existing state
                if let existing = newAvatarData {
                    localImageData = existing
                }
                localRemove = removeAvatar
                promptText = "A profile avatar for an AI assistant named \(agent.name.capitalized). Clean, modern, circular icon style."
            }
        }
        #if os(iOS)
        .presentationDetents([.large])
        .presentationDragIndicator(.visible)
        #endif
    }

    // MARK: - Avatar Preview

    @ViewBuilder
    private var avatarPreview: some View {
        let size: CGFloat = 160
        ZStack {
            if let data = displayImage {
                avatarImage(from: data, size: size)
            } else {
                ZStack {
                    Circle()
                        .fill(.blue.gradient)
                        .frame(width: size, height: size)
                    Text(agent.initials)
                        .font(.system(size: size * 0.35, weight: .semibold))
                        .foregroundStyle(.white)
                }
            }

            if isGenerating {
                Circle()
                    .fill(.black.opacity(0.4))
                    .frame(width: size, height: size)
                ProgressView()
                    .controlSize(.large)
                    .tint(.white)
            }
        }
    }

    @ViewBuilder
    private func avatarImage(from data: Data, size: CGFloat) -> some View {
        #if os(macOS)
        if let img = NSImage(data: data) {
            Image(nsImage: img)
                .resizable().scaledToFill()
                .frame(width: size, height: size)
                .clipShape(Circle())
        }
        #else
        if let img = UIImage(data: data) {
            Image(uiImage: img)
                .resizable().scaledToFill()
                .frame(width: size, height: size)
                .clipShape(Circle())
        }
        #endif
    }

    // MARK: - Action Buttons

    @ViewBuilder
    private var actionButtons: some View {
        HStack(spacing: 32) {
            PhotosPicker(selection: $photoItem, matching: .images) {
                VStack(spacing: 6) {
                    Image(systemName: "photo.on.rectangle")
                        .font(.title2)
                    Text("Photo Library")
                        .font(.caption)
                }
                .foregroundStyle(.blue)
            }
            .buttonStyle(.plain)
            .disabled(isGenerating)
            .onChange(of: photoItem) { _, item in
                Task {
                    guard let item else { return }
                    if let data = try? await item.loadTransferable(type: Data.self) {
                        localImageData = resizeAvatar(data)
                        localRemove = false
                    }
                }
            }

            if displayImage != nil {
                Button {
                    localImageData = nil
                    localRemove = true
                    photoItem = nil
                } label: {
                    VStack(spacing: 6) {
                        Image(systemName: "trash")
                            .font(.title2)
                        Text("Remove")
                            .font(.caption)
                    }
                    .foregroundStyle(.red)
                }
                .disabled(isGenerating)
            }
        }
    }

    // MARK: - AI Generate

    @ViewBuilder
    private var generateSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Generate with AI")
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(.secondary)
                .padding(.horizontal)

            TextEditor(text: $promptText)
                .frame(minHeight: 80, maxHeight: 120)
                .padding(8)
                .scrollContentBackground(.hidden)
                .background(
                    RoundedRectangle(cornerRadius: 10)
                        #if os(macOS)
                        .fill(Color(.controlBackgroundColor))
                        #else
                        .fill(Color(.systemGray6))
                        #endif
                )
                .padding(.horizontal)
                .disabled(isGenerating)

            Button {
                startGeneration()
            } label: {
                HStack {
                    if isGenerating {
                        ProgressView()
                            .controlSize(.small)
                            .tint(.white)
                        Text("Generating...")
                    } else {
                        Image(systemName: "sparkles")
                        Text("Generate")
                    }
                }
                .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(isGenerating || promptText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            .padding(.horizontal)
        }
    }

    // MARK: - Actions

    private func startGeneration() {
        generateTask = Task {
            isGenerating = true
            do {
                let data = try await onGenerate(promptText, referenceImage)
                localImageData = data
                localRemove = false
            } catch is CancellationError {
                // User cancelled
            } catch {
                errorMessage = error.localizedDescription
                showError = true
            }
            isGenerating = false
        }
    }

    private func commitChanges() {
        newAvatarData = localImageData
        removeAvatar = localRemove
    }
}
