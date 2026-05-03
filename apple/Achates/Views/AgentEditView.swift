import SwiftUI

struct AgentEditView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss
    let agent: Agent

    @State private var config: AgentEditModel?
    @State private var original: AgentEditModel?
    @State private var isLoading = true
    @State private var isSaving = false
    @State private var saveEnabled = false
    @State private var errorMessage: String?
    @State private var showError = false
    @State private var showAvatarSheet = false
    @State private var availableTools: [ToolInfo] = []
    @State private var showToolsEditor = false

    var body: some View {
        Group {
            if isLoading {
                ProgressView("Loading...")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let _ = config {
                formContent
            }
        }
        .navigationTitle(config?.displayName ?? agent.displayName)
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
                .disabled(!saveEnabled || isSaving)
            }
            ToolbarItem(placement: .cancellationAction) {
                Button("Cancel") { dismiss() }
            }
        }
        .onChange(of: config) { _, _ in
            if let config, let original {
                saveEnabled = config != original
            } else {
                saveEnabled = false
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
                    Text("Name")
                    Spacer()
                    TextField("", text: binding(\.displayName))
                        .multilineTextAlignment(.trailing)
                        .foregroundStyle(.secondary)
                }

                HStack {
                    Text("Description")
                    Spacer()
                    TextField("", text: binding(\.description))
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

            Section("Generation") {
                NavigationLink {
                    ModelBrowseView(
                        selectedModel: binding(\.model),
                        title: "Model",
                        defaultModel: config?.defaultModel
                    )
                } label: {
                    modelRow(label: "Model", current: config?.model, fallback: config?.defaultModel)
                }

                if config?.tools.contains("think") == true {
                    NavigationLink {
                        ModelBrowseView(
                            selectedModel: binding(\.thinkingModel),
                            title: "Thinking Model",
                            defaultModel: config?.defaultThinkingModel
                        )
                    } label: {
                        modelRow(label: "Thinking Model", current: config?.thinkingModel, fallback: config?.defaultThinkingModel)
                    }
                }

                reasoningEffortPicker

                HStack {
                    Text("Temperature")
                    Spacer()
                    TextField("Default", text: temperatureBinding)
                        .multilineTextAlignment(.trailing)
                        .foregroundStyle(.secondary)
                        .fixedSize()
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
                        .fixedSize()
                        #if os(iOS)
                        .keyboardType(.numberPad)
                        #endif
                }
            }

            Section {
                Toggle("Dreamtime", isOn: dreamtimeEnabledBinding)
                if config?.dreamtime != nil {
                    DatePicker(
                        "Time",
                        selection: dreamtimeBinding,
                        displayedComponents: .hourAndMinute
                    )
                }
            } footer: {
                Text("Each night at this time, the agent reviews recent sessions and updates its memory.")
            }

            Section("Tools") {
                DisclosureGroup(isExpanded: $showToolsEditor) {
                    ForEach(availableTools) { tool in
                        Toggle(tool.label, isOn: toolToggle(tool.name))
                    }
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
        #else
        .formStyle(.grouped)
        #endif
    }

    @ViewBuilder
    private func modelRow(label: String, current: String?, fallback: String?) -> some View {
        HStack {
            Text(label)
            Spacer()
            if let current {
                Text(shortModelName(current))
                    .foregroundStyle(.secondary)
            } else if let fallback {
                Text("Default (\(shortModelName(fallback)))")
                    .foregroundStyle(.secondary)
            } else {
                Text("None")
                    .foregroundStyle(.secondary)
            }
        }
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
            set: { newValue in
                guard var c = config else { return }
                c.reasoningEffort = newValue
                config = c
            }
        )
    }

    private var temperatureBinding: Binding<String> {
        Binding(
            get: { config?.temperature.map { String($0) } ?? "" },
            set: { newValue in
                guard var c = config else { return }
                if newValue.isEmpty { c.temperature = nil }
                else if let v = Double(newValue) { c.temperature = v }
                config = c
            }
        )
    }

    private var maxTokensBinding: Binding<String> {
        Binding(
            get: { config?.maxTokens.map { String($0) } ?? "" },
            set: { newValue in
                guard var c = config else { return }
                if newValue.isEmpty { c.maxTokens = nil }
                else if let v = Int(newValue) { c.maxTokens = v }
                config = c
            }
        )
    }

    private var dreamtimeEnabledBinding: Binding<Bool> {
        Binding(
            get: { config?.dreamtime != nil },
            set: { enabled in
                guard var c = config else { return }
                if enabled {
                    var comps = DateComponents()
                    comps.hour = 3
                    comps.minute = 0
                    c.dreamtime = Calendar.current.date(from: comps) ?? Date()
                } else {
                    c.dreamtime = nil
                }
                config = c
            }
        )
    }

    private var dreamtimeBinding: Binding<Date> {
        Binding(
            get: { config?.dreamtime ?? Date() },
            set: { newValue in
                guard var c = config else { return }
                c.dreamtime = newValue
                config = c
            }
        )
    }

    private func toolToggle(_ tool: String) -> Binding<Bool> {
        Binding(
            get: { config?.tools.contains(tool) ?? false },
            set: { enabled in
                guard var c = config else { return }
                if enabled {
                    if !c.tools.contains(tool) { c.tools.append(tool) }
                } else {
                    c.tools.removeAll { $0 == tool }
                }
                config = c
            }
        )
    }

    private func binding<T>(_ keyPath: WritableKeyPath<AgentEditModel, T>) -> Binding<T> {
        Binding(
            get: { config![keyPath: keyPath] },
            set: { newValue in
                var updated = config!
                updated[keyPath: keyPath] = newValue
                config = updated
            }
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

    private func load() async {
        do {
            async let loadedConfig = appState.loadAgentConfig(agent)
            async let loadedTools = appState.loadAvailableTools()
            config = try await loadedConfig
            original = config
            availableTools = (try? await loadedTools) ?? []
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
            try await appState.saveAgentConfig(agent, config: config, original: original!)
            original = config
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
            showError = true
        }
        isSaving = false
    }

}
