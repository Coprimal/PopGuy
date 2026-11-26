// ComposerView.swift
// PopGuy
//
// SwiftUI content of the composer window: the text editor and a footer with the
// source hint, "Restore draft", Cancel, Done and Done & Submit, with the image strip
// above the footer and the enlarged image preview over everything. When the terminal
// session has ended, or Compose has no target app, Done becomes "Copy & Close" and
// Done & Submit is hidden.
// The owner (the composer controller) holds the `ComposerModel` and its callbacks.

import AppKit
import Combine
import SwiftUI

/// The calling agent's commands, offered as suggestions in a CLI session.
nonisolated struct ComposerSuggestionCatalog: Equatable, Sendable {
    let agent: String
    let items: [ComposerCatalogItem]
}

/// State and callbacks of one composer window. The buttons and the editor's
/// keyboard shortcuts call the same callbacks.
@MainActor
final class ComposerModel: ObservableObject {
    @Published var text: String
    /// Name of the app the text is edited for; nil for the Compose mode.
    @Published var sourceAppName: String?
    /// The CLI went away: the text can only be copied.
    @Published var sessionEnded = false
    /// Compose mode: name of the app Done pastes into.
    @Published var pasteTargetName: String?
    /// Compose mode without a target app: the text can only be copied.
    @Published var noTarget = false
    /// An older draft exists; shows "Restore draft".
    @Published var draftAvailable = false
    /// The CLI session's command catalog, once the client sent it; nil otherwise.
    @Published var suggestionCatalog: ComposerSuggestionCatalog?

    var onDone: () -> Void = {}
    var onDoneAndSubmit: () -> Void = {}
    var onCancel: () -> Void = {}
    /// Called after the text was copied to the general pasteboard.
    var onCopyAndClose: () -> Void = {}
    var onRestoreDraft: () -> Void = {}

    /// Direct, undoable access to the editor (image strip selection, removal, lock).
    let editor: ComposerEditorHandle
    /// The image strip and its enlarged preview; follows `text`.
    let images: ComposerImageStripModel

    init(text: String = "", sourceAppName: String? = nil) {
        self.text = text
        self.sourceAppName = sourceAppName
        let editor = ComposerEditorHandle()
        self.editor = editor
        self.images = ComposerImageStripModel(editor: editor)
        images.bind(text: $text.eraseToAnyPublisher())
    }

    /// The text as it leaves the composer: pasted image badges expanded to their mentions.
    var expandedText: String {
        images.expand(text)
    }

    /// Done copies the text instead of delivering it.
    var isCopyOnly: Bool {
        sessionEnded || noTarget
    }

    /// The text cannot be sent back: it (with badges expanded) exceeds the protocol's size cap.
    var isTooLarge: Bool {
        expandedText.utf8.count > ComposerMessage.maxTextBytes
    }
}

struct ComposerView: View {
    @ObservedObject var model: ComposerModel
    /// Observed directly: changes inside the nested model do not re-render `model`'s views.
    @ObservedObject private var images: ComposerImageStripModel

    init(model: ComposerModel) {
        _model = ObservedObject(wrappedValue: model)
        _images = ObservedObject(wrappedValue: model.images)
    }

    var body: some View {
        let images = self.images
        ZStack {
            VStack(spacing: 0) {
                ComposerTextView(
                    text: $model.text,
                    onDone: done,
                    onDoneAndSubmit: doneAndSubmit,
                    onCancel: { model.onCancel() },
                    suggestionCatalog: model.suggestionCatalog,
                    editor: model.editor,
                    onEscapeOverride: {
                        guard images.isPreviewOpen else { return false }
                        images.closePreview()
                        return true
                    },
                    collapsePastedImages: { images.collapse($0) },
                    expandPastedImages: { images.expand($0) },
                    pastedImagePath: { images.pastedImages[$0] }
                )
                // A smaller editor while the strip shows, so the window's minimum size does not clip.
                .frame(minWidth: 320, maxWidth: .infinity, minHeight: images.items.isEmpty ? 140 : 80,
                       maxHeight: .infinity)
                Divider()
                if !images.items.isEmpty {
                    ComposerImageStrip(model: images)
                        .fixedSize(horizontal: false, vertical: true)
                        // The strip insets its tiles by 10; 2 more aligns them with the footer's 12.
                        .padding(.horizontal, 2)
                        .padding(.top, 4)
                }
                footer
            }
            if images.preview != nil {
                ComposerImagePreview(model: images)
            }
        }
    }

    // MARK: Footer

    private var footer: some View {
        VStack(alignment: .leading, spacing: 8) {
            if model.sessionEnded {
                notice("The terminal session ended. Copy the text to keep it.")
            }
            if model.isTooLarge {
                notice("Text is too large (max 1 MB).")
            }
            // One row when it fits; in a narrow window the actions move to a second row.
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 8) {
                    leadingItems
                    Spacer(minLength: 0)
                    actionButtons
                }
                VStack(alignment: .leading, spacing: 8) {
                    HStack(spacing: 8) { leadingItems }
                    HStack(spacing: 8) {
                        Spacer(minLength: 0)
                        actionButtons
                    }
                }
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
    }

    @ViewBuilder
    private var leadingItems: some View {
        Text(sourceHint)
            .font(.callout)
            .foregroundStyle(.secondary)
            .lineLimit(1)
            .truncationMode(.tail)
        if model.draftAvailable {
            footerButton("Restore draft", tooltip: "Restore the last saved draft") {
                model.onRestoreDraft()
            }
        }
    }

    @ViewBuilder
    private var actionButtons: some View {
        footerButton("Cancel", tooltip: "Cancel (Esc)") { model.onCancel() }
        if model.isCopyOnly {
            footerButton("Copy & Close", tooltip: "Copy & Close (\u{2318}\u{21A9})", prominent: true, action: done)
                .disabled(model.text.isEmpty)
        } else {
            footerButton("Done", tooltip: "Done (\u{2318}\u{21A9})", prominent: true, action: done)
                .disabled(model.isTooLarge)
            footerButton("Done & Submit", tooltip: "Done & Submit (\u{2318}\u{21E7}\u{21A9})", action: doneAndSubmit)
                .disabled(model.isTooLarge)
        }
    }

    private var sourceHint: String {
        if let name = model.sourceAppName, !name.isEmpty {
            return "Editing for \(name)"
        }
        if model.noTarget {
            return "Compose (no target: Copy & Close copies the text)"
        }
        if let name = model.pasteTargetName, !name.isEmpty {
            return "Pasting into \(name)"
        }
        return "Compose"
    }

    private func notice(_ message: String) -> some View {
        Label(message, systemImage: "exclamationmark.triangle.fill")
            .font(.callout)
            .foregroundStyle(.secondary)
    }

    private func footerButton(_ title: String, tooltip: String, prominent: Bool = false,
                              action: @escaping () -> Void) -> some View {
        Button(title, action: action)
            .buttonStyle(BottomBarButtonStyle(height: 28, prominent: prominent, horizontalPadding: 12))
            .fixedSize()
            .hoverTooltip(tooltip)
    }

    // MARK: Actions (buttons and keyboard)

    /// Done, or Copy & Close once the session ended or without a Compose target.
    /// Ignored while the text is too large.
    private func done() {
        // The preview closes first (it also unlocks the editor).
        images.closePreview()
        if model.isCopyOnly {
            guard !model.text.isEmpty else { return }
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            pasteboard.setString(model.expandedText, forType: .string)
            model.onCopyAndClose()
            return
        }
        guard !model.isTooLarge else {
            NSSound.beep()
            return
        }
        model.onDone()
    }

    private func doneAndSubmit() {
        images.closePreview()
        guard !model.isCopyOnly, !model.isTooLarge else {
            NSSound.beep()
            return
        }
        model.onDoneAndSubmit()
    }
}
