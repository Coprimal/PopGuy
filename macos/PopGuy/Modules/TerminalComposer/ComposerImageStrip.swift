// ComposerImageStrip.swift
// PopGuy
//
// The Terminal Composer's image strip: one tile per image mention ("@/abs/path.png")
// whose file exists, plus a chip per CLI "[Image #N]" placeholder when the session's
// CLI uses them. A click selects the mention in the editor (and, for a file, opens
// the enlarged preview, see ComposerImagePreview); the hover x removes it from the
// text as one undoable edit.
//
// ComposerImageStripModel follows the editor text (Combine, debounced), parses it
// with ComposerAttachments, checks the files off the main actor and loads thumbnails
// through ComposerThumbnailLoader. Edits and selection go through ComposerEditorHandle
// so the text view keeps its undo history.

import AppKit
import Combine
import SwiftUI
import os

@MainActor
final class ComposerImageStripModel: ObservableObject {
    /// The enlarged preview: the placeholder until `image` is decoded or `failed`.
    struct PreviewState: Equatable {
        let path: String
        var image: CGImage?
        var failed: Bool
        var showsSpinner: Bool

        static func == (lhs: PreviewState, rhs: PreviewState) -> Bool {
            lhs.path == rhs.path && lhs.image === rhs.image
                && lhs.failed == rhs.failed && lhs.showsSpinner == rhs.showsSpinner
        }
    }

    /// One strip entry with an id that is stable per occurrence (token + its index
    /// among equal tokens).
    struct Entry: Identifiable {
        let id: String
        let item: ComposerAttachments.Item
    }

    @Published private(set) var items: [ComposerAttachments.Item] = []
    /// Thumbnails by path.
    @Published var thumbnails: [String: CGImage] = [:]
    /// Paths whose thumbnail could not be decoded.
    @Published var failedPaths: Set<String> = []
    @Published var preview: PreviewState?

    /// Shows the CLI's "[Image #N]" chips; changing it re-filters the current text.
    var showsClaudeImageTokens = false {
        didSet {
            guard showsClaudeImageTokens != oldValue else { return }
            applyFilter()
        }
    }

    let editor: ComposerEditorHandle

    var isPreviewOpen: Bool { preview != nil }

    var entries: [Entry] {
        var seen: [String: Int] = [:]
        return items.map { item in
            let occurrence = seen[item.token, default: 0]
            seen[item.token] = occurrence + 1
            return Entry(id: "\(occurrence)#\(item.token)", item: item)
        }
    }

    private static let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "PopGuy", category: "composer")
    private static let previewMaxPixel = 2048
    private static let spinnerDelay: Duration = .milliseconds(150)

    private var textCancellable: AnyCancellable?
    private var parsedText: String?
    private var parsedItems: [ComposerAttachments.Item] = []
    /// Paths known to exist; filled off the main actor for the current text.
    private var existingPaths: Set<String> = []
    private var textGeneration = 0
    private var loadTask: Task<Void, Never>?

    private var previewGeneration = 0
    private var previewTask: Task<Void, Never>?
    private var spinnerTask: Task<Void, Never>?
    #if DEBUG
    private var previewClickTime: ContinuousClock.Instant?
    #endif

    init(editor: ComposerEditorHandle) {
        self.editor = editor
    }

    // MARK: - Text

    /// Follows the editor text: duplicates dropped, 150 ms debounce on the main run loop.
    func bind(text: AnyPublisher<String, Never>) {
        textCancellable = text
            .removeDuplicates()
            .debounce(for: .milliseconds(150), scheduler: RunLoop.main)
            .sink { [weak self] text in
                MainActor.assumeIsolated {
                    self?.update(text: text)
                }
            }
    }

    /// Parses now instead of after the debounce (a reopen must not show the last session's tiles).
    func update(text: String) {
        guard text != parsedText else { return }
        parsedText = text
        parsedItems = ComposerAttachments.parse(text)
        textGeneration += 1
        let generation = textGeneration

        let paths = Set(parsedItems.compactMap(Self.path(of:)))
        // Known paths show at once; new ones appear once their file is found.
        existingPaths.formIntersection(paths)
        failedPaths.formIntersection(paths)
        thumbnails = thumbnails.filter { paths.contains($0.key) }
        applyFilter()

        loadTask?.cancel()
        guard !paths.isEmpty else { return }
        loadTask = Task { [weak self] in
            let existing = await Task.detached(priority: .userInitiated) {
                Self.existingFiles(among: paths)
            }.value
            guard let self, !Task.isCancelled, generation == self.textGeneration else { return }
            self.existingPaths = existing
            self.applyFilter()
            await self.loadThumbnails(generation: generation)
        }
    }

    private func applyFilter() {
        items = parsedItems.filter { item in
            switch item.kind {
            case .file(let path): existingPaths.contains(path)
            case .cliImage: showsClaudeImageTokens
            }
        }
    }

    /// Loads the thumbnails not cached or failed yet, in strip order.
    private func loadThumbnails(generation: Int) async {
        var requested: Set<String> = []
        for item in items {
            guard let path = Self.path(of: item), !requested.contains(path),
                  thumbnails[path] == nil, !failedPaths.contains(path) else { continue }
            requested.insert(path)
            let image = await ComposerThumbnailLoader.shared.thumbnail(path: path)
            guard !Task.isCancelled, generation == textGeneration else { return }
            if let image {
                thumbnails[path] = image
            } else {
                failedPaths.insert(path)
            }
        }
    }

    private nonisolated static func existingFiles(among paths: Set<String>) -> Set<String> {
        // Keyed by the mention's own path; only the resolved one is checked.
        paths.filter { path in
            guard let resolved = ComposerThumbnailLoader.resolvedLocalPath(path) else { return false }
            let url = URL(fileURLWithPath: resolved, isDirectory: false)
            return (try? url.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true
        }
    }

    private static func path(of item: ComposerAttachments.Item) -> String? {
        if case .file(let path) = item.kind { return path }
        return nil
    }

    // MARK: - Actions

    /// Selects the item in the editor; a file item also opens the enlarged preview
    /// (placeholder now, image once decoded) and locks the editor.
    func activate(_ item: ComposerAttachments.Item) {
        #if DEBUG
        let clickTime = ContinuousClock.now
        #endif
        let text = editor.currentText
        guard let match = currentMatch(for: item, in: text) else { return }
        editor.select(match.range)
        guard case .file(let path) = match.kind else { return }

        editor.setLocked(true)
        previewGeneration += 1
        let generation = previewGeneration
        preview = PreviewState(path: path, image: nil, failed: false, showsSpinner: false)
        #if DEBUG
        previewClickTime = clickTime
        logPreviewTiming("placeholder")
        #endif

        spinnerTask?.cancel()
        spinnerTask = Task { [weak self] in
            try? await Task.sleep(for: Self.spinnerDelay)
            guard let self, !Task.isCancelled, generation == self.previewGeneration,
                  var state = self.preview, state.image == nil, !state.failed else { return }
            state.showsSpinner = true
            self.preview = state
            #if DEBUG
            self.logPreviewTiming("spinner")
            #endif
        }

        previewTask?.cancel()
        previewTask = Task { [weak self] in
            let image = await ComposerThumbnailLoader.shared.preview(path: path, maxPixel: Self.previewMaxPixel)
            guard let self, !Task.isCancelled, generation == self.previewGeneration,
                  var state = self.preview, state.path == path else { return }
            state.image = image
            state.failed = image == nil
            state.showsSpinner = false
            self.preview = state
            self.spinnerTask?.cancel()
            #if DEBUG
            self.logPreviewTiming(image == nil ? "failed" : "image")
            #endif
        }
    }

    /// Removes the item (and one adjacent space) from the text as one undoable edit.
    func remove(_ item: ComposerAttachments.Item) {
        let text = editor.currentText
        guard let match = currentMatch(for: item, in: text) else { return }
        editor.remove(ComposerAttachments.removalRange(for: match.range, in: text))
        // The edit can re-open the command suggestions; a removal never wants them.
        editor.textView?.suggestions.hide()
        // The tile goes at once instead of after the debounce.
        update(text: editor.currentText)
    }

    func closePreview() {
        guard preview != nil else { return }
        preview = nil
        previewGeneration += 1
        previewTask?.cancel()
        spinnerTask?.cancel()
        #if DEBUG
        previewClickTime = nil
        #endif
        editor.setLocked(false)
        editor.refreshOwnSelection()
        if let textView = editor.textView {
            textView.window?.makeFirstResponder(textView)
        }
    }

    /// Called by the preview overlay on its first frame (timing log in Debug builds).
    func previewDidAppear() {
        #if DEBUG
        logPreviewTiming("overlayShown")
        #endif
    }

    /// The item in `text` with the same token and the same index among equal tokens
    /// as `item` has in the strip, or nil.
    private func currentMatch(for item: ComposerAttachments.Item, in text: String) -> ComposerAttachments.Item? {
        guard let index = items.firstIndex(of: item) else { return nil }
        let occurrence = items[..<index].filter { $0.token == item.token }.count
        let candidates = ComposerAttachments.parse(text).filter { $0.token == item.token }
        return occurrence < candidates.count ? candidates[occurrence] : nil
    }

    #if DEBUG
    private func logPreviewTiming(_ stage: StaticString) {
        guard let previewClickTime else { return }
        let elapsed = ContinuousClock.now - previewClickTime
        let ms = Double(elapsed.components.seconds) * 1000 + Double(elapsed.components.attoseconds) / 1e15
        let stageName = "\(stage)"
        Self.log.debug("imagePreview t+\(ms, format: .fixed(precision: 1), privacy: .public)ms \(stageName, privacy: .public)")
    }
    #endif
}

// MARK: - Views

struct ComposerImageStrip: View {
    @ObservedObject var model: ComposerImageStripModel

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(model.entries) { entry in
                    ComposerImageTile(model: model, item: entry.item)
                }
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 6)
        }
    }
}

private struct ComposerImageTile: View {
    @ObservedObject var model: ComposerImageStripModel
    let item: ComposerAttachments.Item

    @State private var isHovered = false

    private static let size: CGFloat = 48
    private static let cornerRadius: CGFloat = 6

    var body: some View {
        Button {
            model.activate(item)
        } label: {
            tileContent
                .frame(height: Self.size)
                .clipShape(RoundedRectangle(cornerRadius: Self.cornerRadius, style: .continuous))
                .overlay(
                    RoundedRectangle(cornerRadius: Self.cornerRadius, style: .continuous)
                        .strokeBorder(Color.primary.opacity(0.12), lineWidth: 1)
                )
                .contentShape(RoundedRectangle(cornerRadius: Self.cornerRadius, style: .continuous))
        }
        .buttonStyle(.plain)
        .hoverTooltip(tooltip)
        .accessibilityLabel(accessibilityName)
        .overlay(alignment: .topTrailing) {
            if isHovered {
                removeButton
            }
        }
        .onHover { isHovered = $0 }
    }

    @ViewBuilder
    private var tileContent: some View {
        switch item.kind {
        case .file(let path):
            ZStack {
                RoundedRectangle(cornerRadius: Self.cornerRadius, style: .continuous)
                    .fill(.quaternary)
                if let image = model.thumbnails[path] {
                    Image(decorative: image, scale: 2)
                        .resizable()
                        .aspectRatio(contentMode: .fill)
                        .frame(width: Self.size, height: Self.size)
                } else {
                    Image(systemName: "photo")
                        .foregroundStyle(.secondary)
                }
            }
            .frame(width: Self.size, height: Self.size)
        case .cliImage(let number):
            HStack(spacing: 6) {
                Image(systemName: "photo")
                Text("Image #\(number)")
                    .lineLimit(1)
            }
            .foregroundStyle(.secondary)
            .padding(.horizontal, 10)
            .frame(minWidth: Self.size, minHeight: Self.size)
            .background(
                RoundedRectangle(cornerRadius: Self.cornerRadius, style: .continuous)
                    .fill(.quaternary)
            )
        }
    }

    private var removeButton: some View {
        Button {
            model.remove(item)
        } label: {
            Image(systemName: "xmark")
                .font(.system(size: 8, weight: .bold))
                .foregroundStyle(.white)
                .frame(width: 16, height: 16)
                .background(Circle().fill(Color.black.opacity(0.65)))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .padding(3)
        .hoverTooltip("Remove from the text")
        .accessibilityLabel("Remove \(displayName) from the text")
    }

    private var displayName: String {
        switch item.kind {
        case .file(let path): (path as NSString).lastPathComponent
        case .cliImage(let number): "Image #\(number)"
        }
    }

    private var tooltip: String {
        switch item.kind {
        case .file(let path): model.failedPaths.contains(path) ? "Can't preview" : displayName
        case .cliImage: "\(displayName), attached in the terminal"
        }
    }

    private var accessibilityName: String {
        switch item.kind {
        case .file: "Image \(displayName)"
        case .cliImage: "\(displayName), attached in the terminal"
        }
    }
}
