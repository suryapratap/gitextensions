using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using GitCommands;
using GitCommands.Patches;
using GitCommands.Settings;
using GitExtUtils;
using GitExtUtils.GitUI;
using GitExtUtils.GitUI.Theming;
using GitUI.CommandsDialogs;
using GitUI.CommandsDialogs.SettingsDialog.Pages;
using GitUI.Editor.Diff;
using GitUI.Hotkey;
using GitUI.Properties;
using GitUIPluginInterfaces;
using JetBrains.Annotations;
using ResourceManager;

namespace GitUI.Editor
{
    [DefaultEvent("SelectedLineChanged")]
    public partial class FileViewer : GitModuleControl
    {
        /// <summary>
        /// Raised when the Escape key is pressed (and only when no selection exists, as the default behaviour of escape is to clear the selection).
        /// </summary>
        public event Action EscapePressed;

        /// <summary>
        /// The type of information currently shown in the file viewer
        /// </summary>
        private enum ViewMode
        {
            // Plain text
            Text,

            // Diff or patch
            Diff,

            // Diffs that will not be affected by diff arguments like white space etc (limited options)
            FixedDiff,

            // range-diff output
            RangeDiff,

            // Image viewer
            Image
        }

        private readonly TranslationString _largeFileSizeWarning = new TranslationString("This file is {0:N1} MB. Showing large files can be slow. Click to show anyway.");
        private readonly TranslationString _cannotViewImage = new TranslationString("Cannot view image {0}");

        public event EventHandler<SelectedLineEventArgs> SelectedLineChanged;
        public event EventHandler HScrollPositionChanged;
        public event EventHandler VScrollPositionChanged;
        public event EventHandler BottomScrollReached;
        public event EventHandler TopScrollReached;
        public event EventHandler RequestDiffView;
        public new event EventHandler TextChanged;
        public event EventHandler TextLoaded;
        public event CancelEventHandler ContextMenuOpening;
        public event EventHandler<EventArgs> ExtraDiffArgumentsChanged;

        private readonly AsyncLoader _async;
        private readonly IFullPathResolver _fullPathResolver;
        private ViewMode _viewMode;
        private Encoding _encoding;
        private Func<Task> _deferShowFunc;
        private readonly ContinuousScrollEventManager _continuousScrollEventManager;

        private static string[] _rangeDiffFullPrefixes = { "      ", "    ++", "    + ", "     +", "    --", "    - ", "     -", "    +-", "    -+", "    " };
        private static string[] _combinedDiffFullPrefixes = { "  ", "++", "+ ", " +", "--", "- ", " -" };
        private static string[] _normalDiffFullPrefixes = { " ", "+", "-" };

        private static string[] _rangeDiffPrefixes = { "    " };
        private static string[] _combinedDiffPrefixes = { "+", "-", " +", " -" };
        private static string[] _normalDiffPrefixes = { "+", "-" };

        public FileViewer()
        {
            TreatAllFilesAsText = false;
            ShowEntireFile = false;
            NumberOfContextLines = AppSettings.NumberOfContextLines;
            InitializeComponent();
            InitializeComplete();

            UICommandsSourceSet += OnUICommandsSourceSet;

            internalFileViewer.MouseEnter += (_, e) => OnMouseEnter(e);
            internalFileViewer.MouseLeave += (_, e) => OnMouseLeave(e);
            internalFileViewer.MouseMove += (_, e) => OnMouseMove(e);
            internalFileViewer.KeyUp += (_, e) => OnKeyUp(e);
            internalFileViewer.EscapePressed += () => EscapePressed?.Invoke();

            _continuousScrollEventManager = new ContinuousScrollEventManager();
            _continuousScrollEventManager.BottomScrollReached += _continuousScrollEventManager_BottomScrollReached;
            _continuousScrollEventManager.TopScrollReached += _continuousScrollEventManager_TopScrollReached;

            PictureBox.MouseWheel += PictureBox_MouseWheel;
            internalFileViewer.SetContinuousScrollManager(_continuousScrollEventManager);

            _async = new AsyncLoader();
            _async.LoadingError +=
                (_, e) =>
                {
                    if (!IsDisposed)
                    {
                        ResetView(ViewMode.Text, null);
                        internalFileViewer.SetText("Unsupported file: \n\n" + e.Exception.ToString(), openWithDifftool: null /* not applicable */);
                        TextLoaded?.Invoke(this, null);
                    }
                };

            IgnoreWhitespace = AppSettings.IgnoreWhitespaceKind;
            OnIgnoreWhitespaceChanged();

            ignoreWhitespaceAtEol.Image = Images.WhitespaceIgnoreEol.AdaptLightness();
            ignoreWhitespaceAtEolToolStripMenuItem.Image = ignoreWhitespaceAtEol.Image;

            ignoreWhiteSpaces.Image = Images.WhitespaceIgnore.AdaptLightness();
            ignoreWhitespaceChangesToolStripMenuItem.Image = ignoreWhiteSpaces.Image;

            ignoreAllWhitespaces.Image = Images.WhitespaceIgnoreAll.AdaptLightness();
            ignoreAllWhitespaceChangesToolStripMenuItem.Image = ignoreAllWhitespaces.Image;

            ShowEntireFile = AppSettings.ShowEntireFile;
            showEntireFileButton.Checked = ShowEntireFile;
            showEntireFileToolStripMenuItem.Checked = ShowEntireFile;
            SetStateOfContextLinesButtons();

            automaticContinuousScrollToolStripMenuItem.Image = Images.UiScrollBar.AdaptLightness();
            automaticContinuousScrollToolStripMenuItem.Checked = AppSettings.AutomaticContinuousScroll;

            showNonPrintChars.Image = Images.ShowWhitespace.AdaptLightness();
            showNonprintableCharactersToolStripMenuItem.Image = showNonPrintChars.Image;
            showNonPrintChars.Checked = AppSettings.ShowNonPrintingChars;
            showNonprintableCharactersToolStripMenuItem.Checked = AppSettings.ShowNonPrintingChars;
            ToggleNonPrintingChars(AppSettings.ShowNonPrintingChars);

            ShowSyntaxHighlightingInDiff = AppSettings.ShowSyntaxHighlightingInDiff;
            showSyntaxHighlighting.Image = Resources.SyntaxHighlighting.AdaptLightness();
            showSyntaxHighlighting.Checked = ShowSyntaxHighlightingInDiff;
            automaticContinuousScrollToolStripMenuItem.Text = Strings.ContScrollToNextFileOnlyWithAlt;

            IsReadOnly = true;

            internalFileViewer.MouseMove += (_, e) =>
            {
                if (IsDiffView(_viewMode) && !fileviewerToolbar.Visible)
                {
                    fileviewerToolbar.Visible = true;
                    fileviewerToolbar.Location = new Point(Width - fileviewerToolbar.Width - 40, 0);
                    fileviewerToolbar.BringToFront();
                }
            };
            internalFileViewer.MouseLeave += (_, e) =>
            {
                if (GetChildAtPoint(PointToClient(MousePosition)) != fileviewerToolbar &&
                    fileviewerToolbar != null)
                {
                    fileviewerToolbar.Visible = false;
                }
            };
            internalFileViewer.TextChanged += (sender, e) =>
            {
                if (IsDiffView(_viewMode))
                {
                    internalFileViewer.AddPatchHighlighting();
                }

                TextChanged?.Invoke(sender, e);
            };
            internalFileViewer.HScrollPositionChanged += (sender, e) => HScrollPositionChanged?.Invoke(sender, e);
            internalFileViewer.VScrollPositionChanged += (sender, e) => VScrollPositionChanged?.Invoke(sender, e);
            internalFileViewer.SelectedLineChanged += (sender, e) => SelectedLineChanged?.Invoke(sender, e);
            internalFileViewer.DoubleClick += (_, args) => RequestDiffView?.Invoke(this, EventArgs.Empty);

            HotkeysEnabled = true;

            if (!IsDesignModeActive && ContextMenuStrip is null)
            {
                ContextMenuStrip = contextMenu;
            }

            contextMenu.Opening += (sender, e) =>
            {
                copyToolStripMenuItem.Enabled = internalFileViewer.GetSelectionLength() > 0;
                ContextMenuOpening?.Invoke(sender, e);
            };

            _fullPathResolver = new FullPathResolver(() => Module.WorkingDir);
        }

        // Public properties

        [Browsable(false)]
        public byte[] FilePreamble { get; private set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public new Font Font
        {
            get => internalFileViewer.Font;
            set => internalFileViewer.Font = value;
        }

        [DefaultValue(true)]
        [Category("Behavior")]
        public bool IsReadOnly
        {
            get => internalFileViewer.IsReadOnly;
            set => internalFileViewer.IsReadOnly = value;
        }

        [DefaultValue(null)]
        [Description("If true line numbers are shown in the textarea")]
        [Category("Appearance")]
        public bool? ShowLineNumbers
        {
            get => internalFileViewer.ShowLineNumbers;
            set => internalFileViewer.ShowLineNumbers = value;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public Encoding Encoding
        {
            get
            {
                if (_encoding is null)
                {
                    _encoding = Module.FilesEncoding;
                }

                return _encoding;
            }
            set
            {
                _encoding = value;

                this.InvokeAsync(() =>
                {
                    if (_encoding != null)
                    {
                        encodingToolStripComboBox.Text = _encoding.EncodingName;
                    }
                    else
                    {
                        encodingToolStripComboBox.SelectedIndex = -1;
                    }
                }).FileAndForget();
            }
        }

        public void ScrollToTop()
        {
            internalFileViewer.ScrollToTop();
        }

        public void ScrollToBottom()
        {
            internalFileViewer.ScrollToBottom();
        }

        [DefaultValue(0)]
        [Browsable(false)]
        public int HScrollPosition
        {
            get => internalFileViewer.HScrollPosition;
            set => internalFileViewer.HScrollPosition = value;
        }

        [DefaultValue(0)]
        [Browsable(false)]
        public int VScrollPosition
        {
            get => internalFileViewer.VScrollPosition;
            set => internalFileViewer.VScrollPosition = value;
        }

        // Private properties

        [Description("Sets what kind of whitespace changes shall be ignored in diffs")]
        [DefaultValue(IgnoreWhitespaceKind.None)]
        private IgnoreWhitespaceKind IgnoreWhitespace { get; set; }

        [Description("Show diffs with <n> lines of context.")]
        [DefaultValue(3)]
        private int NumberOfContextLines { get; set; }

        [Description("Show diffs with entire file.")]
        [DefaultValue(false)]
        private bool ShowEntireFile { get; set; }

        [Description("Treat all files as text.")]
        [DefaultValue(false)]
        private bool TreatAllFilesAsText { get; set; }

        [Description("Show syntax highlighting in diffs.")]
        [DefaultValue(true)]
        private bool ShowSyntaxHighlightingInDiff { get; set; }

        // Public methods

        public void SetGitBlameGutter(IEnumerable<GitBlameEntry> gitBlameEntries)
        {
            internalFileViewer.ShowGutterAvatars = AppSettings.BlameShowAuthorAvatar;

            if (AppSettings.BlameShowAuthorAvatar)
            {
                internalFileViewer.SetGitBlameGutter(gitBlameEntries);
            }
        }

        public void ReloadHotkeys()
        {
            Hotkeys = HotkeySettingsManager.LoadHotkeys(HotkeySettingsName);
        }

        public ToolStripSeparator AddContextMenuSeparator()
        {
            var separator = new ToolStripSeparator();
            contextMenu.Items.Add(separator);
            return separator;
        }

        public ToolStripMenuItem AddContextMenuEntry(string text, EventHandler toolStripItem_Click)
        {
            var toolStripItem = new ToolStripMenuItem(text);
            contextMenu.Items.Add(toolStripItem);
            toolStripItem.Click += toolStripItem_Click;
            return toolStripItem;
        }

        public void EnableScrollBars(bool enable)
        {
            internalFileViewer.EnableScrollBars(enable);
        }

        public ArgumentString GetExtraDiffArguments(bool isRangeDiff = false)
        {
            return new ArgumentBuilder
            {
                { IgnoreWhitespace == IgnoreWhitespaceKind.AllSpace, "--ignore-all-space" },
                { IgnoreWhitespace == IgnoreWhitespaceKind.Change, "--ignore-space-change" },
                { IgnoreWhitespace == IgnoreWhitespaceKind.Eol, "--ignore-space-at-eol" },
                { ShowEntireFile, "--inter-hunk-context=9000 --unified=9000", $"--unified={NumberOfContextLines}" },

                // Handle zero context as showing no file changes, to get the summary only
                { isRangeDiff && NumberOfContextLines == 0, "--no-patch " },
                { TreatAllFilesAsText, "--text" }
            };
        }

        public string GetSelectedText()
        {
            return internalFileViewer.GetSelectedText();
        }

        public int GetSelectionPosition()
        {
            return internalFileViewer.GetSelectionPosition();
        }

        public int GetSelectionLength()
        {
            return internalFileViewer.GetSelectionLength();
        }

        public void GoToLine(int line)
        {
            internalFileViewer.GoToLine(line);
        }

        public int GetLineFromVisualPosY(int visualPosY)
        {
            return internalFileViewer.GetLineFromVisualPosY(visualPosY);
        }

        public void HighlightLines(int startLine, int endLine, Color color)
        {
            internalFileViewer.HighlightLines(startLine, endLine, color);
        }

        public void ClearHighlighting()
        {
            internalFileViewer.ClearHighlighting();
        }

        public string GetText() => internalFileViewer.GetText();

        public void ViewCurrentChanges(GitItemStatus item, bool isStaged, [CanBeNull] Action openWithDifftool)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(
                async () =>
                {
                    if (item?.IsStatusOnly ?? false)
                    {
                        // Present error (e.g. parsing Git)
                        await ViewTextAsync(item.Name, item.ErrorMessage);
                        return;
                    }

                    if (item.IsSubmodule)
                    {
                        var getStatusTask = item.GetSubmoduleStatusAsync();
                        if (getStatusTask != null)
                        {
                            var status = await getStatusTask;
                            if (status is null)
                            {
                                await ViewTextAsync(item.Name, $"Submodule \"{item.Name}\" has unresolved conflicts");
                                return;
                            }

                            await ViewTextAsync(item.Name, LocalizationHelpers.ProcessSubmoduleStatus(Module, status));
                            return;
                        }

                        var changes = await Module.GetCurrentChangesAsync(item.Name, item.OldName, isStaged,
                            GetExtraDiffArguments(), Encoding);
                        var text = LocalizationHelpers.ProcessSubmodulePatch(Module, item.Name, changes);
                        await ViewTextAsync(item.Name, text);
                        return;
                    }

                    if (!item.IsTracked || item.IsNew)
                    {
                        var id = isStaged ? ObjectId.IndexId : ObjectId.WorkTreeId;
                        await ViewGitItemRevisionAsync(item, id, openWithDifftool);
                    }
                    else
                    {
                        var patch = await Module.GetCurrentChangesAsync(
                            item.Name, item.OldName, isStaged, GetExtraDiffArguments(), Encoding);
                        await ViewPatchAsync(item.Name, patch?.Text ?? "", openWithDifftool);
                    }

                    SetVisibilityDiffContextMenuStaging();
                });
        }

        /// <summary>
        /// Present the text as a patch in the file viewer
        /// </summary>
        /// <param name="fileName">The fileName to present</param>
        /// <param name="text">The patch text</param>
        /// <param name="openWithDifftool">The action to open the difftool</param>
        public void ViewPatch([CanBeNull] string fileName,
            [NotNull] string text,
            [CanBeNull] Action openWithDifftool = null)
        {
            ThreadHelper.JoinableTaskFactory.Run(
                () => ViewPatchAsync(fileName, text, openWithDifftool));
        }

        public async Task ViewPatchAsync(string fileName, string text, Action openWithDifftool)
            => await ViewPrivateAsync(fileName, text, openWithDifftool, ViewMode.Diff);

        /// <summary>
        /// Present the text as a patch in the file viewer, for GitHub
        /// </summary>
        /// <param name="fileName">The fileName to present</param>
        /// <param name="text">The patch text</param>
        /// <param name="openWithDifftool">The action to open the difftool</param>
        public void ViewFixedPatch([CanBeNull] string fileName,
            [NotNull] string text,
            [CanBeNull] Action openWithDifftool = null)
        {
            ThreadHelper.JoinableTaskFactory.Run(
                () => ViewPrivateAsync(fileName, text, openWithDifftool, ViewMode.FixedDiff));
        }

        public async Task ViewFixedPatchAsync(string fileName, string text, Action openWithDifftool = null)
            => await ViewPrivateAsync(fileName, text, openWithDifftool, ViewMode.FixedDiff);

        public async Task ViewRangeDiffAsync(string fileName, string text)
            => await ViewPrivateAsync(fileName, text, openWithDifftool: null, ViewMode.RangeDiff);

        public void ViewText([CanBeNull] string fileName,
            [NotNull] string text,
            [CanBeNull] Action openWithDifftool = null)
        {
            ThreadHelper.JoinableTaskFactory.Run(
                () => ViewTextAsync(fileName, text, openWithDifftool));
        }

        public async Task ViewTextAsync([CanBeNull] string fileName, [NotNull] string text,
            [CanBeNull] Action openWithDifftool = null, bool checkGitAttributes = false)
        {
            await ShowOrDeferAsync(
                text.Length,
                () =>
                {
                    ResetView(ViewMode.Text, fileName);

                    // Check for binary file. Using gitattributes could be misleading for a changed file,
                    // but not much other can be done
                    bool isBinary = (checkGitAttributes && FileHelper.IsBinaryFileName(Module, fileName))
                                    || FileHelper.IsBinaryFileAccordingToContent(text);

                    if (isBinary)
                    {
                        try
                        {
                            var summary = new StringBuilder()
                                .AppendLine("Binary file:")
                                .AppendLine()
                                .AppendLine(fileName)
                                .AppendLine()
                                .AppendLine($"{text.Length:N0} bytes:")
                                .AppendLine();
                            internalFileViewer.SetText(summary.ToString(), openWithDifftool);

                            ToHexDump(Encoding.ASCII.GetBytes(text), summary);
                            internalFileViewer.SetText(summary.ToString(), openWithDifftool);
                        }
                        catch
                        {
                            internalFileViewer.SetText($"Binary file: {fileName} (Detected)", openWithDifftool);
                        }
                    }
                    else
                    {
                        internalFileViewer.SetText(text, openWithDifftool);
                    }

                    TextLoaded?.Invoke(this, null);
                    return Task.CompletedTask;
                });
        }

        public Task ViewGitItemRevisionAsync(GitItemStatus file, ObjectId revision, [CanBeNull] Action openWithDifftool = null)
        {
            if (revision == ObjectId.WorkTreeId)
            {
                // No blob exists for worktree, present contents from file system
                return ViewFileAsync(file.Name, file.IsSubmodule, openWithDifftool);
            }

            if (file.TreeGuid is null)
            {
                file.TreeGuid = Module.GetFileBlobHash(file.Name, revision);
            }

            return ViewGitItemAsync(file, openWithDifftool);
        }

        /// <summary>
        /// View the git item with the TreeGuid
        /// </summary>
        /// <param name="file">GitItem file, with TreeGuid</param>
        /// <param name="openWithDifftool">difftool command</param>
        /// <returns>Task to view the item</returns>
        public Task ViewGitItemAsync(GitItemStatus file, [CanBeNull] Action openWithDifftool = null)
        {
            var sha = file.TreeGuid?.ToString();
            var isSubmodule = file.IsSubmodule;

            if (!isSubmodule && file.IsNew && file.Staged == StagedStatus.Index)
            {
                // File system access for other than Worktree,
                // to handle that git-status does not detect details for untracked (git-diff --no-index will not give info)
                var fullPath = PathUtil.Combine(Module.WorkingDir, file.Name);
                if (Directory.Exists(fullPath) && GitModule.IsValidGitWorkingDir(fullPath))
                {
                    isSubmodule = true;
                }
            }

            return ViewItemAsync(
                file.Name,
                isSubmodule,
                getImage: GetImage,
                getFileText: GetFileTextIfBlobExists,
                getSubmoduleText: () => LocalizationHelpers.GetSubmoduleText(Module, file.Name.TrimEnd('/'), sha),
                openWithDifftool: openWithDifftool);

            string GetFileTextIfBlobExists()
            {
                FilePreamble = new byte[] { };
                return file.TreeGuid != null ? Module.GetFileText(file.TreeGuid, Encoding) : string.Empty;
            }

            Image GetImage()
            {
                try
                {
                    using (var stream = Module.GetFileStream(sha))
                    {
                        return CreateImage(file.Name, stream);
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Get contents in the file system async if not too big, otherwise ask user
        /// </summary>
        /// <param name="fileName">The file/submodule path</param>
        /// <param name="isSubmodule">If submodule</param>
        /// <param name="openWithDifftool">Diff action</param>
        /// <returns>Task</returns>
        public Task ViewFileAsync(string fileName, bool isSubmodule = false, [CanBeNull] Action openWithDifftool = null)
        {
            string fullPath = _fullPathResolver.Resolve(fileName);

            if (isSubmodule && !GitModule.IsValidGitWorkingDir(fullPath))
            {
                return ViewTextAsync(fileName, "Invalid submodule: " + fileName);
            }

            if (!isSubmodule && (fileName.EndsWith("/") || Directory.Exists(fullPath)))
            {
                if (!GitModule.IsValidGitWorkingDir(fullPath))
                {
                    return ViewTextAsync(fileName, "Directory: " + fileName);
                }

                isSubmodule = true;
            }

            return ShowOrDeferAsync(
                fileName,
                () => ViewItemAsync(
                    fileName,
                    isSubmodule,
                    getImage: GetImage,
                    getFileText: GetFileText,
                    getSubmoduleText: () => LocalizationHelpers.GetSubmoduleText(Module, fileName.TrimEnd('/'), ""),
                    openWithDifftool));

            Image GetImage()
            {
                try
                {
                    var path = _fullPathResolver.Resolve(fileName);

                    if (!File.Exists(path))
                    {
                        return null;
                    }

                    using (var stream = File.OpenRead(path))
                    {
                        return CreateImage(fileName, stream);
                    }
                }
                catch
                {
                    return null;
                }
            }

            string GetFileText()
            {
                var path = File.Exists(fileName)
                    ? fileName
                    : _fullPathResolver.Resolve(fileName);

                if (!File.Exists(path))
                {
                    return $"File {path} does not exist";
                }

                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Module.FilesEncoding))
                {
#pragma warning disable VSTHRD103 // Call async methods when in an async method
                    var content = reader.ReadToEnd();
#pragma warning restore VSTHRD103 // Call async methods when in an async method
                    FilePreamble = reader.CurrentEncoding.GetPreamble();
                    return content;
                }
            }
        }

        public void Clear()
        {
            ThreadHelper.JoinableTaskFactory.Run(() => ViewTextAsync("", ""));
        }

        public bool HasAnyPatches()
        {
            return internalFileViewer.GetText() != null && internalFileViewer.GetText().Contains("@@");
        }

        public void SetFileLoader(GetNextFileFnc fileLoader)
        {
            internalFileViewer.SetFileLoader(fileLoader);
        }

        public void CherryPickAllChanges()
        {
            if (GetText().Length > 0)
            {
                applySelectedLines(0, GetText().Length, reverse: false);
            }
        }

        // Protected

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UICommandsSourceSet -= OnUICommandsSourceSet;
                _async.Dispose();
                components?.Dispose();

                if (TryGetUICommands(out var uiCommands))
                {
                    uiCommands.PostSettings -= UICommands_PostSettings;
                }
            }

            base.Dispose(disposing);
        }

        protected override void DisposeUICommandsSource()
        {
            UICommandsSource.UICommandsChanged -= OnUICommandsChanged;
            base.DisposeUICommandsSource();
        }

        protected override void OnRuntimeLoad()
        {
            ReloadHotkeys();
            Font = AppSettings.FixedWidthFont;

            DetectDefaultEncoding();
            return;

            void DetectDefaultEncoding()
            {
                var encodings = AppSettings.AvailableEncodings.Values.Select(e => e.EncodingName).ToArray();
                encodingToolStripComboBox.Items.AddRange(encodings);
                encodingToolStripComboBox.ResizeDropDownWidth(50, 250);

                var defaultEncodingName = Encoding.Default.EncodingName;

                for (int i = 0; i < encodings.Length; i++)
                {
                    if (string.Equals(encodings[i], defaultEncodingName, StringComparison.OrdinalIgnoreCase))
                    {
                        encodingToolStripComboBox.Items[i] = "Default (" + Encoding.Default.HeaderName + ")";
                        break;
                    }
                }
            }
        }

        // Private methods

        private static bool IsDiffView(ViewMode viewMode)
        {
            return viewMode == ViewMode.Diff || viewMode == ViewMode.FixedDiff || viewMode == ViewMode.RangeDiff;
        }

        private async Task ViewPrivateAsync(string fileName, string text, Action openWithDifftool, ViewMode viewMode = ViewMode.Diff)
        {
            await ShowOrDeferAsync(
                text.Length,
                () =>
                {
                    ResetView(viewMode, fileName);
                    internalFileViewer.SetText(text, openWithDifftool, isDiff: IsDiffView(_viewMode), isRangeDiff: _viewMode == ViewMode.RangeDiff);

                    TextLoaded?.Invoke(this, null);
                    return Task.CompletedTask;
                });
        }

        private void CopyNotStartingWith(char startChar)
        {
            string code = internalFileViewer.GetSelectedText();
            bool noSelection = false;

            if (string.IsNullOrEmpty(code))
            {
                code = internalFileViewer.GetText();
                noSelection = true;
            }

            if (IsDiffView(_viewMode))
            {
                // add artificial space if selected text is not starting from line beginning, it will be removed later
                int pos = noSelection ? 0 : internalFileViewer.GetSelectionPosition();
                string fileText = internalFileViewer.GetText();

                if (pos > 0 && fileText[pos - 1] != '\n')
                {
                    code = " " + code;
                }

                var lines = code.Split('\n')
                    .Where(s => s.Length == 0 || s[0] != startChar || (s.Length > 2 && s[1] == s[0] && s[2] == s[0]));
                var hpos = fileText.IndexOf("\n@@");

                // if header is selected then don't remove diff extra chars
                if (hpos <= pos)
                {
                    char[] specials = { ' ', '-', '+' };
                    lines = lines.Select(s => s.Length > 0 && specials.Any(c => c == s[0]) ? s.Substring(1) : s);
                }

                code = string.Join("\n", lines);
            }

            ClipboardUtil.TrySetText(code.AdjustLineEndings(Module.EffectiveConfigFile.core.autocrlf.Value));
        }

        private void SetVisibilityDiffContextMenu(ViewMode viewMode, [CanBeNull] string fileName)
        {
            bool changePhysicalFile = (viewMode == ViewMode.Diff || viewMode == ViewMode.FixedDiff)
                                      && !Module.IsBareRepository()
                                      && File.Exists(_fullPathResolver.Resolve(fileName));

            cherrypickSelectedLinesToolStripMenuItem.Visible = changePhysicalFile;
            revertSelectedLinesToolStripMenuItem.Visible = changePhysicalFile;

            // RangeDiff patch is undefined, could be new/old commit or to parents
            copyPatchToolStripMenuItem.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.FixedDiff;
            copyNewVersionToolStripMenuItem.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.FixedDiff;
            copyOldVersionToolStripMenuItem.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.FixedDiff;

            ignoreWhitespaceAtEolToolStripMenuItem.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.RangeDiff;
            ignoreWhitespaceChangesToolStripMenuItem.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.RangeDiff;
            ignoreAllWhitespaceChangesToolStripMenuItem.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.RangeDiff;
            increaseNumberOfLinesToolStripMenuItem.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.RangeDiff;
            decreaseNumberOfLinesToolStripMenuItem.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.RangeDiff;
            showEntireFileToolStripMenuItem.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.RangeDiff;
            toolStripSeparator2.Visible = IsDiffView(viewMode);
            treatAllFilesAsTextToolStripMenuItem.Visible = IsDiffView(viewMode);

            // toolbar
            nextChangeButton.Visible = IsDiffView(viewMode);
            previousChangeButton.Visible = IsDiffView(viewMode);
            increaseNumberOfLines.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.RangeDiff;
            decreaseNumberOfLines.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.RangeDiff;
            showEntireFileButton.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.RangeDiff;
            showSyntaxHighlighting.Visible = IsDiffView(viewMode);
            ignoreWhitespaceAtEol.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.RangeDiff;
            ignoreWhiteSpaces.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.RangeDiff;
            ignoreAllWhitespaces.Visible = viewMode == ViewMode.Diff || viewMode == ViewMode.RangeDiff;
        }

        private void SetVisibilityDiffContextMenuStaging()
        {
            cherrypickSelectedLinesToolStripMenuItem.Visible = false;
            revertSelectedLinesToolStripMenuItem.Visible = false;
        }

        private void OnExtraDiffArgumentsChanged()
        {
            ExtraDiffArgumentsChanged?.Invoke(this, EventArgs.Empty);
        }

        private Task ShowOrDeferAsync(string fileName, Func<Task> showFunc)
        {
            return ShowOrDeferAsync(GetFileLength(), showFunc);

            long GetFileLength()
            {
                try
                {
                    var resolvedPath = _fullPathResolver.Resolve(fileName);

                    if (File.Exists(resolvedPath))
                    {
                        var file = new FileInfo(resolvedPath);
                        return file.Length;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"{ex.Message}{Environment.NewLine}{fileName}", Strings.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                // If the file does not exist, it doesn't matter what size we
                // return as nothing will be shown anyway.
                return 0;
            }
        }

        private Task ShowOrDeferAsync(long contentLength, Func<Task> showFunc)
        {
            const long maxLength = 5 * 1024 * 1024;

            if (contentLength > maxLength)
            {
                Clear();
                Refresh();
                _NO_TRANSLATE_lblShowPreview.Text = string.Format(_largeFileSizeWarning.Text, contentLength / (1024d * 1024));
                _NO_TRANSLATE_lblShowPreview.Show();
                _deferShowFunc = showFunc;
                return Task.CompletedTask;
            }
            else
            {
                _NO_TRANSLATE_lblShowPreview.Hide();
                _deferShowFunc = null;
                return showFunc();
            }
        }

        [NotNull]
        private static Image CreateImage([NotNull] string fileName, [NotNull] Stream stream)
        {
            if (IsIcon())
            {
                using (var icon = new Icon(stream))
                {
                    return icon.ToBitmap();
                }
            }

            return new Bitmap(CopyStream());

            bool IsIcon()
            {
                return fileName.EndsWith(".ico", StringComparison.CurrentCultureIgnoreCase);
            }

            MemoryStream CopyStream()
            {
                var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy;
            }
        }

        private void OnIgnoreWhitespaceChanged()
        {
            switch (IgnoreWhitespace)
            {
                case IgnoreWhitespaceKind.None:
                    ignoreWhitespaceAtEol.Checked = false;
                    ignoreWhitespaceAtEolToolStripMenuItem.Checked = ignoreWhitespaceAtEol.Checked;
                    ignoreWhiteSpaces.Checked = false;
                    ignoreWhitespaceChangesToolStripMenuItem.Checked = ignoreWhiteSpaces.Checked;
                    ignoreAllWhitespaces.Checked = false;
                    ignoreAllWhitespaceChangesToolStripMenuItem.Checked = ignoreAllWhitespaces.Checked;
                    break;
                case IgnoreWhitespaceKind.Eol:
                    ignoreWhitespaceAtEol.Checked = true;
                    ignoreWhitespaceAtEolToolStripMenuItem.Checked = ignoreWhitespaceAtEol.Checked;
                    ignoreWhiteSpaces.Checked = false;
                    ignoreWhitespaceChangesToolStripMenuItem.Checked = ignoreWhiteSpaces.Checked;
                    ignoreAllWhitespaces.Checked = false;
                    ignoreAllWhitespaceChangesToolStripMenuItem.Checked = ignoreAllWhitespaces.Checked;
                    break;
                case IgnoreWhitespaceKind.Change:
                    ignoreWhitespaceAtEol.Checked = true;
                    ignoreWhitespaceAtEolToolStripMenuItem.Checked = ignoreWhitespaceAtEol.Checked;
                    ignoreWhiteSpaces.Checked = true;
                    ignoreWhitespaceChangesToolStripMenuItem.Checked = ignoreWhiteSpaces.Checked;
                    ignoreAllWhitespaces.Checked = false;
                    ignoreAllWhitespaceChangesToolStripMenuItem.Checked = ignoreAllWhitespaces.Checked;
                    break;
                case IgnoreWhitespaceKind.AllSpace:
                    ignoreWhitespaceAtEol.Checked = true;
                    ignoreWhitespaceAtEolToolStripMenuItem.Checked = ignoreWhitespaceAtEol.Checked;
                    ignoreWhiteSpaces.Checked = true;
                    ignoreWhitespaceChangesToolStripMenuItem.Checked = ignoreWhiteSpaces.Checked;
                    ignoreAllWhitespaces.Checked = true;
                    ignoreAllWhitespaceChangesToolStripMenuItem.Checked = ignoreAllWhitespaces.Checked;
                    break;
                default:
                    throw new NotSupportedException("Unsupported value for IgnoreWhitespaceKind: " + IgnoreWhitespace);
            }

            AppSettings.IgnoreWhitespaceKind = IgnoreWhitespace;
        }

        private void ResetView(ViewMode viewMode, [CanBeNull] string fileName)
        {
            _viewMode = viewMode;
            if (_viewMode == ViewMode.Text
                && !string.IsNullOrEmpty(fileName)
                && (fileName.EndsWith(".diff", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".patch", StringComparison.OrdinalIgnoreCase)))
            {
                _viewMode = ViewMode.FixedDiff;
            }

            SetVisibilityDiffContextMenu(_viewMode, fileName);
            ClearImage();
            PictureBox.Visible = _viewMode == ViewMode.Image;
            internalFileViewer.Visible = _viewMode != ViewMode.Image;

            if (((ShowSyntaxHighlightingInDiff && IsDiffView(_viewMode)) || _viewMode == ViewMode.Text) && fileName != null)
            {
                internalFileViewer.SetHighlightingForFile(fileName);
            }
            else
            {
                internalFileViewer.SetHighlighting("");
            }

            return;

            void ClearImage()
            {
                PictureBox.ImageLocation = "";

                if (PictureBox.Image is null)
                {
                    return;
                }

                PictureBox.Image.Dispose();
                PictureBox.Image = null;
            }
        }

        private Task ViewItemAsync(string fileName, bool isSubmodule, Func<Image> getImage, Func<string> getFileText, Func<string> getSubmoduleText, [CanBeNull] Action openWithDifftool)
        {
            FilePreamble = null;

            if (isSubmodule)
            {
                return _async.LoadAsync(
                    getSubmoduleText,
                    text => ThreadHelper.JoinableTaskFactory.Run(
                        () => ViewTextAsync(fileName, text, openWithDifftool)));
            }
            else if (FileHelper.IsImage(fileName))
            {
                return _async.LoadAsync(getImage,
                            image =>
                            {
                                if (image is null)
                                {
                                    ResetView(ViewMode.Text, null);
                                    internalFileViewer.SetText(string.Format(_cannotViewImage.Text, fileName), openWithDifftool);
                                    return;
                                }

                                ResetView(ViewMode.Image, fileName);
                                var size = DpiUtil.Scale(image.Size);
                                if (size.Height > PictureBox.Size.Height || size.Width > PictureBox.Size.Width)
                                {
                                    PictureBox.SizeMode = PictureBoxSizeMode.Zoom;
                                }
                                else
                                {
                                    PictureBox.SizeMode = PictureBoxSizeMode.CenterImage;
                                }

                                PictureBox.Image = DpiUtil.Scale(image);
                                internalFileViewer.SetText("", openWithDifftool);
                            });
            }
            else
            {
                return _async.LoadAsync(
                    getFileText,
                    text => ThreadHelper.JoinableTaskFactory.Run(
                        () => ViewTextAsync(fileName, text, openWithDifftool, checkGitAttributes: true)));
            }
        }

        private static string ToHexDump(byte[] bytes, StringBuilder str, int columnWidth = 8, int columnCount = 2)
        {
            if (bytes.Length == 0)
            {
                return "";
            }

            // Do not freeze GE when selecting large binary files
            // Show only the header of the binary file to indicate contents and files incorrectly handled
            // Use a dedicated editor to view the complete file
            var limit = Math.Min(bytes.Length, columnWidth * columnCount * 256);
            var i = 0;

            while (i < limit)
            {
                var baseIndex = i;

                if (i != 0)
                {
                    str.AppendLine();
                }

                // OFFSET
                str.Append($"{baseIndex:X4}    ");

                // BYTES
                for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    // space between columns
                    if (columnIndex != 0)
                    {
                        str.Append("  ");
                    }

                    for (var j = 0; j < columnWidth; j++)
                    {
                        if (j != 0)
                        {
                            str.Append(' ');
                        }

                        str.Append(i < bytes.Length
                            ? bytes[i].ToString("X2")
                            : "  ");
                        i++;
                    }
                }

                str.Append("    ");

                // ASCII
                i = baseIndex;
                for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    // space between columns
                    if (columnIndex != 0)
                    {
                        str.Append(' ');
                    }

                    for (var j = 0; j < columnWidth; j++)
                    {
                        if (i < bytes.Length)
                        {
                            var c = (char)bytes[i];
                            str.Append(char.IsControl(c) ? '.' : c);
                        }
                        else
                        {
                            str.Append(' ');
                        }

                        i++;
                    }
                }
            }

            if (bytes.Length > limit)
            {
                str.AppendLine();
                str.Append("[Truncated]");
            }

            return str.ToString();
        }

        private void SetStateOfContextLinesButtons()
        {
            increaseNumberOfLines.Enabled = !ShowEntireFile;
            decreaseNumberOfLines.Enabled = !ShowEntireFile;
            increaseNumberOfLinesToolStripMenuItem.Enabled = !ShowEntireFile;
            decreaseNumberOfLinesToolStripMenuItem.Enabled = !ShowEntireFile;
        }

        private void ToggleNonPrintingChars(bool show)
        {
            internalFileViewer.ShowEOLMarkers = show;
            internalFileViewer.ShowSpaces = show;
            internalFileViewer.ShowTabs = show;
        }

        // Event handlers

        private void OnUICommandsChanged(object sender, [CanBeNull] GitUICommandsChangedEventArgs e)
        {
            if (e?.OldCommands != null)
            {
                e.OldCommands.PostSettings -= UICommands_PostSettings;
            }

            var commandSource = sender as IGitUICommandsSource;
            if (commandSource?.UICommands != null)
            {
                commandSource.UICommands.PostSettings += UICommands_PostSettings;
                UICommands_PostSettings(commandSource.UICommands, null);
            }

            Encoding = null;
        }

        private void UICommands_PostSettings(object sender, GitUIPostActionEventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await internalFileViewer.SwitchToMainThreadAsync();
                internalFileViewer.VRulerPosition = AppSettings.DiffVerticalRulerPosition;
            }).FileAndForget();
        }

        private void IgnoreWhitespaceAtEolToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (IgnoreWhitespace == IgnoreWhitespaceKind.Eol)
            {
                IgnoreWhitespace = IgnoreWhitespaceKind.None;
            }
            else
            {
                IgnoreWhitespace = IgnoreWhitespaceKind.Eol;
            }

            OnIgnoreWhitespaceChanged();
            OnExtraDiffArgumentsChanged();
        }

        private void IgnoreWhitespaceChangesToolStripMenuItemClick(object sender, EventArgs e)
        {
            if (IgnoreWhitespace == IgnoreWhitespaceKind.Change)
            {
                IgnoreWhitespace = IgnoreWhitespaceKind.None;
            }
            else
            {
                IgnoreWhitespace = IgnoreWhitespaceKind.Change;
            }

            OnIgnoreWhitespaceChanged();
            OnExtraDiffArgumentsChanged();
        }

        private void IncreaseNumberOfLinesToolStripMenuItemClick(object sender, EventArgs e)
        {
            NumberOfContextLines++;
            AppSettings.NumberOfContextLines = NumberOfContextLines;
            OnExtraDiffArgumentsChanged();
        }

        private void DecreaseNumberOfLinesToolStripMenuItemClick(object sender, EventArgs e)
        {
            if (NumberOfContextLines > 0)
            {
                NumberOfContextLines--;
            }
            else
            {
                NumberOfContextLines = 0;
            }

            AppSettings.NumberOfContextLines = NumberOfContextLines;
            OnExtraDiffArgumentsChanged();
        }

        private void ShowSyntaxHighlighting_Click(object sender, System.EventArgs e)
        {
            ShowSyntaxHighlightingInDiff = !ShowSyntaxHighlightingInDiff;
            showSyntaxHighlighting.Checked = ShowSyntaxHighlightingInDiff;
            AppSettings.ShowSyntaxHighlightingInDiff = ShowSyntaxHighlightingInDiff;
            OnExtraDiffArgumentsChanged();
        }

        private void ShowEntireFileToolStripMenuItemClick(object sender, EventArgs e)
        {
            ShowEntireFile = !ShowEntireFile;
            showEntireFileButton.Checked = ShowEntireFile;
            showEntireFileToolStripMenuItem.Checked = ShowEntireFile;
            SetStateOfContextLinesButtons();
            AppSettings.ShowEntireFile = ShowEntireFile;
            OnExtraDiffArgumentsChanged();
        }

        private void _continuousScrollEventManager_BottomScrollReached(object sender, EventArgs e)
            => BottomScrollReached?.Invoke(sender, e);

        private void _continuousScrollEventManager_TopScrollReached(object sender, EventArgs e)
            => TopScrollReached?.Invoke(sender, e);

        private void llShowPreview_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            _NO_TRANSLATE_lblShowPreview.Hide();
            ThreadHelper.JoinableTaskFactory.Run(() => _deferShowFunc());
        }

        private void PictureBox_MouseWheel(object sender, MouseEventArgs e)
        {
            var isScrollingTowardTop = e.Delta > 0;
            var isScrollingTowardBottom = e.Delta < 0;

            if (isScrollingTowardTop)
            {
                _continuousScrollEventManager.RaiseTopScrollReached(sender, e);
            }

            if (isScrollingTowardBottom)
            {
                _continuousScrollEventManager.RaiseBottomScrollReached(sender, e);
            }
        }

        private void OnUICommandsSourceSet(object sender, GitUICommandsSourceEventArgs e)
        {
            UICommandsSource.UICommandsChanged += OnUICommandsChanged;
            OnUICommandsChanged(UICommandsSource, null);
        }

        private void TreatAllFilesAsTextToolStripMenuItemClick(object sender, EventArgs e)
        {
            treatAllFilesAsTextToolStripMenuItem.Checked = !treatAllFilesAsTextToolStripMenuItem.Checked;
            TreatAllFilesAsText = treatAllFilesAsTextToolStripMenuItem.Checked;
            OnExtraDiffArgumentsChanged();
        }

        /// <summary>
        /// Copy selected text, excluding diff added/deleted information
        /// </summary>
        /// <param name="sender">sender object</param>
        /// <param name="e">event args</param>
        private void CopyToolStripMenuItemClick(object sender, EventArgs e)
        {
            string code = internalFileViewer.GetSelectedText();

            if (string.IsNullOrEmpty(code))
            {
                return;
            }

            if (IsDiffView(_viewMode))
            {
                int pos = internalFileViewer.GetSelectionPosition();
                string fileText = internalFileViewer.GetText();
                int hpos = fileText.IndexOf("\n@@");

                // if header is selected then don't remove diff extra chars
                // for range-diff, copy all info (hpos will never match)
                if (hpos <= pos)
                {
                    if (pos > 0)
                    {
                        // add artificial space if selected text is not starting from line beginning, it will be removed later
                        if (fileText[pos - 1] != '\n')
                        {
                            code = " " + code;
                        }
                    }

                    string[] lines = code.Split('\n');
                    lines.Transform(RemovePrefix);
                    code = string.Join("\n", lines);
                }
            }

            ClipboardUtil.TrySetText(code.AdjustLineEndings(Module.EffectiveConfigFile.core.autocrlf.Value));

            return;

            string RemovePrefix(string line)
            {
                var specials = _viewMode == ViewMode.RangeDiff
                    ? _rangeDiffFullPrefixes
                    : DiffHighlightService.IsCombinedDiff(internalFileViewer.GetText())
                        ? _combinedDiffFullPrefixes
                        : _normalDiffFullPrefixes;

                foreach (var special in specials.Where(line.StartsWith))
                {
                    return line.Substring(special.Length);
                }

                return line;
            }
        }

        /// <summary>
        /// Copy selected text as a patch
        /// </summary>
        /// <param name="sender">sender object</param>
        /// <param name="e">event args</param>
        private void CopyPatchToolStripMenuItemClick(object sender, EventArgs e)
        {
            var selectedText = internalFileViewer.GetSelectedText();

            if (!string.IsNullOrEmpty(selectedText))
            {
                ClipboardUtil.TrySetText(selectedText);
                return;
            }

            var text = internalFileViewer.GetText();

            if (!string.IsNullOrEmpty(text))
            {
                ClipboardUtil.TrySetText(text);
            }
        }

        /// <summary>
        /// Go to next change
        /// For normal diffs, this is the next diff
        /// For range-diff, it is the next commit summary header
        /// </summary>
        /// <param name="sender">sender object</param>
        /// <param name="e">event args</param>
        private void NextChangeButtonClick(object sender, EventArgs e)
        {
            Focus();

            var inChange = _viewMode == ViewMode.RangeDiff
                ? _rangeDiffPrefixes
                : DiffHighlightService.IsCombinedDiff(internalFileViewer.GetText())
                    ? _combinedDiffPrefixes
                    : _normalDiffPrefixes;

            // skip the first pseudo-change containing the file names for normal diffs
            var headerEndLine = _viewMode == ViewMode.RangeDiff ? 0 : 4;
            var currentVisibleLine = internalFileViewer.LineAtCaret;
            var startLine = Math.Max(headerEndLine, currentVisibleLine + 1);
            var totalNumberOfLines = internalFileViewer.TotalNumberOfLines;
            var emptyLineCheck = _viewMode == ViewMode.RangeDiff;
            for (var line = startLine; line < totalNumberOfLines; line++)
            {
                var lineContent = internalFileViewer.GetLineText(line);

                if (_viewMode == ViewMode.RangeDiff ^ lineContent.StartsWithAny(inChange))
                {
                    if (emptyLineCheck)
                    {
                        internalFileViewer.FirstVisibleLine = Math.Max(line - headerEndLine, 0);
                        internalFileViewer.LineAtCaret = line;
                        return;
                    }
                }
                else
                {
                    emptyLineCheck = true;
                }
            }

            // Do not go to the end of the file if no change is found
            ////TextEditor.ActiveTextAreaControl.TextArea.TextView.FirstVisibleLine = totalNumberOfLines - TextEditor.ActiveTextAreaControl.TextArea.TextView.VisibleLineCount;
        }

        private void PreviousChangeButtonClick(object sender, EventArgs e)
        {
            Focus();

            var startLine = internalFileViewer.LineAtCaret;
            var inChange = _viewMode == ViewMode.RangeDiff
                ? _rangeDiffPrefixes
                : DiffHighlightService.IsCombinedDiff(internalFileViewer.GetText())
                    ? _combinedDiffPrefixes
                    : _normalDiffPrefixes;

            // go to the top of change block
            if (_viewMode == ViewMode.RangeDiff)
            {
                // Start checking line above current
                startLine--;
            }

            var headerEndLine = _viewMode == ViewMode.RangeDiff ? 0 : 4;
            while (startLine > headerEndLine &&
                   internalFileViewer.GetLineText(startLine).StartsWithAny(inChange))
            {
                startLine--;
            }

            if (_viewMode == ViewMode.RangeDiff)
            {
                internalFileViewer.FirstVisibleLine = startLine;
                internalFileViewer.LineAtCaret = startLine;
                return;
            }

            var emptyLineCheck = false;
            for (var line = startLine; line > headerEndLine; line--)
            {
                var lineContent = internalFileViewer.GetLineText(line);

                if (lineContent.StartsWithAny(inChange))
                {
                    emptyLineCheck = true;
                    continue;
                }

                if (!emptyLineCheck)
                {
                    continue;
                }

                internalFileViewer.FirstVisibleLine = Math.Max(0, line - 3);
                internalFileViewer.LineAtCaret = line + 1;
                return;
            }

            // Do not go to the start of the file if no change is found
            ////TextEditor.ActiveTextAreaControl.TextArea.TextView.FirstVisibleLine = 0;
        }

        private void ContinuousScrollToolStripMenuItemClick(object sender, EventArgs e)
        {
            automaticContinuousScrollToolStripMenuItem.Checked = !automaticContinuousScrollToolStripMenuItem.Checked;
            AppSettings.AutomaticContinuousScroll = automaticContinuousScrollToolStripMenuItem.Checked;
        }

        private void ShowNonprintableCharactersToolStripMenuItemClick(object sender, EventArgs e)
        {
            showNonprintableCharactersToolStripMenuItem.Checked = !showNonprintableCharactersToolStripMenuItem.Checked;
            showNonPrintChars.Checked = showNonprintableCharactersToolStripMenuItem.Checked;

            ToggleNonPrintingChars(show: showNonprintableCharactersToolStripMenuItem.Checked);
            AppSettings.ShowNonPrintingChars = showNonPrintChars.Checked;
        }

        private void FindToolStripMenuItemClick(object sender, EventArgs e)
        {
            internalFileViewer.Find();
        }

        private void encodingToolStripComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            Encoding encod;
            if (string.IsNullOrEmpty(encodingToolStripComboBox.Text))
            {
                encod = Module.FilesEncoding;
            }
            else if (encodingToolStripComboBox.Text.StartsWith("Default", StringComparison.CurrentCultureIgnoreCase))
            {
                encod = Encoding.Default;
            }
            else
            {
                encod = AppSettings.AvailableEncodings.Values
                    .FirstOrDefault(en => en.EncodingName == encodingToolStripComboBox.Text)
                        ?? Module.FilesEncoding;
            }

            if (!encod.Equals(Encoding))
            {
                Encoding = encod;
                OnExtraDiffArgumentsChanged();
            }
        }

        private void fileviewerToolbar_VisibleChanged(object sender, EventArgs e)
        {
            if (fileviewerToolbar.Visible)
            {
                if (Encoding != null)
                {
                    encodingToolStripComboBox.Text = Encoding.EncodingName;
                }
            }
        }

        private void goToLineToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var formGoToLine = new FormGoToLine())
            {
                formGoToLine.SetMaxLineNumber(internalFileViewer.MaxLineNumber);
                if (formGoToLine.ShowDialog(this) == DialogResult.OK)
                {
                    GoToLine(formGoToLine.GetLineNumber());
                }
            }
        }

        private void copyNewVersionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CopyNotStartingWith('-');
        }

        private void copyOldVersionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CopyNotStartingWith('+');
        }

        private void applySelectedLines(int selectionStart, int selectionLength, bool reverse)
        {
            // Prepare git command
            var args = new GitArgumentBuilder("apply")
            {
                "--3way",
                "--whitespace=nowarn"
            };

            byte[] patch;

            if (reverse)
            {
                patch = PatchManager.GetResetWorkTreeLinesAsPatch(
                    Module, GetText(),
                    selectionStart, selectionLength, Encoding);
            }
            else
            {
                patch = PatchManager.GetSelectedLinesAsPatch(
                    GetText(),
                    selectionStart, selectionLength,
                    false, Encoding, false);
            }

            if (patch != null && patch.Length > 0)
            {
                string output = Module.GitExecutable.GetOutput(args, patch);

                if (!string.IsNullOrEmpty(output))
                {
                    if (!MergeConflictHandler.HandleMergeConflicts(UICommands, this, false, false))
                    {
                        MessageBox.Show(this, output + "\n\n" + Encoding.GetString(patch), Strings.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void cherrypickSelectedLinesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            applySelectedLines(GetSelectionPosition(), GetSelectionLength(), reverse: false);
        }

        private void settingsButton_Click(object sender, EventArgs e)
        {
            UICommands.StartSettingsDialog(ParentForm, DiffViewerSettingsPage.GetPageReference());
        }

        private void revertSelectedLinesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            applySelectedLines(GetSelectionPosition(), GetSelectionLength(), reverse: true);
        }

        private void IgnoreAllWhitespaceChangesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (IgnoreWhitespace == IgnoreWhitespaceKind.AllSpace)
            {
                IgnoreWhitespace = IgnoreWhitespaceKind.None;
            }
            else
            {
                IgnoreWhitespace = IgnoreWhitespaceKind.AllSpace;
            }

            OnIgnoreWhitespaceChanged();
            OnExtraDiffArgumentsChanged();
        }

        #region Hotkey commands

        public static readonly string HotkeySettingsName = "FileViewer";

        internal enum Commands
        {
            Find = 0,
            FindNextOrOpenWithDifftool = 8,
            FindPrevious = 9,
            GoToLine = 1,
            IncreaseNumberOfVisibleLines = 2,
            DecreaseNumberOfVisibleLines = 3,
            ShowEntireFile = 4,
            TreatFileAsText = 5,
            NextChange = 6,
            PreviousChange = 7,
            NextOccurrence = 10,
            PreviousOccurrence = 11
        }

        protected override CommandStatus ExecuteCommand(int cmd)
        {
            var command = (Commands)cmd;

            switch (command)
            {
                case Commands.Find: internalFileViewer.Find(); break;
                case Commands.FindNextOrOpenWithDifftool: ThreadHelper.JoinableTaskFactory.RunAsync(() => internalFileViewer.FindNextAsync(searchForwardOrOpenWithDifftool: true)); break;
                case Commands.FindPrevious: ThreadHelper.JoinableTaskFactory.RunAsync(() => internalFileViewer.FindNextAsync(searchForwardOrOpenWithDifftool: false)); break;
                case Commands.GoToLine: goToLineToolStripMenuItem_Click(null, null); break;
                case Commands.IncreaseNumberOfVisibleLines: IncreaseNumberOfLinesToolStripMenuItemClick(null, null); break;
                case Commands.DecreaseNumberOfVisibleLines: DecreaseNumberOfLinesToolStripMenuItemClick(null, null); break;
                case Commands.ShowEntireFile: ShowEntireFileToolStripMenuItemClick(null, null); break;
                case Commands.TreatFileAsText: TreatAllFilesAsTextToolStripMenuItemClick(null, null); break;
                case Commands.NextChange: NextChangeButtonClick(null, null); break;
                case Commands.PreviousChange: PreviousChangeButtonClick(null, null); break;
                case Commands.NextOccurrence: internalFileViewer.GoToNextOccurrence(); break;
                case Commands.PreviousOccurrence: internalFileViewer.GoToPreviousOccurrence(); break;
                default: return base.ExecuteCommand(cmd);
            }

            return true;
        }

        #endregion

        internal TestAccessor GetTestAccessor() => new TestAccessor(this);

        internal readonly struct TestAccessor
        {
            private readonly FileViewer _fileViewer;

            public TestAccessor(FileViewer fileViewer)
            {
                _fileViewer = fileViewer;
            }

            public ToolStripMenuItem CopyToolStripMenuItem => _fileViewer.copyToolStripMenuItem;

            public FileViewerInternal FileViewerInternal => _fileViewer.internalFileViewer;

            public IgnoreWhitespaceKind IgnoreWhitespace
            {
                get => _fileViewer.IgnoreWhitespace;
                set => _fileViewer.IgnoreWhitespace = value;
            }

            public bool ShowSyntaxHighlightingInDiff
            {
                get => _fileViewer.ShowSyntaxHighlightingInDiff;
                set => _fileViewer.ShowSyntaxHighlightingInDiff = value;
            }

            public ToolStripButton IgnoreWhitespaceAtEolButton => _fileViewer.ignoreWhitespaceAtEol;
            public ToolStripMenuItem IgnoreWhitespaceAtEolMenuItem => _fileViewer.ignoreWhitespaceAtEolToolStripMenuItem;

            public ToolStripButton IgnoreWhiteSpacesButton => _fileViewer.ignoreWhiteSpaces;
            public ToolStripMenuItem IgnoreWhiteSpacesMenuItem => _fileViewer.ignoreWhitespaceChangesToolStripMenuItem;

            public ToolStripButton IgnoreAllWhitespacesButton => _fileViewer.ignoreAllWhitespaces;
            public ToolStripMenuItem IgnoreAllWhitespacesMenuItem => _fileViewer.ignoreAllWhitespaceChangesToolStripMenuItem;

            internal void IgnoreWhitespaceAtEolToolStripMenuItem_Click(object sender, EventArgs e) => _fileViewer.IgnoreWhitespaceAtEolToolStripMenuItem_Click(sender, e);
            internal void IgnoreWhitespaceChangesToolStripMenuItemClick(object sender, EventArgs e) => _fileViewer.IgnoreWhitespaceChangesToolStripMenuItemClick(sender, e);
            internal void IgnoreAllWhitespaceChangesToolStripMenuItem_Click(object sender, EventArgs e) => _fileViewer.IgnoreAllWhitespaceChangesToolStripMenuItem_Click(sender, e);
        }
    }
}
