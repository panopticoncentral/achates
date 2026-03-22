import SwiftUI

struct AgentEditView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss
    let agent: Agent

    @State private var config: AgentEditModel?
    @State private var original: AgentEditModel?
    @State private var isLoading = true
    @State private var isSaving = false
    @State private var errorMessage: String?
    @State private var showError = false
    @State private var showAvatarSheet = false

    private var hasChanges: Bool {
        guard let config, let original else { return false }
        return config != original
    }

    var body: some View {
        Group {
            if isLoading {
                ProgressView("Loading...")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let _ = config {
                formContent
            }
        }
        .navigationTitle(agent.name.capitalized)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .confirmationAction) {
                Button {
                    Task { await save() }
                } label: {
                    if isSaving {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("Save")
                    }
                }
                .disabled(!hasChanges || isSaving)
            }
            ToolbarItem(placement: .cancellationAction) {
                Button("Cancel") { dismiss() }
            }
        }
        .alert("Error", isPresented: $showError) {
            Button("OK") {}
        } message: {
            Text(errorMessage ?? "Unknown error")
        }
        .sheet(isPresented: $showAvatarSheet) {
            if config != nil {
                AvatarEditSheet(
                    agent: agent,
                    hasAvatar: config?.hasAvatar ?? false,
                    newAvatarData: binding(\.newAvatarData),
                    removeAvatar: binding(\.removeAvatar),
                    resizeAvatar: resizeAvatar,
                    onGenerate: { prompt, refImage in
                        try await appState.generateAvatar(agent, prompt: prompt, referenceImage: refImage)
                    }
                )
            }
        }
        .task { await load() }
    }

    @ViewBuilder
    private var formContent: some View {
        Form {
            Section("Identity") {
                HStack {
                    Spacer()
                    Button { showAvatarSheet = true } label: {
                        avatarPreview
                    }
                    .buttonStyle(.plain)
                    Spacer()
                }
                .listRowBackground(Color.clear)

                HStack {
                    Text("Description")
                    Spacer()
                    TextField("Description", text: binding(\.description))
                        .multilineTextAlignment(.trailing)
                        .foregroundStyle(.secondary)
                }

                NavigationLink {
                    PromptEditView(prompt: binding(\.prompt))
                } label: {
                    HStack {
                        Text("System Prompt")
                        Spacer()
                        Text(config?.prompt.isEmpty == true ? "None" : "\(config?.prompt.count ?? 0) chars")
                            .foregroundStyle(.secondary)
                    }
                }
            }

            Section("Model") {
                NavigationLink {
                    ModelPickerView(
                        selectedModel: binding(\.model),
                        agentModels: config?.agentModels ?? []
                    )
                } label: {
                    HStack {
                        Text("Model")
                        Spacer()
                        Text(shortModelName(config?.model ?? ""))
                            .foregroundStyle(.secondary)
                    }
                }

                reasoningEffortPicker

                HStack {
                    Text("Temperature")
                    Spacer()
                    TextField("Default", text: temperatureBinding)
                        .multilineTextAlignment(.trailing)
                        .foregroundStyle(.secondary)
                        .frame(width: 80)
                        #if os(iOS)
                        .keyboardType(.decimalPad)
                        #endif
                }

                HStack {
                    Text("Max Tokens")
                    Spacer()
                    TextField("Default", text: maxTokensBinding)
                        .multilineTextAlignment(.trailing)
                        .foregroundStyle(.secondary)
                        .frame(width: 80)
                        #if os(iOS)
                        .keyboardType(.numberPad)
                        #endif
                }
            }

            Section("Tools") {
                NavigationLink {
                    ToolsEditView(tools: binding(\.tools))
                } label: {
                    HStack {
                        Text("Tools")
                        Spacer()
                        Text("\(config?.tools.count ?? 0) enabled")
                            .foregroundStyle(.secondary)
                    }
                }
            }

            if config?.tools.contains("chat") == true {
                Section("Advanced") {
                    NavigationLink {
                        AllowedChatsEditView(
                            allowedChats: binding(\.allowedChats),
                            allAgents: appState.agents.filter { $0.id != agent.id }
                        )
                    } label: {
                        HStack {
                            Text("Allowed Chats")
                            Spacer()
                            Text(config?.allowedChats.isEmpty == true ? "All" : "\(config?.allowedChats.count ?? 0) agents")
                                .foregroundStyle(.secondary)
                        }
                    }
                }
            }
        }
        #if os(iOS)
        .scrollDismissesKeyboard(.interactively)
        #endif
    }

    @ViewBuilder
    private var reasoningEffortPicker: some View {
        HStack {
            Text("Reasoning")
            Spacer()
            Picker("", selection: reasoningBinding) {
                Text("Low").tag("low")
                Text("Med").tag("medium")
                Text("High").tag("high")
            }
            .pickerStyle(.segmented)
            .frame(width: 180)
        }
    }

    private var reasoningBinding: Binding<String> {
        Binding(
            get: { config?.reasoningEffort ?? "medium" },
            set: { config?.reasoningEffort = $0 }
        )
    }

    private var temperatureBinding: Binding<String> {
        Binding(
            get: { config?.temperature.map { String($0) } ?? "" },
            set: {
                if $0.isEmpty { config?.temperature = nil }
                else if let v = Double($0) { config?.temperature = v }
            }
        )
    }

    private var maxTokensBinding: Binding<String> {
        Binding(
            get: { config?.maxTokens.map { String($0) } ?? "" },
            set: {
                if $0.isEmpty { config?.maxTokens = nil }
                else if let v = Int($0) { config?.maxTokens = v }
            }
        )
    }

    private func binding<T>(_ keyPath: WritableKeyPath<AgentEditModel, T>) -> Binding<T> {
        Binding(
            get: { config![keyPath: keyPath] },
            set: { config![keyPath: keyPath] = $0 }
        )
    }

    @ViewBuilder
    private var avatarPreview: some View {
        let size: CGFloat = 80
        if let data = config?.newAvatarData {
            #if os(macOS)
            if let img = NSImage(data: data) {
                Image(nsImage: img)
                    .resizable().scaledToFill()
                    .frame(width: size, height: size).clipShape(Circle())
            }
            #else
            if let img = UIImage(data: data) {
                Image(uiImage: img)
                    .resizable().scaledToFill()
                    .frame(width: size, height: size).clipShape(Circle())
            }
            #endif
        } else if config?.hasAvatar == true, !config!.removeAvatar {
            AgentAvatar(agent: agent, size: size)
        } else {
            ZStack {
                Circle()
                    .fill(.blue.gradient)
                    .frame(width: size, height: size)
                Text(agent.initials)
                    .font(.system(size: size * 0.4, weight: .semibold))
                    .foregroundStyle(.white)
            }
            .overlay(alignment: .bottom) {
                Text("Edit")
                    .font(.caption2)
                    .foregroundStyle(.white)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 2)
                    .background(.black.opacity(0.5), in: Capsule())
                    .offset(y: -4)
            }
        }
    }

    private func resizeAvatar(_ data: Data) -> Data? {
        #if os(macOS)
        guard let image = NSImage(data: data) else { return nil }
        let maxSize: CGFloat = 256
        let size = image.size
        let scale = min(maxSize / size.width, maxSize / size.height, 1.0)
        let newSize = NSSize(width: size.width * scale, height: size.height * scale)
        let newImage = NSImage(size: newSize)
        newImage.lockFocus()
        image.draw(in: NSRect(origin: .zero, size: newSize))
        newImage.unlockFocus()
        guard let tiff = newImage.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .jpeg, properties: [.compressionFactor: 0.8])
        #else
        guard let image = UIImage(data: data) else { return nil }
        let maxSize: CGFloat = 256
        let size = image.size
        let scale = min(maxSize / size.width, maxSize / size.height, 1.0)
        let newSize = CGSize(width: size.width * scale, height: size.height * scale)
        let renderer = UIGraphicsImageRenderer(size: newSize)
        let resized = renderer.image { _ in
            image.draw(in: CGRect(origin: .zero, size: newSize))
        }
        return resized.jpegData(compressionQuality: 0.8)
        #endif
    }

    private func shortModelName(_ id: String) -> String {
        if let slash = id.lastIndex(of: "/") {
            return String(id[id.index(after: slash)...])
        }
        return id
    }

    private func load() async {
        do {
            let loaded = try await appState.loadAgentConfig(agent)
            config = loaded
            original = loaded
            isLoading = false
        } catch {
            errorMessage = error.localizedDescription
            showError = true
        }
    }

    private func save() async {
        guard let config else { return }
        isSaving = true
        do {
            try await appState.saveAgentConfig(agent, config: config)
            original = config
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
            showError = true
        }
        isSaving = false
    }

}
