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
    @State private var showDeleteConfirm = false
    @State private var isDeleting = false
    @State private var voiceRegistry: VoiceRegistry?
    @State private var previewPlayer = SpeechPreviewPlayer()
    @State private var isPreviewLoading = false
    @State private var previewError: String?

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
        .alert("Delete Agent", isPresented: $showDeleteConfirm) {
            Button("Delete", role: .destructive) {
                Task { await deleteAgent() }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Delete \(agent.displayName)? All conversations, memory, and settings for this agent will be permanently removed. This cannot be undone.")
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
        .task {
            // iOS 26 NavigationStack can re-fire .task after a pushed destination
            // pops. Guard so we don't reload from the server and stomp the user's
            // in-progress edits.
            guard config == nil else { return }
            await load()
        }
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

            Section {
                Toggle("Access shared user memory", isOn: sharedMemoryBinding)
            } footer: {
                Text("When off, this agent only sees its own private notes — useful for roleplay or in-character chat.")
            }

            Section {
                voicePicker
                HStack {
                    Text("Custom blend")
                    Spacer()
                    TextField("af_nicole(0.7)+af_bella(0.3)", text: voiceBinding)
                        .multilineTextAlignment(.trailing)
                        .foregroundStyle(.secondary)
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                }
                speechRateRow
                if config?.speechRate != nil {
                    Button("Reset rate to default") { resetSpeechRate() }
                        .font(.footnote)
                }
                playSampleRow
            } header: {
                Text("Voice")
            } footer: {
                if let err = previewError {
                    Text(err)
                        .foregroundStyle(.red)
                } else if let reg = voiceRegistry, reg.voices.isEmpty, !reg.isLoading {
                    Text("Speech is not configured on the server. Configure tools.speech in ~/.achates/config.yaml and restart to enable.")
                } else {
                    Text("Voice plays for sessions where the speaker toggle is on. Empty makes the agent silent. Rate ranges from 0.5× (slow) to 2× (fast); 1.0× is normal.")
                }
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

            Section {
                Button(role: .destructive) {
                    showDeleteConfirm = true
                } label: {
                    HStack {
                        Spacer()
                        if isDeleting {
                            ProgressView().controlSize(.small)
                        } else {
                            Text("Delete Agent")
                        }
                        Spacer()
                    }
                }
                .disabled(isSaving || isDeleting)
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

    @ViewBuilder
    private var voicePicker: some View {
        // Picker selects from the known voice list; "" = voiceless. If the
        // configured voice doesn't match (e.g. a custom blend), the picker
        // shows the explicit "Custom" sentinel and the textfield carries
        // the actual string.
        let known = voiceRegistry?.voices ?? []
        let current = config?.voice ?? ""
        let isCustom = !current.isEmpty && !known.contains(current)
        HStack {
            Text("Voice")
            Spacer()
            Picker("", selection: voicePickerBinding) {
                Text("Voiceless").tag("")
                ForEach(known, id: \.self) { v in
                    Text(v).tag(v)
                }
                if isCustom {
                    Text("Custom (\(current))").tag(current)
                }
            }
            .pickerStyle(.menu)
            .labelsHidden()
        }
    }

    private var voicePickerBinding: Binding<String> {
        Binding(
            get: { config?.voice ?? "" },
            set: { newValue in
                guard var c = config else { return }
                c.voice = newValue.isEmpty ? nil : newValue
                config = c
            }
        )
    }

    private var voiceBinding: Binding<String> {
        Binding(
            get: { config?.voice ?? "" },
            set: { newValue in
                guard var c = config else { return }
                c.voice = newValue.isEmpty ? nil : newValue
                config = c
            }
        )
    }

    @ViewBuilder
    private var speechRateRow: some View {
        // The stepper operates on a non-nil Double (Kokoro's default 1.0 stands
        // in for nil). Snapping back to exactly 1.0 reverts the model to nil so
        // we don't pin "default" into AGENT.md as a no-op `**Speech Rate:** 1`.
        HStack {
            Text("Rate")
            Spacer()
            Text(speechRateLabel)
                .foregroundStyle(.secondary)
                .monospacedDigit()
            Stepper("Rate", value: speechRateBinding, in: 0.5...2.0, step: 0.05)
                .labelsHidden()
        }
    }

    /// Human-readable label that matches the stepper value. Shows "1.0×
    /// (default)" when unset so the user understands they're at the unspecified
    /// baseline.
    private var speechRateLabel: String {
        if let r = config?.speechRate {
            return String(format: "%.2f×", r)
        }
        return "1.0× (default)"
    }

    private var speechRateBinding: Binding<Double> {
        Binding(
            get: { config?.speechRate ?? 1.0 },
            set: { newValue in
                guard var c = config else { return }
                // Snap exact 1.0 to nil — that's the "default" sentinel and
                // shouldn't be persisted as a no-op capability line.
                let rounded = (newValue * 100).rounded() / 100
                c.speechRate = abs(rounded - 1.0) < 0.001 ? nil : rounded
                config = c
            }
        )
    }

    private func resetSpeechRate() {
        guard var c = config else { return }
        c.speechRate = nil
        config = c
    }

    @ViewBuilder
    private var playSampleRow: some View {
        Button {
            Task { await playSample() }
        } label: {
            HStack {
                if isPreviewLoading {
                    ProgressView().controlSize(.small)
                    Text("Synthesizing…")
                } else if previewPlayer.isPlaying {
                    Image(systemName: "speaker.wave.2.fill")
                    Text("Playing…")
                } else {
                    Image(systemName: "play.circle")
                    Text("Play sample")
                }
                Spacer()
            }
        }
        .disabled(playSampleDisabled)
    }

    private var playSampleDisabled: Bool {
        // Need a voice to test with, and don't fire while a synth call is in
        // flight or playback is already running.
        let hasVoice = !(config?.voice ?? "").isEmpty
        return !hasVoice || isPreviewLoading || previewPlayer.isPlaying
    }

    private func playSample() async {
        guard let client = appState.client, let voice = config?.voice, !voice.isEmpty else { return }
        isPreviewLoading = true
        previewError = nil
        defer { isPreviewLoading = false }

        var params: [String: JSONValue] = ["voice": .string(voice)]
        if let rate = config?.speechRate {
            params["speed"] = .double(rate)
        }
        do {
            let payload = try await client.sendRequest(method: "speech.test", params: params)
            if let base64 = payload?["data"]?.stringValue {
                previewPlayer.play(base64Mp3: base64)
                if let err = previewPlayer.lastError {
                    previewError = err
                }
            } else {
                previewError = "Server returned no audio."
            }
        } catch {
            previewError = "Sample failed: \(error.localizedDescription)"
        }
    }

    private var sharedMemoryBinding: Binding<Bool> {
        Binding(
            get: { config?.sharedMemory ?? true },
            set: { newValue in
                guard var c = config else { return }
                c.sharedMemory = newValue
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

        // Kick off voice list refresh in the background; the picker shows
        // "Voiceless" + the agent's current voice immediately and adds the
        // server list as it lands.
        let registry = voiceRegistry ?? VoiceRegistry(appState: appState)
        voiceRegistry = registry
        await registry.loadIfStale()
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

    private func deleteAgent() async {
        isDeleting = true
        defer { isDeleting = false }
        do {
            try await appState.deleteAgent(agent)
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
            showError = true
        }
    }

}
