using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PopGuy.Controls;
using PopGuy.Core.Composer;
using PopGuy.Platform.Capture;
using PopGuy.Platform.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Color = Windows.UI.Color;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;
using VirtualKey = Windows.System.VirtualKey;

namespace PopGuy.Composer;

/// <summary>
/// The Terminal Composer window (macOS ComposerWindow.swift, ComposerView.swift, ComposerTextView.swift): a resizable
/// editor window on the solid base background, centered on the monitor with the pointer. The editor is a plain-text
/// <see cref="RichEditBox"/> (paste and copy are plain text) with simple Markdown highlighting from
/// <see cref="MarkdownHighlighter"/>: a heading line bold at the base size, code monospace on a subtle background,
/// bold and italic as their traits, list markers and fence lines dimmed. Keys: Ctrl+Enter Done, Ctrl+Shift+Enter Done
/// &amp; Submit, Esc Cancel (closes the image preview, the suggestions, then the find bar first), Ctrl+F find;
/// Tab / Shift+Tab indent / outdent a list item. In a CLI session, a command or skill typed with its trigger ("/" or "$") shows the CLI's matching
/// <see cref="CatalogItem"/>s at the caret (<see cref="CommandSuggestions"/>): Up/Down select, Enter/Tab or a click accept.
/// Pasting an image (or copied image files) inserts "@path" mentions (<see cref="ComposerImageStore"/>); a mention of
/// PopGuy's own pasted image shows as a "[Pasted image #N]" badge (<see cref="ComposerPastedImages"/>). Unlike the toolbar, the composer takes the
/// foreground. One window per session: the controller makes it, answers its requests and closes it with
/// <see cref="CloseComposer"/>. The text is never logged. UI thread only.
/// </summary>
public sealed partial class ComposerWindow : Window
{
    private const double DefaultWidth = 720;
    private const double DefaultHeight = 480;
    private const double MinWidth = 520;
    private const double MinHeight = 260;

    /// <summary>Highlighting waits for a pause in typing, so a burst of keys is one undo step and one format pass.</summary>
    private static readonly TimeSpan HighlightDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>The editor's font (the XAML FontFamily): highlighting resets text to it.</summary>
    private const string BaseFontName = "Segoe UI";

    /// <summary>Inbox on every Windows.</summary>
    private const string CodeFontName = "Consolas";

    private readonly DispatcherQueueTimer _highlightTimer;

    /// <summary>The document's text ends with a paragraph mark the user never typed (measured once, at start).</summary>
    private readonly bool _hasFinalMark;

    /// <summary>The raw text the formats match: a TextChanged with this text is a format change, not an edit.</summary>
    private string _highlighted = "";

    /// <summary>A format pass is running: its own TextChanged events are ignored.</summary>
    private bool _formatting;

    private string? _sourceAppName;
    private string? _noTargetHint;
    private bool _composeMode;
    private bool _canSubmit = true;
    private bool _sessionEnded;
    private bool _draftAvailable;
    private bool _placed;
    private bool _closingByCode;
    private bool _closed;

    /// <summary>The CLI's agent and its commands and skills (<see cref="SetCatalog"/>); none until it arrives.</summary>
    private string? _catalogAgent;
    private IReadOnlyList<CatalogItem> _catalogItems = [];

    /// <summary>The suggestion list's geometry (DIPs): fixed, so placing it never waits for a layout pass.</summary>
    private const double SuggestionRowHeight = 32;
    private const int SuggestionVisibleRows = 6;
    private const double SuggestionWidth = 420;
    private const double SuggestionChrome = 10; // The popup's padding (4) and border (1), both sides.
    private const double SuggestionGap = 4;
    private const double SuggestionEdgeMargin = 8;
    private const double SuggestionTextInset = 17; // The popup's padding and border (5) and the name's margin (12).

    /// <summary>The token the shown list is for, its items and the selected row; null token: the list is hidden.</summary>
    private CommandSuggestions.Token? _suggestionToken;
    private IReadOnlyList<CatalogItem> _suggestions = [];
    private List<Grid> _suggestionRows = [];
    private int _suggestionIndex;

    /// <summary>The token Esc closed the list for: it stays closed until the token changes.</summary>
    private CommandSuggestions.Token? _dismissedToken;

    /// <summary>An IME composition is in progress: no suggestions.</summary>
    private bool _composing;

    /// <summary>The editor's own scroll viewer (its template's), for the caret's place while scrolled.</summary>
    private ScrollViewer? _editorScroll;

    /// <summary>Where pasted images are saved (null: images paste as nothing, as before).</summary>
    public ComposerImageStore? ImageStore { get; set; }

    public ComposerWindow()
    {
        InitializeComponent();
        Hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        // The title bar's icon (an unpackaged app's windows have none of their own).
        AppWindow.SetIcon(App.IconPath);

        _highlightTimer = DispatcherQueue.CreateTimer();
        _highlightTimer.Interval = HighlightDelay;
        _highlightTimer.IsRepeating = false;
        _highlightTimer.Tick += OnHighlightTick;

        // RichEdit's text ends with a final paragraph mark ("x" reads "x\r"): measured, then left out of the text.
        Editor.TextDocument.SetText(TextSetOptions.None, "x");
        Editor.TextDocument.GetText(TextGetOptions.None, out var probe);
        _hasFinalMark = probe == "x\r";
        Editor.TextDocument.SetText(TextSetOptions.None, "");
        Editor.TextDocument.ClearUndoRedoHistory();

        HoverTooltip.SetText(CancelButton, "Cancel (Esc)");
        HoverTooltip.SetText(DoneSubmitButton, "Done & Submit (Ctrl+Shift+Enter)");
        HoverTooltip.SetText(RestoreDraftButton, "Restore the last saved draft");
        SetIconButton(FindPreviousButton, "Previous match (Shift+Enter)");
        SetIconButton(FindNextButton, "Next match (Enter)");
        SetIconButton(FindCloseButton, "Close (Esc)");

        CancelButton.Click += OnCancelClick;
        DoneButton.Click += OnDoneClick;
        DoneSubmitButton.Click += OnDoneSubmitClick;
        RestoreDraftButton.Click += OnRestoreDraftClick;
        FindPreviousButton.Click += OnFindPreviousClick;
        FindNextButton.Click += OnFindNextClick;
        FindCloseButton.Click += OnFindCloseClick;
        FindBox.PreviewKeyDown += OnFindBoxPreviewKeyDown;
        FindBox.TextChanged += OnFindBoxTextChanged;
        Editor.TextChanged += OnEditorTextChanged;
        Editor.Paste += OnEditorPaste;
        Editor.CopyingToClipboard += OnEditorCopyingToClipboard;
        Editor.CuttingToClipboard += OnEditorCuttingToClipboard;
        Editor.PreviewKeyDown += OnEditorPreviewKeyDown;
        Editor.Loaded += OnEditorLoaded;
        Editor.SelectionChanged += OnEditorSelectionChanged;
        Editor.TextCompositionStarted += OnEditorCompositionStarted;
        Editor.TextCompositionEnded += OnEditorCompositionEnded;
        Editor.SizeChanged += OnEditorSizeChanged;
        RootGrid.PreviewKeyDown += OnRootPreviewKeyDown;
        RootGrid.ActualThemeChanged += OnActualThemeChanged;
        AppWindow.Closing += OnAppWindowClosing;
        Activated += OnActivated;
        Closed += OnClosed;
        InitializeImages();

        UpdateFooter();
    }

    // Requests to the controller.

    /// <summary>
    /// Done (false) or Done &amp; Submit (true, only while <see cref="CanSubmit"/>). Not raised while the text is too large,
    /// the session ended, or Compose has no target.
    /// </summary>
    public event Action<bool>? DoneRequested;

    /// <summary>Cancel, Esc, or the window's close button (the window stays open: the controller closes it).</summary>
    public event Action? CancelRequested;

    /// <summary>After the session ended, or in Compose without a target: the text was put on the clipboard.</summary>
    public event Action? CopyAndCloseRequested;

    /// <summary>"Restore draft": the controller sets <see cref="Text"/> from the draft.</summary>
    public event Action? RestoreDraftRequested;

    /// <summary>The text changed: raised after a pause in typing (not per key), for the draft autosave.</summary>
    public event Action? TextEdited;

    // State from the controller.

    public nint Hwnd { get; }

    /// <summary>The foreground window when <see cref="ShowAndActivate"/> was called (not this one); 0 when none.</summary>
    public nint PreviousForeground { get; private set; }

    /// <summary>
    /// The editor's plain text with "\n" line breaks and the pasted-image badges expanded to their full mentions: the text
    /// that leaves the composer (Done, Copy &amp; Close, drafts). Setting it loads the text with an empty undo history,
    /// with the mentions of PopGuy's own pasted images shown as badges.
    /// </summary>
    public string Text
    {
        get => ExpandPastedImages(ComposerFileRules.NormalizeLineEndings(RawText().Replace('\v', '\n')));
        set
        {
            _highlightTimer.Stop();
            Editor.TextDocument.SetText(TextSetOptions.None, CollapsePastedImages(value.Replace("\r\n", "\r").Replace('\n', '\r')));
            Highlight();
            Editor.TextDocument.ClearUndoRedoHistory();
            // The caret at the end, to go on writing.
            Editor.TextDocument.Selection.SetRange(_highlighted.Length, _highlighted.Length);
            RefreshImageStrip();
        }
    }

    /// <summary>
    /// The app the text is edited for ("Editing for …"), or in <see cref="ComposeMode"/> the app Done pastes into
    /// ("Pasting into …"). Null or empty: none; in Compose mode Done then becomes Copy &amp; Close.
    /// </summary>
    public string? SourceAppName
    {
        get => _sourceAppName;
        set
        {
            _sourceAppName = value;
            UpdateFooter();
        }
    }

    /// <summary>
    /// The footer of a <see cref="ComposeMode"/> composer without a target, saying why there is none; null: the default
    /// ("no target").
    /// </summary>
    public string? NoTargetHint
    {
        get => _noTargetHint;
        set
        {
            _noTargetHint = value;
            UpdateFooter();
        }
    }

    /// <summary>A Compose composer (no CLI session): Done pastes into <see cref="SourceAppName"/>.</summary>
    public bool ComposeMode
    {
        get => _composeMode;
        set
        {
            _composeMode = value;
            UpdateFooter();
        }
    }

    /// <summary>Done &amp; Submit (and Ctrl+Shift+Enter) is offered; false: Done only, no Enter.</summary>
    public bool CanSubmit
    {
        get => _canSubmit;
        set
        {
            _canSubmit = value;
            UpdateFooter();
        }
    }

    /// <summary>Done copies the text and closes: the session ended, or Compose has no target.</summary>
    private bool CopyOnly => _sessionEnded || _composeMode && string.IsNullOrEmpty(_sourceAppName);

    /// <summary>The CLI went away: Done becomes Copy &amp; Close and Done &amp; Submit hides.</summary>
    public bool SessionEnded
    {
        get => _sessionEnded;
        set
        {
            _sessionEnded = value;
            UpdateFooter();
        }
    }

    /// <summary>An older draft exists: shows "Restore draft".</summary>
    public bool DraftAvailable
    {
        get => _draftAvailable;
        set
        {
            _draftAvailable = value;
            RestoreDraftButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The calling CLI's commands and skills, for the suggestions (CLI sessions only).</summary>
    public void SetCatalog(string agent, IReadOnlyList<CatalogItem> items)
    {
        _catalogAgent = agent;
        _catalogItems = items;
        // A catalog that arrives after the token was typed shows its list at once.
        RefreshSuggestions();
    }

    // Show and close.

    /// <summary>
    /// Shows the window (centered on the monitor with the pointer the first time) and activates it with the keyboard
    /// focus in the editor. <paramref name="fromPipe"/>: opened by a popguy-edit request, so PopGuy got no input event
    /// and Windows would only flash its taskbar button; the input queue is then attached to the foreground thread
    /// around <c>SetForegroundWindow</c>.
    /// </summary>
    public void ShowAndActivate(bool fromPipe)
    {
        if (_closed) return;
        var foreground = ToolbarWindowStyle.ForegroundWindow();
        if (foreground != 0 && foreground != Hwnd) PreviousForeground = foreground;
        if (!_placed)
        {
            _placed = true;
            Place();
        }
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        if (fromPipe && ToolbarWindowStyle.ForegroundWindow() != Hwnd) ForceForeground();
        Editor.Focus(FocusState.Programmatic);
    }

    /// <summary>Closes the window without raising <see cref="CancelRequested"/>.</summary>
    public void CloseComposer()
    {
        if (_closed) return;
        _closingByCode = true;
        Close();
    }

    /// <summary>
    /// The close button and Alt+F4 are Cancel: the window stays until the controller closes it, on the next dispatcher
    /// turn (not from inside the Closing event).
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingByCode || _closed) return;
        args.Cancel = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed && !_closingByCode) CancelRequested?.Invoke();
        });
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _highlightTimer.Stop();
        _highlightTimer.Tick -= OnHighlightTick;
        Editor.TextChanged -= OnEditorTextChanged;
        Editor.Paste -= OnEditorPaste;
        Editor.CopyingToClipboard -= OnEditorCopyingToClipboard;
        Editor.CuttingToClipboard -= OnEditorCuttingToClipboard;
        Editor.PreviewKeyDown -= OnEditorPreviewKeyDown;
        Editor.Loaded -= OnEditorLoaded;
        Editor.SelectionChanged -= OnEditorSelectionChanged;
        Editor.TextCompositionStarted -= OnEditorCompositionStarted;
        Editor.TextCompositionEnded -= OnEditorCompositionEnded;
        Editor.SizeChanged -= OnEditorSizeChanged;
        if (_editorScroll is not null) _editorScroll.ViewChanged -= OnEditorViewChanged;
        FindBox.PreviewKeyDown -= OnFindBoxPreviewKeyDown;
        FindBox.TextChanged -= OnFindBoxTextChanged;
        RootGrid.PreviewKeyDown -= OnRootPreviewKeyDown;
        RootGrid.ActualThemeChanged -= OnActualThemeChanged;
        AppWindow.Closing -= OnAppWindowClosing;
        Activated -= OnActivated;
        Closed -= OnClosed;
    }

    /// <summary>The default size (in DIPs at that monitor's scale) centered on the work area of the monitor with the pointer.</summary>
    private void Place()
    {
        var pointer = ToolbarWindowStyle.CursorPosition();
        var work = DisplayArea.GetFromPoint(new PointInt32(pointer.X, pointer.Y), DisplayAreaFallback.Nearest).WorkArea;
        var scale = MonitorInfo.Scale(pointer);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Px(MinWidth, scale);
            presenter.PreferredMinimumHeight = Px(MinHeight, scale);
        }
        var width = Math.Min(Px(DefaultWidth, scale), work.Width);
        var height = Math.Min(Px(DefaultHeight, scale), work.Height);
        AppWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
    }

    private static int Px(double dips, double scale) => (int)Math.Round(dips * scale);

    /// <summary>
    /// The foreground-unlock lever: with this thread's input attached to the foreground window's thread, Windows lets
    /// <c>SetForegroundWindow</c> through. Detached right after.
    /// </summary>
    private void ForceForeground()
    {
        var foreground = ToolbarWindowStyle.ForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, 0);
        var thread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thread && AttachThreadInput(thread, foregroundThread, true);
        try
        {
            ToolbarWindowStyle.SetForeground(Hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thread, foregroundThread, false);
        }
    }

    /// <summary>
    /// The toolbar's Paste back (in-process): the editor's selection becomes <paramref name="text"/> as plain text, with
    /// the caret after it, as a paste does.
    /// </summary>
    public void ReplaceSelection(string text)
    {
        if (_closed) return;
        var selection = Editor.TextDocument.Selection;
        selection.SetText(TextSetOptions.None, text.Replace("\r\n", "\r").Replace('\n', '\r'));
        selection.Collapse(false);
        Editor.Focus(FocusState.Programmatic);
    }

    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        Editor.Focus(FocusState.Programmatic);
        if (_editorScroll is null && FindScrollViewer(Editor) is { } scroll)
        {
            _editorScroll = scroll;
            scroll.ViewChanged += OnEditorViewChanged;
        }
    }

    /// <summary>Another app took the foreground: the suggestions close.</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) HideSuggestions();
    }

    // Footer.

    private void UpdateFooter()
    {
        var text = Text;
        var tooLarge = Encoding.UTF8.GetByteCount(text) > ComposerMessage.MaxTextBytes;
        var copyOnly = CopyOnly;
        var named = !string.IsNullOrEmpty(_sourceAppName);
        SourceHint.Text = _composeMode
            ? (named ? $"Pasting into {_sourceAppName}" : _noTargetHint ?? "Compose (no target: Copy & Close copies the text)")
            : (named ? $"Editing for {_sourceAppName}" : "Compose");
        // The target can be long enough to be trimmed.
        HoverTooltip.SetText(SourceHint, SourceHint.Text);
        SessionEndedNotice.Visibility = _sessionEnded ? Visibility.Visible : Visibility.Collapsed;
        TooLargeNotice.Visibility = tooLarge && !copyOnly ? Visibility.Visible : Visibility.Collapsed;
        DoneSubmitButton.Visibility = copyOnly || !_canSubmit ? Visibility.Collapsed : Visibility.Visible;
        if (copyOnly)
        {
            DoneButton.Content = "Copy & Close";
            DoneButton.IsEnabled = text.Length > 0;
            SetTooltip(DoneButton, "Copy & Close (Ctrl+Enter)");
        }
        else
        {
            DoneButton.Content = "Done";
            DoneButton.IsEnabled = !tooLarge;
            SetTooltip(DoneButton, "Done (Ctrl+Enter)");
        }
        DoneSubmitButton.IsEnabled = !tooLarge;
    }

    private static void SetTooltip(Button button, string text)
    {
        HoverTooltip.SetText(button, text);
        AutomationProperties.SetHelpText(button, text);
    }

    private static void SetIconButton(Button button, string text)
    {
        AutomationProperties.SetName(button, text);
        HoverTooltip.SetText(button, text);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        HideSuggestions();
        CancelRequested?.Invoke();
    }

    private void OnDoneClick(object sender, RoutedEventArgs e) => Done();

    private void OnDoneSubmitClick(object sender, RoutedEventArgs e) => DoneAndSubmit();

    private void OnRestoreDraftClick(object sender, RoutedEventArgs e) => RestoreDraftRequested?.Invoke();

    /// <summary>
    /// Done, or Copy &amp; Close once the session ended or when Compose has no target (the clipboard write is the user's
    /// own action).
    /// </summary>
    private void Done()
    {
        ClosePreview();
        HideSuggestions();
        var text = Text;
        if (CopyOnly)
        {
            // A failed write keeps the window open, so the text is not lost.
            if (text.Length > 0 && ClipboardGuard.WriteText(text, excludeFromHistory: false)) CopyAndCloseRequested?.Invoke();
            return;
        }
        if (Encoding.UTF8.GetByteCount(text) > ComposerMessage.MaxTextBytes)
        {
            UpdateFooter();
            return;
        }
        DoneRequested?.Invoke(false);
    }

    private void DoneAndSubmit()
    {
        ClosePreview();
        HideSuggestions();
        if (CopyOnly || !_canSubmit) return;
        if (Encoding.UTF8.GetByteCount(Text) > ComposerMessage.MaxTextBytes)
        {
            UpdateFooter();
            return;
        }
        DoneRequested?.Invoke(true);
    }

    // Keys.

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>The window's key map, before the focused control sees the key.</summary>
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var control = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        // The image preview takes the keys first: Esc closes it, Ctrl+Enter goes on to Done (which closes it first). The
        // focused close button keeps Enter and Space, Alt+F4 still closes the window; every other key is swallowed, so
        // nothing reaches the editor (or Find, undo, paste) under the scrim.
        if (IsPreviewOpen && !(e.Key == VirtualKey.Enter && control))
        {
            if (e.Key == VirtualKey.Escape) ClosePreview();
            else if (e.Key is VirtualKey.Enter or VirtualKey.Space && ImagePreviewClose.FocusState != FocusState.Unfocused) return;
            else if (e.Key == VirtualKey.F4 && IsDown(VirtualKey.Menu)) return;
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Enter && control)
        {
            e.Handled = true;
            if (shift) DoneAndSubmit();
            else Done();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (SuggestionsVisible) DismissSuggestions();
            else if (FindBar.Visibility == Visibility.Visible) CloseFind();
            else CancelRequested?.Invoke();
        }
        else if (e.Key == VirtualKey.F && control && !shift)
        {
            e.Handled = true;
            OpenFind();
        }
    }

    /// <summary>
    /// While the suggestions show, Up and Down select and Enter and Tab accept (without modifiers). Enter and Backspace
    /// continue and end Markdown lists, Tab and Shift+Tab indent and outdent list items. Undo and redo step over the
    /// format passes (they leave the text as it was), so one key is one edit.
    /// </summary>
    private void OnEditorPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = IsDown(VirtualKey.Shift);
        if (SuggestionsVisible && !shift && !IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Menu)
            && HandleSuggestionKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (!IsDown(VirtualKey.Control))
        {
            if (IsDown(VirtualKey.Menu)) return;
            Func<string, int, ListEdit?>? makeEdit = e.Key switch
            {
                VirtualKey.Enter when !shift => ListContinuation.ReturnEdit,
                VirtualKey.Back when !shift => ListContinuation.BackspaceEdit,
                VirtualKey.Tab => shift ? ListContinuation.OutdentEdit : ListContinuation.IndentEdit,
                _ => null,
            };
            if (makeEdit is not null && ApplyListEdit(makeEdit)) e.Handled = true;
            // Shift+Tab off a nested item does nothing (never moves the focus out of the editor).
            else if (e.Key == VirtualKey.Tab && shift) e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Z)
        {
            e.Handled = true;
            StepHistory(redo: shift);
        }
        else if (e.Key == VirtualKey.Y && !shift)
        {
            e.Handled = true;
            StepHistory(redo: true);
        }
    }

    /// <summary>
    /// The <see cref="ListContinuation"/> edit at a caret (no selection), as one undoable edit with the caret after it;
    /// false: the key does its own thing.
    /// </summary>
    private bool ApplyListEdit(Func<string, int, ListEdit?> makeEdit)
    {
        var document = Editor.TextDocument;
        var selection = document.Selection;
        if (selection.StartPosition != selection.EndPosition) return false;
        if (makeEdit(RawText(), selection.StartPosition) is not { } edit) return false;
        var range = document.GetRange(edit.Start, edit.Start + edit.Length);
        range.SetText(TextSetOptions.None, edit.Replacement.Replace('\n', '\r'));
        selection.SetRange(range.EndPosition, range.EndPosition);
        return true;
    }

    /// <summary>
    /// RichEdit records format changes as undo steps and has no way to leave them out, so each format pass is grouped
    /// into one step and undo/redo skip the steps that do not change the text. An undone edit brings its formats back
    /// with it; a redone edit is followed by its format pass when that is the next step.
    /// </summary>
    private void StepHistory(bool redo)
    {
        var document = Editor.TextDocument;
        _highlightTimer.Stop();
        var before = RawText();
        var current = before;
        while (redo ? document.CanRedo() : document.CanUndo())
        {
            if (redo) document.Redo();
            else document.Undo();
            current = RawText();
            if (current != before) break;
        }
        if (redo && current != before && document.CanRedo())
        {
            document.Redo();
            if (RawText() != current) document.Undo();
        }
        _highlighted = RawText();
        UpdateFooter();
        // The highlight timer is stopped: the strip follows the undone or redone text here.
        if (_highlighted != _stripText) RefreshImageStrip();
    }

    // Paste: plain text (its mentions of PopGuy's own pasted images as badges), or "@path" mentions for an image or copied
    // image files.

    private async void OnEditorPaste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        string? text;
        try
        {
            var content = Clipboard.GetContent();
            text = await ImagePasteText(content)
                ?? (content.Contains(StandardDataFormats.Text) ? CollapsePastedImages(await content.GetTextAsync()) : null);
            if (text is null) return;
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or IOException)
        {
            return;
        }
        if (_closed) return;
        var selection = Editor.TextDocument.Selection;
        selection.SetText(TextSetOptions.None, text.Replace("\r\n", "\r").Replace('\n', '\r'));
        selection.Collapse(false);
    }

    // Copy and cut: the selection leaves the editor with its badges expanded (macOS writeSelection).

    private void OnEditorCopyingToClipboard(RichEditBox sender, TextControlCopyingToClipboardEventArgs e) =>
        e.Handled = CopyExpandedSelection(cut: false);

    private void OnEditorCuttingToClipboard(RichEditBox sender, TextControlCuttingToClipboardEventArgs e) =>
        e.Handled = CopyExpandedSelection(cut: true);

    /// <summary>
    /// Puts the selection on the clipboard as plain text with its badges expanded and, for a cut, deletes it as one range
    /// edit (one undo step). False when the selection has no badge of this session: the editor's own copy or cut runs.
    /// </summary>
    private bool CopyExpandedSelection(bool cut)
    {
        if (_closed) return false;
        var text = RawText();
        var selection = Editor.TextDocument.Selection;
        // The final paragraph mark is not part of the text.
        var start = Math.Min(selection.StartPosition, text.Length);
        var end = Math.Min(selection.EndPosition, text.Length);
        if (end <= start) return false;
        var selected = text[start..end];
        var expanded = ExpandPastedImages(selected);
        if (expanded == selected) return false;
        // Handled even when the write fails (nothing is cut then): the editor's own copy would put the badges there.
        if (!ClipboardGuard.WriteText(expanded.Replace('\v', '\n'), excludeFromHistory: false) || !cut) return true;
        Editor.TextDocument.GetRange(start, end).SetText(TextSetOptions.None, "");
        Editor.TextDocument.Selection.SetRange(start, start);
        return true;
    }

    /// <summary>
    /// The mentions to paste for the clipboard's images, or null to paste text: copied files that are all images (their
    /// own paths), else a bitmap when the clipboard has no text (or only a lone URL, as browsers copy an image), saved as
    /// a PNG in <see cref="ImageStore"/>. A file in that folder pastes as its badge.
    /// </summary>
    private async Task<string?> ImagePasteText(DataPackageView content)
    {
        if (ImageStore is not { } store) return null;
        List<string> paths;
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            var items = await content.GetStorageItemsAsync();
            paths = [.. items.Select(item => item.Path)];
            if (paths.Count == 0 || !paths.All(path => !string.IsNullOrEmpty(path) && ComposerImageStore.IsImageFile(path))) return null;
        }
        else
        {
            if (!content.Contains(StandardDataFormats.Bitmap)) return null;
            if (content.Contains(StandardDataFormats.Text) && !ComposerImageStore.IsLoneUrl(await content.GetTextAsync())) return null;
            var png = await EncodePng(await content.GetBitmapAsync());
            if (await Task.Run(() => store.Save(png)) is not { } path) return null;
            paths = [path];
        }
        if (_closed) return null;
        var selection = Editor.TextDocument.Selection;
        char? previous = null;
        if (selection.StartPosition > 0)
        {
            var before = RawText();
            if (selection.StartPosition <= before.Length) previous = before[selection.StartPosition - 1];
        }
        return CollapsePastedImages(ComposerImageStore.Insertion(paths, previous));
    }

    /// <summary>The clipboard bitmap re-encoded as PNG.</summary>
    private static async Task<byte[]> EncodePng(Windows.Storage.Streams.RandomAccessStreamReference reference)
    {
        using var source = await reference.OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(source);
        using var bitmap = await decoder.GetSoftwareBitmapAsync();
        using var output = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        output.Seek(0);
        using var reader = new Windows.Storage.Streams.DataReader(output);
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    // Highlighting.

    /// <summary>The document's text with "\r" line breaks, without the final paragraph mark; its indices are the editor's.</summary>
    private string RawText()
    {
        Editor.TextDocument.GetText(TextGetOptions.None, out var text);
        return _hasFinalMark && text.EndsWith('\r') ? text[..^1] : text;
    }

    private void OnEditorTextChanged(object sender, RoutedEventArgs e)
    {
        if (_formatting || _closed) return;
        _highlightTimer.Stop();
        _highlightTimer.Start();
        RefreshSuggestions();
    }

    private void OnHighlightTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_closed) return;
        if (RawText() != _highlighted) Highlight();
        UpdateFooter();
        // Also past the highlighter's size limit; a text the strip already shows is not parsed again.
        if (RawText() != _stripText) RefreshImageStrip();
        TextEdited?.Invoke();
    }

    /// <summary>Theme colors are set on the text itself: a theme switch formats it again.</summary>
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (!_closed) DispatcherQueue.TryEnqueue(Highlight);
    }

    /// <summary>
    /// One format pass over the whole text (up to <see cref="MarkdownHighlighter.FullDocumentLimit"/>; a longer text
    /// keeps its formats): the base format first, then the runs. One undo step, one repaint.
    /// </summary>
    private void Highlight()
    {
        if (_closed || _formatting) return;
        var raw = RawText();
        _highlighted = raw;
        if (raw.Length > MarkdownHighlighter.FullDocumentLimit) return;

        var theme = RootGrid.ActualTheme;
        var background = ThemeColor("SolidBackgroundFillColorBase", theme);
        var text = Over(ThemeColor("TextFillColorPrimary", theme), background);
        var dimmed = Over(ThemeColor("TextFillColorSecondary", theme), background);
        var code = Over(ThemeColor("SubtleFillColorSecondary", theme), background);

        var document = Editor.TextDocument;
        _formatting = true;
        document.BatchDisplayUpdates();
        document.BeginUndoGroup();
        try
        {
            var all = document.GetRange(0, raw.Length).CharacterFormat;
            all.Bold = FormatEffect.Off;
            all.Italic = FormatEffect.Off;
            all.Underline = UnderlineType.None;
            all.Name = BaseFontName;
            all.ForegroundColor = text;
            all.BackgroundColor = background;
            // "\v" is a soft line break (Shift+Enter): a line end for the highlighter, at the same index.
            foreach (var run in MarkdownHighlighter.Runs(raw.Replace('\v', '\r')))
            {
                var format = document.GetRange(run.Start, run.Start + run.Length).CharacterFormat;
                switch (run.Kind)
                {
                    case HighlightKind.Heading:
                    case HighlightKind.Bold:
                        format.Bold = FormatEffect.On;
                        break;
                    case HighlightKind.Italic:
                        format.Italic = FormatEffect.On;
                        break;
                    case HighlightKind.ListMarker:
                        format.ForegroundColor = dimmed;
                        break;
                    case HighlightKind.InlineCode:
                    case HighlightKind.CodeBlock:
                        format.Name = CodeFontName;
                        format.BackgroundColor = code;
                        break;
                    case HighlightKind.CodeFence:
                        format.Name = CodeFontName;
                        format.BackgroundColor = code;
                        format.ForegroundColor = dimmed;
                        break;
                }
            }
        }
        finally
        {
            document.EndUndoGroup();
            document.ApplyDisplayUpdates();
            _formatting = false;
        }
    }

    /// <summary>
    /// <paramref name="key"/>'s color in the theme dictionary of <paramref name="theme"/> (WinUI keeps Dark under
    /// "Default"), as SettingsWindow reads its caption colors.
    /// </summary>
    private static Color ThemeColor(string key, ElementTheme theme)
    {
        string[] names = theme == ElementTheme.Light ? ["Light"] : ["Dark", "Default"];
        if (FindThemeValue(Application.Current.Resources, key, names) is Color color) return color;
        return (Color)Application.Current.Resources[key];
    }

    private static object? FindThemeValue(ResourceDictionary dictionary, string key, string[] names)
    {
        foreach (var name in names)
        {
            if (dictionary.ThemeDictionaries.TryGetValue(name, out var found)
                && found is ResourceDictionary themed
                && themed.TryGetValue(key, out var value))
            {
                return value;
            }
        }
        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (FindThemeValue(merged, key, names) is { } value) return value;
        }
        return null;
    }

    /// <summary>
    /// <paramref name="top"/> blended over <paramref name="bottom"/>, opaque: RichEdit drops a color's alpha, and the
    /// Fluent text and fill colors are translucent.
    /// </summary>
    private static Color Over(Color top, Color bottom)
    {
        byte Mix(byte up, byte down) => (byte)Math.Round((up * top.A + down * (255 - top.A)) / 255.0);
        return Color.FromArgb(255, Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    // Command suggestions: the calling CLI's commands and skills for the token at the caret.

    private bool SuggestionsVisible => _suggestionToken is not null;

    private void OnEditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_formatting || _closed) return;
        RefreshSuggestions();
    }

    private void OnEditorCompositionStarted(RichEditBox sender, TextCompositionStartedEventArgs args)
    {
        _composing = true;
        HideSuggestions();
    }

    private void OnEditorCompositionEnded(RichEditBox sender, TextCompositionEndedEventArgs args)
    {
        _composing = false;
        RefreshSuggestions();
    }

    private void OnEditorSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SuggestionsVisible) PositionSuggestions();
    }

    /// <summary>A wheel scroll, or the editor following the caret, moves the caret's line: the list follows it.</summary>
    private void OnEditorViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (SuggestionsVisible) PositionSuggestions();
    }

    /// <summary>The token at the caret for the catalog's agent; null without a catalog or triggers, or with a selection.</summary>
    private CommandSuggestions.Token? CurrentToken()
    {
        if (_catalogAgent is null || _catalogItems.Count == 0) return null;
        var (triggers, slashOnlyAtStart) = CommandSuggestions.Triggers(_catalogAgent);
        if (triggers.Count == 0) return null;
        var selection = Editor.TextDocument.Selection;
        if (selection.StartPosition != selection.EndPosition) return null;
        // The editor's indices: "\r" and "\v" line breaks are whitespace to the token.
        return CommandSuggestions.ActiveToken(RawText(), selection.StartPosition, triggers, slashOnlyAtStart);
    }

    /// <summary>
    /// Shows the items whose name matches the token at the caret, or hides the list: no token, no match, an IME
    /// composition, or the token Esc closed the list for. The selection goes back to the first row when the token changes.
    /// </summary>
    private void RefreshSuggestions()
    {
        if (_closed) return;
        var token = _composing ? null : CurrentToken();
        if (token is null || token != _dismissedToken) _dismissedToken = null;
        if (token is not { } current || _dismissedToken is not null)
        {
            HideSuggestions();
            return;
        }
        var items = CommandSuggestions.Rank(_catalogItems, current.Trigger, current.Query);
        if (items.Count == 0)
        {
            HideSuggestions();
            return;
        }
        var rebuild = !SuggestionsVisible || !items.SequenceEqual(_suggestions);
        if (_suggestionToken != current) _suggestionIndex = 0;
        _suggestionToken = current;
        _suggestions = items;
        _suggestionIndex = Math.Clamp(_suggestionIndex, 0, items.Count - 1);
        if (rebuild) BuildSuggestionRows();
        else UpdateSuggestionSelection(scrollToTop: false);
        SuggestionPopup.Visibility = Visibility.Visible;
        PositionSuggestions();
    }

    /// <summary>One row per item: the trigger and name ("/review"), then the description, both plain text (TextBlock.Text
    /// is never markup).</summary>
    private void BuildSuggestionRows()
    {
        var highlightStyle = (Style)RootGrid.Resources["SuggestionHighlightStyle"];
        var pillStyle = (Style)RootGrid.Resources["SuggestionPillStyle"];
        var nameStyle = (Style)RootGrid.Resources["SuggestionNameStyle"];
        var descriptionStyle = (Style)RootGrid.Resources["SuggestionDescriptionStyle"];
        var rows = new List<Grid>(_suggestions.Count);
        for (var i = 0; i < _suggestions.Count; i++)
        {
            var item = _suggestions[i];
            // A transparent background, so the whole row takes the pointer.
            var row = new Grid
            {
                Height = SuggestionRowHeight,
                Background = new SolidColorBrush(Colors.Transparent),
                AllowFocusOnInteraction = false,
                IsTabStop = false,
                Tag = i,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var highlight = new Border { Style = highlightStyle };
            Grid.SetColumnSpan(highlight, 2);
            row.Children.Add(highlight);
            row.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Style = pillStyle });
            row.Children.Add(new TextBlock { Style = nameStyle, Text = item.Trigger + item.Name });
            var description = new TextBlock { Style = descriptionStyle, Text = item.Description };
            Grid.SetColumn(description, 1);
            row.Children.Add(description);
            AutomationProperties.SetName(row, item.Trigger + item.Name);
            row.PointerPressed += OnSuggestionRowPointerPressed;
            rows.Add(row);
        }
        _suggestionRows = rows;
        SuggestionScroll.Height = Math.Min(rows.Count, SuggestionVisibleRows) * SuggestionRowHeight;
        SuggestionList.ItemsSource = rows;
        UpdateSuggestionSelection(scrollToTop: true);
    }

    /// <summary>Highlights the selected row and scrolls the list to keep it in view.</summary>
    private void UpdateSuggestionSelection(bool scrollToTop)
    {
        for (var i = 0; i < _suggestionRows.Count; i++)
        {
            var visibility = i == _suggestionIndex ? Visibility.Visible : Visibility.Collapsed;
            _suggestionRows[i].Children[0].Visibility = visibility;
            _suggestionRows[i].Children[1].Visibility = visibility;
        }
        var visible = Math.Min(_suggestionRows.Count, SuggestionVisibleRows) * SuggestionRowHeight;
        var offset = scrollToTop ? 0 : SuggestionScroll.VerticalOffset;
        var top = _suggestionIndex * SuggestionRowHeight;
        if (top < offset) offset = top;
        else if (top + SuggestionRowHeight > offset + visible) offset = top + SuggestionRowHeight - visible;
        SuggestionScroll.ChangeView(null, offset, null, true);
    }

    /// <summary>Up and Down move the selection (stopping at the first and last row), Enter and Tab accept it; false: the
    /// key does its own thing.</summary>
    private bool HandleSuggestionKey(VirtualKey key)
    {
        if (key is VirtualKey.Up or VirtualKey.Down)
        {
            _suggestionIndex = Math.Clamp(_suggestionIndex + (key == VirtualKey.Down ? 1 : -1), 0, _suggestions.Count - 1);
            UpdateSuggestionSelection(scrollToTop: false);
            return true;
        }
        if (key is VirtualKey.Enter or VirtualKey.Tab) return TryAcceptSuggestion();
        return false;
    }

    /// <summary>
    /// The selected item replaces the token at the caret ("&lt;trigger&gt;&lt;name&gt; ") with the caret after it, through
    /// the same range edit as <see cref="ApplyListEdit"/>: one RichEdit undo step, so <see cref="StepHistory"/> undoes it
    /// in one go. False when the token changed since the list was shown (the list is refreshed instead).
    /// </summary>
    private bool TryAcceptSuggestion()
    {
        if (CurrentToken() is not { } token || token != _suggestionToken || _suggestionIndex >= _suggestions.Count)
        {
            RefreshSuggestions();
            return false;
        }
        var edit = CommandSuggestions.AcceptEdit(token, _suggestions[_suggestionIndex]);
        HideSuggestions();
        var document = Editor.TextDocument;
        var range = document.GetRange(edit.Start, edit.Start + edit.Length);
        range.SetText(TextSetOptions.None, edit.Text);
        document.Selection.SetRange(range.EndPosition, range.EndPosition);
        return true;
    }

    /// <summary>A press on a row accepts it; the editor keeps the keyboard focus.</summary>
    private void OnSuggestionRowPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid { Tag: int index } row || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (index < _suggestions.Count)
        {
            _suggestionIndex = index;
            TryAcceptSuggestion();
        }
        Editor.Focus(FocusState.Programmatic);
    }

    /// <summary>Esc: the list closes and stays closed for this token (a format pass or a caret event does not reopen it).</summary>
    private void DismissSuggestions()
    {
        _dismissedToken = _suggestionToken;
        HideSuggestions();
    }

    private void HideSuggestions()
    {
        if (_suggestionToken is null) return;
        _suggestionToken = null;
        SuggestionPopup.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The list under the caret's line (over it when there is less room below), its names lined up with the trigger,
    /// inside the editor. A text range's rect (<see cref="PointOptions.None"/>) is in DIPs from the top left of the
    /// editor's text inside its padding, unscrolled: as CommunityToolkit's RichSuggestBox reads it, adding the padding
    /// and taking off the editor's scroll offsets gives the editor's own coordinates.
    /// </summary>
    private void PositionSuggestions()
    {
        if (_suggestionToken is not { } token) return;
        var width = SuggestionCanvas.ActualWidth;
        var height = SuggestionCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var document = Editor.TextDocument;
        document.Selection.GetRect(PointOptions.None, out var caret, out _);
        document.GetRange(token.Start, token.Start).GetRect(PointOptions.None, out var start, out _);
        var padding = Editor.Padding;
        var border = Editor.BorderThickness;
        var origin = Editor.TransformToVisual(SuggestionCanvas).TransformPoint(new Windows.Foundation.Point(
            border.Left + padding.Left - (_editorScroll?.HorizontalOffset ?? 0),
            border.Top + padding.Top - (_editorScroll?.VerticalOffset ?? 0)));
        var lineTop = origin.Y + caret.Y;
        var lineBottom = lineTop + (caret.Height > 0 ? caret.Height : Editor.FontSize * 1.33);

        var popupWidth = Math.Min(SuggestionWidth, Math.Max(0, width - 2 * SuggestionEdgeMargin));
        var popupHeight = Math.Min(_suggestions.Count, SuggestionVisibleRows) * SuggestionRowHeight + SuggestionChrome;
        var roomBelow = height - lineBottom - SuggestionGap;
        var roomAbove = lineTop - SuggestionGap;
        var top = roomBelow >= popupHeight || roomBelow >= roomAbove
            ? lineBottom + SuggestionGap
            : lineTop - SuggestionGap - popupHeight;
        top = Math.Clamp(top, 0, Math.Max(0, height - popupHeight));
        var left = Math.Clamp(origin.X + start.X - SuggestionTextInset,
            SuggestionEdgeMargin, Math.Max(SuggestionEdgeMargin, width - popupWidth - SuggestionEdgeMargin));

        SuggestionPopup.Width = popupWidth;
        Canvas.SetLeft(SuggestionPopup, left);
        Canvas.SetTop(SuggestionPopup, top);
    }

    /// <summary>The first scroll viewer in <paramref name="parent"/>'s visual tree (a RichEditBox's template has one).</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    // Find: case-insensitive, wraps around, selects the match.

    private void OpenFind()
    {
        HideSuggestions();
        FindBar.Visibility = Visibility.Visible;
        FindStatus.Visibility = Visibility.Collapsed;
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
    }

    private void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        Editor.Focus(FocusState.Programmatic);
    }

    private void OnFindBoxPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || IsDown(VirtualKey.Control)) return;
        e.Handled = true;
        Find(forward: !IsDown(VirtualKey.Shift));
    }

    private void OnFindBoxTextChanged(object sender, TextChangedEventArgs e) => FindStatus.Visibility = Visibility.Collapsed;

    private void OnFindPreviousClick(object sender, RoutedEventArgs e) => Find(forward: false);

    private void OnFindNextClick(object sender, RoutedEventArgs e) => Find(forward: true);

    private void OnFindCloseClick(object sender, RoutedEventArgs e) => CloseFind();

    private void Find(bool forward)
    {
        var query = FindBox.Text.Replace("\r\n", "\r").Replace('\n', '\r');
        var text = RawText();
        if (query.Length == 0 || text.Length == 0) return;
        var selection = Editor.TextDocument.Selection;
        int index;
        if (forward)
        {
            var from = Math.Min(selection.EndPosition, text.Length);
            index = text.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
            if (index < 0) index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // A match that starts before the selection (it may end inside it).
            var last = selection.StartPosition + query.Length - 2;
            index = selection.StartPosition > 0
                ? text.LastIndexOf(query, Math.Min(last, text.Length - 1), StringComparison.OrdinalIgnoreCase)
                : -1;
            if (index < 0) index = text.LastIndexOf(query, StringComparison.OrdinalIgnoreCase);
        }
        FindStatus.Visibility = index < 0 ? Visibility.Visible : Visibility.Collapsed;
        if (index < 0) return;
        selection.SetRange(index, index + query.Length);
        selection.ScrollIntoView(PointOptions.None);
    }

    // Win32: the foreground-unlock lever (not in PopGuy.Platform's CsWin32 set).

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint hwnd, nint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
}
