// ComposerTextView.swift
// PopGuy
//
// The composer's plain-text editor: an AppKit text view in a scroll view, wrapped
// for SwiftUI. Soft wrap to the width, undo, find bar, plain text only (paste drops
// formatting), no smart quotes/dashes or autocorrect (prompt and code text).
//
// Keys: Cmd+Return → onDone, Cmd+Shift+Return → onDoneAndSubmit, Esc → onCancel
// (unless onEscapeOverride consumes it first, e.g. to close an image preview).
// In a CLI session with a command catalog, typing the agent's trigger shows the
// suggestion list (CommandSuggestionPopup): while it is shown, Up/Down move its
// selection, Return and Tab accept, and Esc closes only the list.
// Return and Backspace continue and end Markdown lists, Tab and Shift+Tab indent and
// outdent list items (ListContinuation). Pasting an image inserts an "@path" mention
// (ComposerImageStore), or a "[Pasted image #N]" badge for an image in PopGuy's image
// store (ComposerPastedImages); hovering a known badge shows its path in the custom
// hover tooltip (HoverTooltipPanelPresenter), never a system tooltip. Copy, cut and
// drag write the selection with its badges expanded; pasted or dropped text gets its
// store mentions collapsed to badges again.
// Standard edit shortcuts (cut/copy/paste/undo/redo/select all/find) are handled
// in the view itself, so they work whether or not the app's main menu has an Edit menu.
// ComposerEditorHandle lets SwiftUI select or remove a range and lock the editor
// through the text view itself, so undo history survives (setting the bound text
// would replace the whole string and wipe it).
//
// Sizing: the caller gives it an explicit frame, e.g.
// `.frame(minWidth:, maxWidth: .infinity, minHeight:, maxHeight: .infinity)`.

import AppKit
import SwiftUI

struct ComposerTextView: NSViewRepresentable {
    @Binding var text: String
    var onDone: () -> Void = {}
    var onDoneAndSubmit: () -> Void = {}
    var onCancel: () -> Void = {}
    /// The CLI session's command catalog; nil shows no suggestions.
    var suggestionCatalog: ComposerSuggestionCatalog?
    /// Gives the caller direct, undoable access to the text view.
    var editor: ComposerEditorHandle? = nil
    /// Checked first on Esc; returning true consumes it (nothing else happens).
    var onEscapeOverride: (() -> Bool)? = nil
    /// Turns the mentions of a paste into pasted image badges where they apply.
    var collapsePastedImages: ((String) -> String)? = nil
    /// Turns the pasted image badges of copied text back into full mentions.
    var expandPastedImages: ((String) -> String)? = nil
    /// The path of a pasted image badge number, or nil when the session has none.
    var pastedImagePath: ((Int) -> String?)? = nil

    static let baseFont = NSFont.systemFont(ofSize: NSFont.systemFontSize + 1)

    func makeCoordinator() -> Coordinator {
        Coordinator(parent: self)
    }

    func makeNSView(context: Context) -> NSScrollView {
        let scrollView = Self.makeScrollView(text: text, coordinator: context.coordinator)
        if let textView = scrollView.documentView as? ComposerNSTextView {
            apply(callbacksTo: textView)
        }
        return scrollView
    }

    /// The scroll view and its text view, wired to `coordinator` (text storage and text view delegate).
    static func makeScrollView(text: String, coordinator: Coordinator) -> NSScrollView {
        let scrollView = NSScrollView()
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = false
        scrollView.autohidesScrollers = true
        scrollView.borderType = .noBorder

        // TextKit 1: reliable find-bar highlighting and attribute-based styling.
        let textView = ComposerNSTextView(usingTextLayoutManager: false)
        textView.isRichText = false
        textView.importsGraphics = false
        textView.allowsUndo = true
        textView.isAutomaticQuoteSubstitutionEnabled = false
        textView.isAutomaticDashSubstitutionEnabled = false
        textView.isAutomaticTextReplacementEnabled = false
        textView.isAutomaticSpellingCorrectionEnabled = false
        textView.isContinuousSpellCheckingEnabled = false
        textView.isGrammarCheckingEnabled = false
        textView.isAutomaticLinkDetectionEnabled = false
        textView.isAutomaticDataDetectionEnabled = false
        textView.font = Self.baseFont
        textView.textColor = .textColor
        textView.textContainerInset = NSSize(width: 8, height: 10)

        // Soft wrap: the text container follows the view's width; no horizontal growth.
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.autoresizingMask = [.width]
        textView.minSize = .zero
        textView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        textView.textContainer?.widthTracksTextView = true

        scrollView.documentView = textView
        textView.usesFindBar = true
        textView.isIncrementalSearchingEnabled = true

        // Markdown highlighting runs on every character edit, including this first text.
        textView.textStorage?.delegate = coordinator
        textView.string = text
        textView.moveCaretToEnd()
        textView.delegate = coordinator
        return scrollView
    }

    func updateNSView(_ scrollView: NSScrollView, context: Context) {
        context.coordinator.parent = self
        guard let textView = scrollView.documentView as? ComposerNSTextView else { return }
        apply(callbacksTo: textView)
        // Push only real external changes, so selection and undo survive normal updates.
        if textView.string != text, !textView.hasMarkedText() {
            textView.suggestions.deferringRefresh {
                textView.string = text
                textView.moveCaretToEnd()
            }
        }
        textView.suggestions.catalog = suggestionCatalog
    }

    private func apply(callbacksTo textView: ComposerNSTextView) {
        textView.onDone = onDone
        textView.onDoneAndSubmit = onDoneAndSubmit
        textView.onCancel = onCancel
        textView.onEscapeOverride = onEscapeOverride
        textView.collapsePastedImages = collapsePastedImages ?? { $0 }
        textView.expandPastedImages = expandPastedImages ?? { $0 }
        textView.pastedImagePath = pastedImagePath ?? { _ in nil }
        editor?.textView = textView
    }

    final class Coordinator: NSObject, NSTextViewDelegate, NSTextStorageDelegate {
        var parent: ComposerTextView

        init(parent: ComposerTextView) {
            self.parent = parent
        }

        func textDidChange(_ notification: Notification) {
            guard let textView = notification.object as? NSTextView else { return }
            parent.text = textView.string
            (textView as? ComposerNSTextView)?.suggestions.refresh()
        }

        func textViewDidChangeSelection(_ notification: Notification) {
            (notification.object as? ComposerNSTextView)?.suggestions.refresh()
        }

        /// Attribute changes made here are not registered with the undo manager.
        /// Runs inside the storage's edit pass, while the text view's selection still has
        /// its pre-edit range: change storage attributes only, never text view state (setting
        /// typing attributes here read out of range and left the storage stuck mid-edit).
        /// Typed text needs no typing attributes: this pass restyles it before it is drawn.
        func textStorage(_ textStorage: NSTextStorage, didProcessEditing editedMask: NSTextStorageEditActions,
                         range editedRange: NSRange, changeInLength delta: Int) {
            guard editedMask.contains(.editedCharacters) else { return }
            MarkdownStyler.highlight(textStorage, editedRange: editedRange, baseFont: ComposerTextView.baseFont)
        }
    }
}

/// Edits and selects through the AppKit text view itself, so the change is undoable and
/// reaches the SwiftUI binding through the text view delegate like a typed edit.
@MainActor
final class ComposerEditorHandle {
    weak var textView: ComposerNSTextView?

    var currentText: String { textView?.string ?? "" }

    /// Locked: the text stays selectable but cannot be edited (also not by undo/redo).
    var isLocked: Bool { textView.map { !$0.isEditable } ?? false }

    private static var lastSelection: (text: String, time: ContinuousClock.Instant)?

    /// True for the selection `select` just made (same text, within a second): the
    /// floating toolbar skips it, or it would cover the preview and take its Esc.
    static func isOwnSelection(_ text: String) -> Bool {
        guard let last = lastSelection, last.text == text else { return false }
        return ContinuousClock.now - last.time < .seconds(1)
    }

    /// Selects `range`, scrolls it into view and focuses the editor; an invalid range does nothing.
    func select(_ range: NSRange) {
        guard let textView, Self.isValid(range, in: textView) else { return }
        Self.lastSelection = ((textView.string as NSString).substring(with: range), .now)
        textView.setSelectedRange(range)
        textView.scrollRangeToVisible(range)
        textView.window?.makeFirstResponder(textView)
    }

    /// Counts the selection `select` made as the editor's own again from now while it is still
    /// selected: the mouse-up of a click that closes the preview would otherwise show the toolbar.
    func refreshOwnSelection() {
        guard let textView, let last = Self.lastSelection else { return }
        let range = textView.selectedRange()
        guard Self.isValid(range, in: textView),
              (textView.string as NSString).substring(with: range) == last.text else { return }
        Self.lastSelection = (last.text, .now)
    }

    /// Deletes `range` as one undoable "Remove Image" edit, caret at its start.
    /// Does nothing while locked, during IME composition, or for an invalid or empty range.
    func remove(_ range: NSRange) {
        guard let textView, textView.isEditable, !textView.hasMarkedText(), range.length > 0,
              Self.isValid(range, in: textView) else { return }
        textView.breakUndoCoalescing()
        guard textView.shouldChangeText(in: range, replacementString: ""),
              let storage = textView.textStorage else { return }
        storage.replaceCharacters(in: range, with: "")
        textView.didChangeText()
        textView.undoManager?.setActionName("Remove Image")
        let caret = NSRange(location: range.location, length: 0)
        textView.setSelectedRange(caret)
        textView.scrollRangeToVisible(caret)
        textView.window?.makeFirstResponder(textView)
    }

    /// Locks or unlocks editing; locking also closes the command suggestion list.
    func setLocked(_ locked: Bool) {
        guard let textView else { return }
        textView.isEditable = !locked
        if locked { textView.suggestions.hide() }
    }

    private static func isValid(_ range: NSRange, in textView: NSTextView) -> Bool {
        let length = (textView.string as NSString).length
        return range.location != NSNotFound && range.location <= length && range.length <= length - range.location
    }
}

/// Text view subclass with the composer's key handling.
final class ComposerNSTextView: NSTextView {
    var onDone: () -> Void = {}
    var onDoneAndSubmit: () -> Void = {}
    var onCancel: () -> Void = {}
    /// Checked first on Esc; returning true consumes it.
    var onEscapeOverride: (() -> Bool)?
    var collapsePastedImages: (String) -> String = { $0 }
    var expandPastedImages: (String) -> String = { $0 }
    var pastedImagePath: (Int) -> String? = { _ in nil }

    /// The command suggestion list of this editor.
    private(set) lazy var suggestions = CommandSuggestionPopup(textView: self)

    /// Delay before a badge's tooltip shows, as the other hover tooltips.
    private static let badgeTooltipDelay: Duration = .milliseconds(120)
    private var badgeTrackingArea: NSTrackingArea?
    /// The badge whose tooltip is shown or pending.
    private var hoveredBadge: (range: NSRange, path: String)?
    private var badgeTooltipTask: Task<Void, Never>?

    override func viewWillMove(toWindow newWindow: NSWindow?) {
        hideBadgeTooltip()
        if let window {
            NotificationCenter.default.removeObserver(self, name: NSWindow.willCloseNotification, object: window)
        }
        super.viewWillMove(toWindow: newWindow)
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        window?.makeFirstResponder(self)
        if let window {
            NotificationCenter.default.addObserver(self, selector: #selector(windowWillClose(_:)),
                                                   name: NSWindow.willCloseNotification, object: window)
        } else {
            suggestions.hide()
        }
    }

    @objc private func windowWillClose(_ notification: Notification) {
        suggestions.hide()
        hideBadgeTooltip()
    }

    /// Focus leaving the editor hides the suggestions; a click on the list keeps the focus here.
    override func resignFirstResponder() -> Bool {
        if suggestions.isMouseDownInList(NSApp.currentEvent) { return false }
        let resigned = super.resignFirstResponder()
        if resigned { suggestions.hide() }
        return resigned
    }

    /// Esc goes to onEscapeOverride first, then closes the suggestion list, else cancels
    /// (NSTextView would otherwise start completion).
    override func cancelOperation(_ sender: Any?) {
        if onEscapeOverride?() == true { return }
        if suggestions.isVisible {
            suggestions.hide()
            return
        }
        onCancel()
    }

    override func moveUp(_ sender: Any?) {
        if suggestions.isVisible {
            suggestions.moveSelection(by: -1)
        } else {
            super.moveUp(sender)
        }
    }

    override func moveDown(_ sender: Any?) {
        if suggestions.isVisible {
            suggestions.moveSelection(by: 1)
        } else {
            super.moveDown(sender)
        }
    }

    /// The caret after the last character, scrolled into view, to go on writing.
    func moveCaretToEnd() {
        let end = NSRange(location: (string as NSString).length, length: 0)
        setSelectedRange(end)
        scrollRangeToVisible(end)
    }

    /// Return accepts the selected suggestion, else continues or ends a Markdown list (ListContinuation).
    override func insertNewline(_ sender: Any?) {
        if suggestions.acceptSelected() { return }
        if !applyListEdit(ListContinuation.returnEdit) { super.insertNewline(sender) }
    }

    /// Backspace right after a list marker outdents a nested item or deletes a top-level marker.
    override func deleteBackward(_ sender: Any?) {
        if !applyListEdit(ListContinuation.backspaceEdit) { super.deleteBackward(sender) }
    }

    /// Tab accepts the selected suggestion, else indents a list item one level.
    override func insertTab(_ sender: Any?) {
        if suggestions.acceptSelected() { return }
        if !applyListEdit(ListContinuation.indentEdit) { super.insertTab(sender) }
    }

    /// Shift+Tab on a nested list item outdents it one level; elsewhere it does nothing
    /// (never moves the focus out of the editor).
    override func insertBacktab(_ sender: Any?) {
        _ = applyListEdit(ListContinuation.outdentEdit)
    }

    /// An image (or copied image files) pastes as "@path" mentions; anything else as plain text.
    override func paste(_ sender: Any?) {
        guard isEditable, !hasMarkedText(), let paths = ComposerImageStore.standard?.imagePaths(from: .general) else {
            super.paste(sender)
            return
        }
        let range = selectedRange()
        let previous = range.location > 0
            ? (string as NSString).substring(with: NSRange(location: range.location - 1, length: 1)).first
            : nil
        insertText(collapsePastedImages(ComposerImageStore.insertion(for: paths, after: previous)),
                   replacementRange: range)
    }

    /// Copy, cut and drag: the text leaves the editor with its badges expanded.
    override func writeSelection(to pboard: NSPasteboard, types: [NSPasteboard.PasteboardType]) -> Bool {
        guard super.writeSelection(to: pboard, types: types) else { return false }
        if let text = pboard.string(forType: .string) {
            let expanded = expandPastedImages(text)
            if expanded != text { pboard.setString(expanded, forType: .string) }
        }
        return true
    }

    /// Paste and drop of text: store mentions become badges again. The collapsed text is
    /// read through a private pasteboard, so AppKit still inserts it (range, undo).
    override func readSelection(from pboard: NSPasteboard, type: NSPasteboard.PasteboardType) -> Bool {
        guard let text = pboard.string(forType: .string) else {
            return super.readSelection(from: pboard, type: type)
        }
        let collapsed = collapsePastedImages(text)
        guard collapsed != text else { return super.readSelection(from: pboard, type: type) }
        let collapsedBoard = NSPasteboard.withUniqueName()
        defer { collapsedBoard.releaseGlobally() }
        collapsedBoard.setString(collapsed, forType: .string)
        return super.readSelection(from: collapsedBoard, type: .string)
    }

    // MARK: Pasted image badge tooltip

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let badgeTrackingArea { removeTrackingArea(badgeTrackingArea) }
        let area = NSTrackingArea(rect: .zero,
                                  options: [.mouseMoved, .mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect],
                                  owner: self, userInfo: nil)
        addTrackingArea(area)
        badgeTrackingArea = area
    }

    override func mouseMoved(with event: NSEvent) {
        super.mouseMoved(with: event)
        updateBadgeTooltip(at: convert(event.locationInWindow, from: nil))
    }

    override func mouseExited(with event: NSEvent) {
        super.mouseExited(with: event)
        hideBadgeTooltip()
    }

    override func mouseDown(with event: NSEvent) {
        hideBadgeTooltip()
        super.mouseDown(with: event)
    }

    override func scrollWheel(with event: NSEvent) {
        hideBadgeTooltip()
        super.scrollWheel(with: event)
    }

    override func didChangeText() {
        super.didChangeText()
        hideBadgeTooltip()
    }

    /// Shows the path of the known badge under `point` (after the hover delay), else
    /// hides the tooltip. Not while locked: the image preview covers the editor then.
    private func updateBadgeTooltip(at point: NSPoint) {
        guard isEditable, let badge = badge(at: point), let path = pastedImagePath(badge.number) else {
            hideBadgeTooltip()
            return
        }
        if let hoveredBadge, hoveredBadge.range == badge.range, hoveredBadge.path == path { return }
        hideBadgeTooltip()
        hoveredBadge = (badge.range, path)
        badgeTooltipTask = Task { [weak self] in
            try? await Task.sleep(for: Self.badgeTooltipDelay)
            guard let self, !Task.isCancelled, self.hoveredBadge?.range == badge.range else { return }
            HoverTooltipPanelPresenter.shared.show(text: path, anchor: self, rect: badge.rect, leadingAligned: true)
        }
    }

    private func hideBadgeTooltip() {
        badgeTooltipTask?.cancel()
        badgeTooltipTask = nil
        guard hoveredBadge != nil else { return }
        hoveredBadge = nil
        HoverTooltipPanelPresenter.shared.hide(anchor: self)
    }

    /// The badge drawn under `point` (view coordinates): its range, number and rect.
    /// TextKit 1 gives the nearest glyph, so the point must also be inside that glyph.
    private func badge(at point: NSPoint) -> (range: NSRange, number: Int, rect: NSRect)? {
        guard let layoutManager, let textContainer else { return nil }
        let origin = textContainerOrigin
        let containerPoint = NSPoint(x: point.x - origin.x, y: point.y - origin.y)
        let glyphIndex = layoutManager.glyphIndex(for: containerPoint, in: textContainer)
        guard glyphIndex < layoutManager.numberOfGlyphs,
              layoutManager.boundingRect(forGlyphRange: NSRange(location: glyphIndex, length: 1), in: textContainer)
                .contains(containerPoint) else { return nil }
        let characterIndex = layoutManager.characterIndexForGlyph(at: glyphIndex)
        guard let (range, number) = ComposerPastedImages.badgeRange(at: characterIndex, in: string) else { return nil }
        let glyphs = layoutManager.glyphRange(forCharacterRange: range, actualCharacterRange: nil)
        let rect = layoutManager.boundingRect(forGlyphRange: glyphs, in: textContainer)
            .offsetBy(dx: origin.x, dy: origin.y)
        return (range, number, rect)
    }

    /// Applies the list edit for a single caret (no selection, no IME composition) as one undoable edit.
    private func applyListEdit(_ makeEdit: (String, Int) -> ListContinuation.Edit?) -> Bool {
        let selection = selectedRange()
        guard isEditable, !hasMarkedText(), selectedRanges.count == 1, selection.length == 0,
              let edit = makeEdit(string, selection.location) else { return false }
        insertText(edit.replacement, replacementRange: edit.range)
        return true
    }

    /// Called for every view in the key window, not only the focused one: when the
    /// find bar is open its search field is the first responder, so clipboard and
    /// select-all actions go to the first responder, and undo/find only act here
    /// when this view has focus.
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection([.command, .shift, .option, .control])
        let isReturn = event.keyCode == 36 || event.keyCode == 76 // Return, keypad Enter
        if isReturn, modifiers == [.command] {
            suggestions.hide()
            onDone()
            return true
        }
        if isReturn, modifiers == [.command, .shift] {
            suggestions.hide()
            onDoneAndSubmit()
            return true
        }

        let key = event.charactersIgnoringModifiers?.lowercased() ?? ""
        let isFocused = window?.firstResponder === self
        switch (key, modifiers) {
        case ("x", [.command]):
            return NSApp.sendAction(#selector(NSText.cut(_:)), to: nil, from: nil)
        case ("c", [.command]):
            return NSApp.sendAction(#selector(NSText.copy(_:)), to: nil, from: nil)
        case ("v", [.command]):
            return NSApp.sendAction(#selector(NSText.paste(_:)), to: nil, from: nil)
        case ("a", [.command]):
            return NSApp.sendAction(#selector(NSText.selectAll(_:)), to: nil, from: nil)
        case ("z", [.command]) where isFocused:
            // Locked (not editable): swallow, so undo cannot change the text.
            if isEditable, undoManager?.canUndo == true { undoManager?.undo() }
            return true
        case ("z", [.command, .shift]) where isFocused:
            if isEditable, undoManager?.canRedo == true { undoManager?.redo() }
            return true
        case ("f", [.command]) where isFocused:
            let item = NSMenuItem()
            item.tag = NSTextFinder.Action.showFindInterface.rawValue
            performTextFinderAction(item)
            return true
        default:
            return super.performKeyEquivalent(with: event)
        }
    }
}
