using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Eto.Drawing;
using GLoom.Serialization;
using GLoom.Vcs;
using Grasshopper.Kernel;
using Rhino;

// The plugin compiles against System.Windows.Forms (for GH_Canvas), so we
// alias every Eto.Forms type used here to keep names unambiguous.
using Application = Eto.Forms.Application;
using Button = Eto.Forms.Button;
using ButtonMenuItem = Eto.Forms.ButtonMenuItem;
using CheckBox = Eto.Forms.CheckBox;
using Color = Eto.Drawing.Color;
using ContextMenu = Eto.Forms.ContextMenu;
using Control = Eto.Forms.Control;
using DialogResult = Eto.Forms.DialogResult;
using DynamicLayout = Eto.Forms.DynamicLayout;
using Font = Eto.Drawing.Font;
using HorizontalAlignment = Eto.Forms.HorizontalAlignment;
using Label = Eto.Forms.Label;
using MessageBox = Eto.Forms.MessageBox;
using MessageBoxButtons = Eto.Forms.MessageBoxButtons;
using MessageBoxType = Eto.Forms.MessageBoxType;
using MouseButtons = Eto.Forms.MouseButtons;
using Orientation = Eto.Forms.Orientation;
using Padding = Eto.Drawing.Padding;
using Panel = Eto.Forms.Panel;
using Scrollable = Eto.Forms.Scrollable;
using SeparatorMenuItem = Eto.Forms.SeparatorMenuItem;
using Size = Eto.Drawing.Size;
using StackLayout = Eto.Forms.StackLayout;
using StackLayoutItem = Eto.Forms.StackLayoutItem;
using TableCell = Eto.Forms.TableCell;
using TableLayout = Eto.Forms.TableLayout;
using TableRow = Eto.Forms.TableRow;
using WrapMode = Eto.Forms.WrapMode;

namespace GLoom.Ui;

[Guid("55f07e53-ad04-44a9-ab21-059f32207842")]
public sealed class GLoomPanel : Panel
{
    public static Guid PanelId => typeof(GLoomPanel).GUID;

    private const int DefaultHistoryLimit = 10;
    private const int HistoryIncrement = 10;

    // Top metadata labels
    private readonly Label _filePathLabel = NewValueLabel();
    private readonly Label _repoLabel = NewValueLabel();
    private readonly Button _branchButton = new() { Text = "-" };
    private readonly Button _remoteButton = new() { Text = "-" };
    private readonly Label _syncStatusLabel = NewValueLabel();
    private readonly Button _syncButton = new() { Text = "Sync" };
    private readonly Label _currentVersionLabel = NewValueLabel();
    private readonly Label _lastCommitLabel = NewValueLabel();
    private readonly Label _nextVersionLabel = NewValueLabel();
    private readonly Label _dirtyLabel = NewValueLabel();
    private readonly Button _commitButton;
    private readonly Button _refreshButton;
    private readonly CheckBox _overlayToggle = new() { Text = "Diff overlay" };
    private readonly CheckBox _filterAdded = new() { Text = "Added", Checked = true };
    private readonly CheckBox _filterModified = new() { Text = "Modified", Checked = true };
    private readonly CheckBox _filterMoved = new() { Text = "Moved", Checked = true };
    private readonly CheckBox _filterDeleted = new() { Text = "Deleted", Checked = true };
    private readonly Button _hoverModeButton = new() { Text = "Hover for details: OFF" };
    private readonly Label _overlayRefLabel = new() { Text = "Overlay ref: HEAD" };
    private readonly Button _overlayRefResetBtn = new() { Text = "Reset", Visible = false };
    private ContextMenu? _branchMenu;
    private ContextMenu? _remoteMenu;

    // History list
    private readonly Label _historyHeaderLabel = new() { Text = "History" };
    private readonly StackLayout _historyContainer = new() { Orientation = Eto.Forms.Orientation.Vertical, Spacing = 0 };
    private readonly Button _showMoreButton;
    private int _historyLimit = DefaultHistoryLimit;
    private readonly HashSet<string> _expandedShas = new(StringComparer.Ordinal);

    public GLoomPanel()
    {
        _commitButton = new Button { Text = "Commit current version" };
        _refreshButton = new Button { Text = "Refresh" };
        _showMoreButton = new Button { Text = "Show more", Visible = false };

        _commitButton.Click += (_, _) => OnCommitClicked();
        _refreshButton.Click += (_, _) =>
        {
            _historyLimit = DefaultHistoryLimit;
            DocumentTracker.Instance.Refresh();
        };
        _showMoreButton.Click += (_, _) =>
        {
            _historyLimit += HistoryIncrement;
            Refresh();
        };
        _branchButton.Click += (_, _) => _branchMenu?.Show(_branchButton);
        _remoteButton.Click += (_, _) => _remoteMenu?.Show(_remoteButton);
        _syncButton.Click += (_, _) => OnSyncClicked();

        // Two-way binding with the overlay singleton: clicking the box
        // toggles the overlay; the overlay raising EnabledChanged
        // (e.g. from a future shortcut) keeps the box in sync.
        var overlay = CanvasDiffOverlay.Instance;
        _overlayToggle.Checked = overlay.Enabled;
        _overlayToggle.CheckedChanged += (_, _) =>
        {
            overlay.SetEnabled(_overlayToggle.Checked == true);
            UpdateFilterEnabled();
        };
        overlay.EnabledChanged += (_, _) =>
        {
            try
            {
                Application.Instance.AsyncInvoke(() =>
                {
                    _overlayToggle.Checked = overlay.Enabled;
                    UpdateFilterEnabled();
                });
            }
            catch { /* not yet attached to an Eto loop */ }
        };

        // Filter checkboxes + hover toggle button drive the overlay settings.
        _filterAdded.Checked = overlay.ShowAdded;
        _filterModified.Checked = overlay.ShowModified;
        _filterMoved.Checked = overlay.ShowMoved;
        _filterDeleted.Checked = overlay.ShowDeleted;
        _filterAdded.CheckedChanged += (_, _) => overlay.ShowAdded = _filterAdded.Checked == true;
        _filterModified.CheckedChanged += (_, _) => overlay.ShowModified = _filterModified.Checked == true;
        _filterMoved.CheckedChanged += (_, _) => overlay.ShowMoved = _filterMoved.Checked == true;
        _filterDeleted.CheckedChanged += (_, _) => overlay.ShowDeleted = _filterDeleted.Checked == true;

        UpdateHoverButtonText();
        _hoverModeButton.Click += (_, _) =>
        {
            overlay.HoverDetailsOnly = !overlay.HoverDetailsOnly;
            UpdateHoverButtonText();
        };

        // Overlay reference indicator + reset. The reference is the
        // commit the overlay compares the live state AGAINST. HEAD by
        // default; user can pick any commit row's "compare on canvas"
        // button to re-target. Reset returns to HEAD.
        UpdateOverlayRefLabel();
        _overlayRefResetBtn.Click += (_, _) => overlay.SetComparisonReference("HEAD");
        overlay.ComparisonReferenceChanged += (_, _) =>
        {
            try
            {
                Application.Instance.AsyncInvoke(() =>
                {
                    UpdateOverlayRefLabel();
                    Refresh();
                });
            }
            catch { /* not yet attached to an Eto loop */ }
        };

        UpdateFilterEnabled();

        var headerFont = new Font(SystemFont.Bold, 13);
        var sectionFont = new Font(SystemFont.Bold, 11);
        _historyHeaderLabel.Font = sectionFont;

        var meta = new TableLayout
        {
            Padding = new Padding(12, 12, 12, 6),
            Spacing = new Size(8, 6),
            Rows =
            {
                new TableRow(new Label { Text = "G-Loom", Font = headerFont }),
                LabelRow("File:",            _filePathLabel),
                LabelRow("Repository:",      _repoLabel),
                LabelRow("Branch:",          _branchButton),
                LabelRow("Remote:",          _remoteButton),
                LabelRow("Sync:",            BuildSyncRow()),
                LabelRow("Current version:", _currentVersionLabel),
                LabelRow("Last commit:",     _lastCommitLabel),
                LabelRow("Next version:",    _nextVersionLabel),
                LabelRow("State:",           _dirtyLabel),
                new TableRow(_commitButton),
                new TableRow(_refreshButton),
                new TableRow(_overlayToggle),
                new TableRow(BuildOverlayFilters()),
            },
        };

        var historyScroll = new Scrollable
        {
            Padding = new Padding(0),
            Border = Eto.Forms.BorderType.None,
            Content = _historyContainer,
            ExpandContentWidth = true,
        };

        var root = new TableLayout
        {
            Spacing = new Size(0, 0),
            Rows =
            {
                new TableRow(meta),
                new TableRow(new Panel { Padding = new Padding(12, 0), Content = _historyHeaderLabel }),
                new TableRow(historyScroll) { ScaleHeight = true },
                new TableRow(new Panel { Padding = new Padding(12, 6), Content = _showMoreButton }),
            },
        };

        Content = root;

        DocumentTracker.Instance.StateChanged += OnTrackerChanged;
        Refresh();
    }

    // ---- helpers ------------------------------------------------------

    private static Label NewValueLabel() => new() { Text = "-", Wrap = WrapMode.Word };

    private StackLayout BuildSyncRow()
    {
        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalContentAlignment = Eto.Forms.VerticalAlignment.Center,
            Items =
            {
                new StackLayoutItem(_syncStatusLabel, expand: true),
                new StackLayoutItem(_syncButton),
            },
        };
    }

    private TableLayout BuildOverlayFilters()
    {
        var filterStack = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            Items = { _filterAdded, _filterModified, _filterMoved, _filterDeleted },
        };

        var refRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = Eto.Forms.VerticalAlignment.Center,
            Items = { _overlayRefLabel, _overlayRefResetBtn },
        };

        return new TableLayout
        {
            Spacing = new Size(8, 4),
            Padding = new Padding(16, 0, 0, 0),
            Rows =
            {
                new TableRow(new TableCell(refRow, true)),
                new TableRow(
                    new TableCell(filterStack, true),
                    new TableCell(_hoverModeButton, false)),
            },
        };
    }

    private void UpdateOverlayRefLabel()
    {
        var r = CanvasDiffOverlay.Instance.ComparisonReference;
        if (r == "HEAD")
        {
            _overlayRefLabel.Text = "Overlay ref: HEAD";
            _overlayRefResetBtn.Visible = false;
        }
        else
        {
            _overlayRefLabel.Text = "Overlay ref: " + (r.Length >= 7 ? r[..7] : r);
            _overlayRefResetBtn.Visible = true;
        }
    }

    private void UpdateHoverButtonText()
    {
        var on = CanvasDiffOverlay.Instance.HoverDetailsOnly;
        _hoverModeButton.Text = on ? "Hover for details: ON" : "Hover for details: OFF";
    }

    private void UpdateFilterEnabled()
    {
        var on = _overlayToggle.Checked == true;
        _filterAdded.Enabled = on;
        _filterModified.Enabled = on;
        _filterMoved.Enabled = on;
        _filterDeleted.Enabled = on;
        _hoverModeButton.Enabled = on;
        _overlayRefResetBtn.Enabled = on && CanvasDiffOverlay.Instance.ComparisonReference != "HEAD";
    }

    private static TableRow LabelRow(string caption, Control valueControl) =>
        new(new TableCell(new Label { Text = caption }, false),
            new TableCell(valueControl, true));

    private void OnTrackerChanged(object? sender, EventArgs e)
    {
        try { Application.Instance.AsyncInvoke(Refresh); }
        catch { /* Eto not yet attached to a UI loop; ignore */ }
    }

    // ---- main render --------------------------------------------------

    private void Refresh()
    {
        var s = DocumentTracker.Instance.State;

        if (s.Document is null) { SetIdle("(no Grasshopper document loaded)"); return; }
        if (string.IsNullOrEmpty(s.FilePath)) { SetIdle("(unsaved document - save it first)"); return; }
        if (!s.IsTracked || s.RepoPath is null)
        {
            SetUntracked(s.FilePath, s.HasUnsavedChanges);
            return;
        }

        try
        {
            _filePathLabel.Text = s.FilePath;
            _repoLabel.Text = s.RepoPath;

            var ghBase = Path.GetFileNameWithoutExtension(s.FilePath);
            var ghRel = Path.GetRelativePath(s.RepoPath, s.FilePath);
            var jsonRel = Path.GetRelativePath(s.RepoPath, s.CanonicalJsonFullPath!);
            var fileScope = new[] { ghRel, jsonRel };

            // Branch is repo-wide; "last commit" is filtered to this file's pair
            // so multi-file repos don't bleed another file's commit into the panel.
            var status = GLoomRepository.GetStatus(s.RepoPath, fileScope);
            var branches = GLoomRepository.GetBranches(s.RepoPath);
            UpdateBranchControl(s, branches, status.Branch);

            var remotes = GLoomRepository.GetRemotes(s.RepoPath);
            var upstream = GLoomRepository.GetUpstream(s.RepoPath, status.Branch);
            var ab = GLoomRepository.GetAheadBehind(s.RepoPath, status.Branch);
            UpdateRemoteControl(s, remotes, upstream, branches, status.Branch);
            UpdateSyncControl(remotes, upstream, ab, status.Branch);
            _lastCommitLabel.Text = status.LastCommit is null
                ? "(no commits yet)"
                : $"{status.LastCommit.Sha[..7]}  {status.LastCommit.Message}";

            // Resolve "current" SHA: explicit restore wins, else this-file's HEAD.
            var currentSha = s.CurrentRestoredSha ?? status.LastCommit?.Sha;
            _currentVersionLabel.Text = ResolveCurrentVersionLabel(s, currentSha, fileScope);

            var nextV = CommitVersioning.NextVersion(s.RepoPath, ghRel, jsonRel);
            _nextVersionLabel.Text = CommitVersioning.FormatMessage(ghBase, nextV);

            _dirtyLabel.Text = s.HasUnsavedChanges
                ? "unsaved changes (commit will use live state, .gh on disk may be stale)"
                : "clean";
            _commitButton.Enabled = true;

            RefreshHistory(s, currentSha, fileScope, status.Branch);
        }
        catch (Exception ex)
        {
            _commitButton.Enabled = false;
            DisableBranchControl();
            DisableRemoteControls();
            _lastCommitLabel.Text = $"(error: {ex.Message})";
            _historyContainer.Items.Clear();
            _showMoreButton.Visible = false;
        }
    }

    private string ResolveCurrentVersionLabel(TrackedState s, string? currentSha, IReadOnlyList<string> fileScope)
    {
        if (currentSha is null) return "(not committed yet)";

        // Scan a window of this-file commits and look for the SHA. Cheaper than
        // a separate `git log -1 <sha>` round-trip and good enough since the
        // current commit is by definition recent in *this file's* history.
        var recent = GLoomRepository.Log(s.RepoPath!, Math.Max(_historyLimit, 50), fileScope);
        foreach (var c in recent)
            if (c.Sha == currentSha)
                return CommitVersioning.ExtractVersionLabel(c.Message)
                       ?? c.Message;

        return currentSha[..7];
    }

    private void RefreshHistory(TrackedState s, string? currentSha, IReadOnlyList<string> fileScope, string branchName)
    {
        if (s.RepoPath is null || s.FilePath is null) { _historyContainer.Items.Clear(); _showMoreButton.Visible = false; return; }

        var commits = GLoomRepository.Log(s.RepoPath, _historyLimit, fileScope);

        // Fork-point markers: where the current branch diverged from
        // (a) the default branch and (b) the closest other local branch.
        var forks = GLoomRepository.GetForkPoints(s.RepoPath, branchName);
        var forksBySha = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var fp in forks)
        {
            if (!forksBySha.TryGetValue(fp.ForkSha, out var list))
            {
                list = new List<string>();
                forksBySha[fp.ForkSha] = list;
            }
            list.Add(fp.ParentBranch);
        }

        // Tags are repo-wide; we group by SHA and only render in drawers
        // for commits the file-scoped history actually surfaces.
        var tags = GLoomRepository.GetTags(s.RepoPath);
        var tagsBySha = new Dictionary<string, List<GLoomRepository.TagInfo>>(StringComparer.Ordinal);
        foreach (var t in tags)
        {
            if (!tagsBySha.TryGetValue(t.Sha, out var list))
            {
                list = new List<GLoomRepository.TagInfo>();
                tagsBySha[t.Sha] = list;
            }
            list.Add(t);
        }
        foreach (var list in tagsBySha.Values)
            list.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));

        // Read the current working tree's canonical JSON ONCE per refresh -
        // every row's diff has the same right-hand side (current state),
        // and parsing N times wastes work. Historic JSON is read lazily
        // when each row's drawer is first expanded.
        CanonicalDocument? currentDoc = null;
        try
        {
            if (s.CanonicalJsonFullPath is not null && File.Exists(s.CanonicalJsonFullPath))
                currentDoc = CanonicalJson.TryParse(File.ReadAllText(s.CanonicalJsonFullPath));
        }
        catch { /* leave currentDoc null; diffs render as unavailable */ }

        var jsonRel = s.CanonicalJsonFullPath is not null
            ? Path.GetRelativePath(s.RepoPath, s.CanonicalJsonFullPath)
            : null;

        var currentRef = CanvasDiffOverlay.Instance.ComparisonReference;
        var visibleShas = new HashSet<string>(StringComparer.Ordinal);
        _historyContainer.Items.Clear();
        foreach (var c in commits)
        {
            visibleShas.Add(c.Sha);
            var isCurrent = currentSha is not null && c.Sha == currentSha;
            var isActiveReference = currentRef == c.Sha;
            forksBySha.TryGetValue(c.Sha, out var parents);
            tagsBySha.TryGetValue(c.Sha, out var rowTags);
            var sha = c.Sha;
            var repoPath = s.RepoPath;

            Func<DocumentDiff?> diffProvider = isCurrent || currentDoc is null || jsonRel is null
                ? () => null
                : () =>
                {
                    var historicJson = GLoomRepository.ReadFileAtCommit(repoPath, sha, jsonRel);
                    var historicDoc = CanonicalJson.TryParse(historicJson);
                    return historicDoc is null ? null : DocumentDiff.Compute(historicDoc, currentDoc);
                };

            var rowIsActiveReference = isActiveReference;
            var row = new CommitRow(
                info: c,
                isCurrent: isCurrent,
                isActiveReference: isActiveReference,
                forkParents: parents,
                tags: rowTags,
                initiallyExpanded: _expandedShas.Contains(sha),
                diffProvider: diffProvider,
                onRestore: () => OnRestoreClicked(sha, c.Message),
                onToggleReference: () =>
                {
                    var newRef = rowIsActiveReference ? "HEAD" : sha;
                    CanvasDiffOverlay.Instance.SetComparisonReference(newRef);
                },
                onCreateTag: () => OnCreateTagClicked(sha, c.Message),
                onDeleteTag: name => OnDeleteTagClicked(name),
                onExpansionChanged: expanded =>
                {
                    if (expanded) _expandedShas.Add(sha);
                    else _expandedShas.Remove(sha);
                });
            _historyContainer.Items.Add(new StackLayoutItem(row, HorizontalAlignment.Stretch));
        }
        // Drop expansion state for commits that no longer appear in the
        // visible window so the set doesn't grow unbounded.
        _expandedShas.RemoveWhere(s => !visibleShas.Contains(s));

        _historyHeaderLabel.Text = commits.Count == 0
            ? "History (no commits yet)"
            : $"History (showing {commits.Count})";

        // If we got back exactly the limit, there might be more. Show the button.
        _showMoreButton.Visible = commits.Count >= _historyLimit;
    }

    private void SetIdle(string fileLabel)
    {
        _filePathLabel.Text = fileLabel;
        _repoLabel.Text = "-";
        DisableBranchControl();
        DisableRemoteControls();
        _currentVersionLabel.Text = "-";
        _lastCommitLabel.Text = "-";
        _nextVersionLabel.Text = "-";
        _dirtyLabel.Text = "-";
        _commitButton.Enabled = false;
        _historyContainer.Items.Clear();
        _historyHeaderLabel.Text = "History";
        _showMoreButton.Visible = false;
    }

    private void SetUntracked(string filePath, bool dirty)
    {
        _filePathLabel.Text = filePath;
        _repoLabel.Text = "(not in a Git repo - run `git init` in the folder)";
        DisableBranchControl();
        DisableRemoteControls();
        _currentVersionLabel.Text = "-";
        _lastCommitLabel.Text = "-";
        _nextVersionLabel.Text = "-";
        _dirtyLabel.Text = dirty ? "unsaved changes" : "clean";
        _commitButton.Enabled = false;
        _historyContainer.Items.Clear();
        _historyHeaderLabel.Text = "History";
        _showMoreButton.Visible = false;
    }

    private void DisableBranchControl()
    {
        _branchButton.Text = "-";
        _branchButton.Enabled = false;
        _branchMenu = null;
    }

    private void DisableRemoteControls()
    {
        _remoteButton.Text = "-";
        _remoteButton.Enabled = false;
        _remoteMenu = null;
        _syncStatusLabel.Text = "-";
        _syncButton.Text = "Sync";
        _syncButton.Enabled = false;
    }

    /// <summary>
    /// Renders the Remote button: shows the upstream when one is configured
    /// (origin/main), or the remote name with a "no upstream" hint, or a
    /// "(no remote)" placeholder. Menu adapts to whichever state we're in -
    /// Add when nothing's configured, Set upstream / Change URL / Remove
    /// when at least one remote exists.
    /// </summary>
    private void UpdateRemoteControl(
        TrackedState s,
        IReadOnlyList<GLoomRepository.RemoteInfo> remotes,
        GLoomRepository.UpstreamInfo? upstream,
        IReadOnlyList<GLoomRepository.BranchInfo> branches,
        string currentBranch)
    {
        var menu = new ContextMenu();

        if (remotes.Count == 0)
        {
            _remoteButton.Text = "(no remote)  ▼";
            _remoteButton.Enabled = true;

            var addItem = new ButtonMenuItem { Text = "Add remote..." };
            addItem.Click += (_, _) => OnAddRemoteClicked(s);
            menu.Items.Add(addItem);
        }
        else
        {
            if (upstream is { } u)
                _remoteButton.Text = $"▶ {u.Remote}/{u.RemoteBranch}  ▼";
            else
                _remoteButton.Text = $"{remotes[0].Name} (no upstream)  ▼";
            _remoteButton.Enabled = true;

            var canSetUpstream = branches.Count > 0
                && !string.IsNullOrEmpty(currentBranch)
                && currentBranch != "(detached)";

            // URL info row(s) - non-actionable, lets the user see what's configured.
            foreach (var r in remotes)
            {
                var info = new ButtonMenuItem { Text = $"{r.Name}: {r.FetchUrl}", Enabled = false };
                menu.Items.Add(info);
            }
            menu.Items.Add(new SeparatorMenuItem());

            // Set upstream - one-click for single-remote, sub-menu for many.
            if (remotes.Count == 1 && canSetUpstream)
            {
                var only = remotes[0];
                var label = upstream is null
                    ? $"Set upstream to {only.Name}/{currentBranch}"
                    : $"Change upstream to {only.Name}/{currentBranch}";
                var setItem = new ButtonMenuItem { Text = label };
                setItem.Click += (_, _) => OnSetUpstreamClicked(s, currentBranch, only.Name, currentBranch);
                menu.Items.Add(setItem);
            }
            else
            {
                var setRoot = new ButtonMenuItem { Text = "Set upstream...", Enabled = canSetUpstream };
                foreach (var r in remotes)
                {
                    var remoteName = r.Name;
                    var sub = new ButtonMenuItem { Text = $"{remoteName}/{currentBranch}" };
                    sub.Click += (_, _) => OnSetUpstreamClicked(s, currentBranch, remoteName, currentBranch);
                    setRoot.Items.Add(sub);
                }
                menu.Items.Add(setRoot);
            }

            // Change URL - single remote: one click, many: sub-menu.
            if (remotes.Count == 1)
            {
                var only = remotes[0];
                var changeItem = new ButtonMenuItem { Text = "Change URL..." };
                changeItem.Click += (_, _) => OnChangeRemoteUrlClicked(s, only.Name, only.FetchUrl);
                menu.Items.Add(changeItem);
            }
            else
            {
                var changeRoot = new ButtonMenuItem { Text = "Change URL..." };
                foreach (var r in remotes)
                {
                    var rn = r.Name;
                    var ru = r.FetchUrl;
                    var sub = new ButtonMenuItem { Text = rn };
                    sub.Click += (_, _) => OnChangeRemoteUrlClicked(s, rn, ru);
                    changeRoot.Items.Add(sub);
                }
                menu.Items.Add(changeRoot);
            }

            menu.Items.Add(new SeparatorMenuItem());

            var addItem = new ButtonMenuItem { Text = "Add remote..." };
            addItem.Click += (_, _) => OnAddRemoteClicked(s);
            menu.Items.Add(addItem);

            if (remotes.Count == 1)
            {
                var only = remotes[0];
                var rem = new ButtonMenuItem { Text = $"Remove '{only.Name}'" };
                rem.Click += (_, _) => OnRemoveRemoteClicked(s, only.Name);
                menu.Items.Add(rem);
            }
            else
            {
                var removeRoot = new ButtonMenuItem { Text = "Remove remote..." };
                foreach (var r in remotes)
                {
                    var rn = r.Name;
                    var sub = new ButtonMenuItem { Text = rn };
                    sub.Click += (_, _) => OnRemoveRemoteClicked(s, rn);
                    removeRoot.Items.Add(sub);
                }
                menu.Items.Add(removeRoot);
            }
        }

        _remoteMenu = menu;
    }

    private void UpdateSyncControl(
        IReadOnlyList<GLoomRepository.RemoteInfo> remotes,
        GLoomRepository.UpstreamInfo? upstream,
        GLoomRepository.AheadBehind ab,
        string currentBranch)
    {
        if (remotes.Count == 0)
        {
            _syncStatusLabel.Text = "(no remote configured)";
            _syncButton.Text = "Sync";
            _syncButton.Enabled = false;
            return;
        }

        if (currentBranch == "(detached)")
        {
            _syncStatusLabel.Text = "(detached HEAD)";
            _syncButton.Text = "Sync";
            _syncButton.Enabled = false;
            return;
        }

        if (upstream is null)
        {
            _syncStatusLabel.Text = "(no upstream)";
            _syncButton.Text = "Push";
            _syncButton.Enabled = true;
            return;
        }

        if (ab.Ahead == 0 && ab.Behind == 0)
        {
            _syncStatusLabel.Text = "✓ up to date";
            _syncButton.Text = "Sync";
        }
        else if (ab.Ahead > 0 && ab.Behind == 0)
        {
            _syncStatusLabel.Text = $"↑{ab.Ahead}";
            _syncButton.Text = "Push";
        }
        else if (ab.Ahead == 0 && ab.Behind > 0)
        {
            _syncStatusLabel.Text = $"↓{ab.Behind}";
            _syncButton.Text = "Pull";
        }
        else
        {
            _syncStatusLabel.Text = $"↓{ab.Behind}  ↑{ab.Ahead}";
            _syncButton.Text = "Sync";
        }

        _syncButton.Enabled = true;
    }

    /// <summary>
    /// Renders the branch dropdown: the button shows the current branch and
    /// a ▼ hint, and clicking opens a menu listing every branch (active one
    /// disabled, others switch on click), plus "New branch...", a
    /// "Rename branch..." sub-menu of every branch, and a "Delete branch..."
    /// sub-menu of non-current branches.
    /// </summary>
    private void UpdateBranchControl(
        TrackedState s,
        IReadOnlyList<GLoomRepository.BranchInfo> branches,
        string statusBranch)
    {
        if (branches.Count == 0)
        {
            // Brand-new repo, no commits yet -> no branches exist.
            _branchButton.Text = $"{statusBranch}  (commit something first)";
            _branchButton.Enabled = false;
            _branchMenu = null;
            return;
        }

        var current = branches.FirstOrDefault(b => b.IsCurrent);
        var currentName = current?.Name ?? statusBranch;
        _branchButton.Text = $"▶ {currentName}  ▼";
        _branchButton.Enabled = true;

        var menu = new ContextMenu();
        foreach (var b in branches)
        {
            var name = b.Name;
            var item = new ButtonMenuItem
            {
                Text = b.IsCurrent ? $"▶ {name}" : name,
                Enabled = !b.IsCurrent,
            };
            if (!b.IsCurrent)
                item.Click += (_, _) => OnSwitchBranchClicked(s, name);
            menu.Items.Add(item);
        }
        menu.Items.Add(new SeparatorMenuItem());

        var newItem = new ButtonMenuItem { Text = "New branch..." };
        newItem.Click += (_, _) => OnCreateBranchClicked(s);
        menu.Items.Add(newItem);

        var renameRoot = new ButtonMenuItem { Text = "Rename branch..." };
        foreach (var b in branches)
        {
            var name = b.Name;
            var sub = new ButtonMenuItem { Text = b.IsCurrent ? $"▶ {name}" : name };
            sub.Click += (_, _) => OnRenameBranchClicked(s, name);
            renameRoot.Items.Add(sub);
        }
        menu.Items.Add(renameRoot);

        var deleteRoot = new ButtonMenuItem { Text = "Delete branch..." };
        var nonCurrent = branches.Where(b => !b.IsCurrent).ToList();
        foreach (var b in nonCurrent)
        {
            var name = b.Name;
            var sub = new ButtonMenuItem { Text = name };
            sub.Click += (_, _) => OnDeleteBranchClicked(s, name);
            deleteRoot.Items.Add(sub);
        }
        deleteRoot.Enabled = nonCurrent.Count > 0;
        menu.Items.Add(deleteRoot);

        _branchMenu = menu;
    }

    // ---- branch handlers ---------------------------------------------

    private void OnSwitchBranchClicked(TrackedState s, string targetBranch)
    {
        if (s.RepoPath is null) return;

        try
        {
            var affected = GLoomRepository.ListAffectedGhFiles(s.RepoPath, targetBranch);
            var fileLines = affected.Count == 0
                ? "  (none tracked)"
                : string.Join("\n", affected.Select(f => "  " + f));

            var prompt = s.HasUnsavedChanges
                ? $"Switch to branch '{targetBranch}'?\n\n" +
                  "WARNING: you have unsaved canvas edits. They will be discarded " +
                  "and cannot be recovered.\n\n" +
                  $"All .gh files in this repo will be swapped to their state on " +
                  $"'{targetBranch}'. Affected files:\n{fileLines}"
                : $"Switch to branch '{targetBranch}'?\n\n" +
                  $"All .gh files in this repo will be swapped to their state on " +
                  $"'{targetBranch}'. Affected files:\n{fileLines}";

            var result = MessageBox.Show(prompt, "G-Loom: Switch branch",
                MessageBoxButtons.OKCancel, MessageBoxType.Warning);
            if (result != DialogResult.Ok) return;

            GLoomRepository.SwitchBranch(s.RepoPath, targetBranch);
            RhinoApp.WriteLine($"[G-Loom] Switched to branch '{targetBranch}'.");

            // ReloadAllInRepo's suspend scope already ends with a forced
            // tracker recompute, so no explicit tracker Refresh here.
            DocumentTracker.Instance.ReloadAllInRepo(s.RepoPath);
            // The branch isn't part of TrackedState, so StateChanged may not
            // fire if working-tree blobs happen to match the prior commit
            // (e.g. branching off the current HEAD). Re-render directly to
            // make sure the dropdown reflects the new branch.
            Refresh();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Switch failed: {ex.Message}");
            MessageBox.Show($"Switch failed:\n\n{ex.Message}",
                "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    private void OnCreateBranchClicked(TrackedState s)
    {
        if (s.RepoPath is null) return;

        var name = string.Empty;
        var ok = Rhino.UI.Dialogs.ShowEditBox(
            "G-Loom: New branch", "Name:", string.Empty, false, out name);
        if (!ok || string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();

        try
        {
            GLoomRepository.CreateBranch(s.RepoPath, trimmed, checkout: true);
            RhinoApp.WriteLine($"[G-Loom] Created and switched to branch '{trimmed}'.");
            DocumentTracker.Instance.Refresh();
            Refresh();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Branch creation failed: {ex.Message}");
            MessageBox.Show($"Branch creation failed:\n\n{ex.Message}",
                "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    private void OnRenameBranchClicked(TrackedState s, string oldName)
    {
        if (s.RepoPath is null) return;

        var newName = string.Empty;
        var ok = Rhino.UI.Dialogs.ShowEditBox(
            "G-Loom: Rename branch", $"Rename '{oldName}' to:", oldName, false, out newName);
        if (!ok || string.IsNullOrWhiteSpace(newName)) return;
        var trimmed = newName.Trim();
        if (trimmed == oldName) return;

        try
        {
            GLoomRepository.RenameBranch(s.RepoPath, oldName, trimmed);
            RhinoApp.WriteLine($"[G-Loom] Renamed branch '{oldName}' -> '{trimmed}'.");
            DocumentTracker.Instance.Refresh();
            Refresh();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Branch rename failed: {ex.Message}");
            MessageBox.Show($"Branch rename failed:\n\n{ex.Message}",
                "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    private void OnDeleteBranchClicked(TrackedState s, string targetBranch)
    {
        if (s.RepoPath is null) return;

        var prompt = $"Delete branch '{targetBranch}'?\n\nThis cannot be undone.";
        var result = MessageBox.Show(prompt, "G-Loom: Delete branch",
            MessageBoxButtons.OKCancel, MessageBoxType.Warning);
        if (result != DialogResult.Ok) return;

        try
        {
            GLoomRepository.DeleteBranch(s.RepoPath, targetBranch, force: false);
            RhinoApp.WriteLine($"[G-Loom] Deleted branch '{targetBranch}'.");
            DocumentTracker.Instance.Refresh();
            Refresh();
        }
        catch (Exception ex)
        {
            // The most common cause is unmerged commits ("not fully merged").
            // Offer force-delete; if that also fails, surface the error.
            var forcePrompt =
                $"Branch '{targetBranch}' has unmerged commits. Force-delete anyway?\n\n" +
                $"git error: {ex.Message}";
            var forceResult = MessageBox.Show(forcePrompt, "G-Loom: Force-delete branch",
                MessageBoxButtons.OKCancel, MessageBoxType.Warning);
            if (forceResult != DialogResult.Ok) return;

            try
            {
                GLoomRepository.DeleteBranch(s.RepoPath, targetBranch, force: true);
                RhinoApp.WriteLine($"[G-Loom] Force-deleted branch '{targetBranch}'.");
                DocumentTracker.Instance.Refresh();
                Refresh();
            }
            catch (Exception forceEx)
            {
                RhinoApp.WriteLine($"[G-Loom] Force-delete failed: {forceEx.Message}");
                MessageBox.Show($"Force-delete failed:\n\n{forceEx.Message}",
                    "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
            }
        }
    }

    // ---- remote handlers ---------------------------------------------

    private void OnAddRemoteClicked(TrackedState s)
    {
        if (s.RepoPath is null) return;

        var name = string.Empty;
        var ok = Rhino.UI.Dialogs.ShowEditBox(
            "G-Loom: Add remote", "Remote name:", "origin", false, out name);
        if (!ok || string.IsNullOrWhiteSpace(name)) return;
        var trimmedName = name.Trim();

        var url = string.Empty;
        ok = Rhino.UI.Dialogs.ShowEditBox(
            "G-Loom: Add remote", $"URL for '{trimmedName}':", string.Empty, false, out url);
        if (!ok || string.IsNullOrWhiteSpace(url)) return;
        var trimmedUrl = url.Trim();

        try
        {
            GLoomRepository.AddRemote(s.RepoPath, trimmedName, trimmedUrl);
            RhinoApp.WriteLine($"[G-Loom] Added remote '{trimmedName}' -> {trimmedUrl}");
            DocumentTracker.Instance.Refresh();
            Refresh();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Add remote failed: {ex.Message}");
            MessageBox.Show($"Add remote failed:\n\n{ex.Message}",
                "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    private void OnRemoveRemoteClicked(TrackedState s, string remoteName)
    {
        if (s.RepoPath is null) return;

        var prompt = $"Remove remote '{remoteName}'?\n\n" +
                     "This only removes the local mapping. The remote repo itself is untouched.";
        var result = MessageBox.Show(prompt, "G-Loom: Remove remote",
            MessageBoxButtons.OKCancel, MessageBoxType.Warning);
        if (result != DialogResult.Ok) return;

        try
        {
            GLoomRepository.RemoveRemote(s.RepoPath, remoteName);
            RhinoApp.WriteLine($"[G-Loom] Removed remote '{remoteName}'.");
            DocumentTracker.Instance.Refresh();
            Refresh();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Remove remote failed: {ex.Message}");
            MessageBox.Show($"Remove remote failed:\n\n{ex.Message}",
                "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    private void OnChangeRemoteUrlClicked(TrackedState s, string remoteName, string currentUrl)
    {
        if (s.RepoPath is null) return;

        var url = string.Empty;
        var ok = Rhino.UI.Dialogs.ShowEditBox(
            "G-Loom: Change remote URL",
            $"New URL for '{remoteName}':", currentUrl, false, out url);
        if (!ok || string.IsNullOrWhiteSpace(url)) return;
        var trimmed = url.Trim();
        if (trimmed == currentUrl) return;

        try
        {
            GLoomRepository.SetRemoteUrl(s.RepoPath, remoteName, trimmed);
            RhinoApp.WriteLine($"[G-Loom] Changed '{remoteName}' URL -> {trimmed}");
            DocumentTracker.Instance.Refresh();
            Refresh();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Change URL failed: {ex.Message}");
            MessageBox.Show($"Change URL failed:\n\n{ex.Message}",
                "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    private void OnSetUpstreamClicked(TrackedState s, string branch, string remoteName, string remoteBranch)
    {
        if (s.RepoPath is null) return;

        try
        {
            GLoomRepository.SetUpstream(s.RepoPath, branch, remoteName, remoteBranch);
            RhinoApp.WriteLine($"[G-Loom] Upstream for '{branch}' -> {remoteName}/{remoteBranch}");
            DocumentTracker.Instance.Refresh();
            Refresh();
        }
        catch (Exception ex)
        {
            // git fails with a clear "the requested upstream branch ... does not
            // exist" when the remote branch hasn't been pushed yet; surface that
            // alongside a hint about pushing first.
            RhinoApp.WriteLine($"[G-Loom] Set upstream failed: {ex.Message}");
            MessageBox.Show(
                $"Set upstream failed:\n\n{ex.Message}\n\n" +
                $"If the branch hasn't been pushed yet, use Push (which auto-sets upstream).",
                "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    // ---- sync handler ------------------------------------------------

    /// <summary>
    /// One-button Smart Sync: fetch first to refresh ahead/behind, then if
    /// behind pull --ff-only, then if ahead push. The button label hints at
    /// which leg will actually run (Sync / Push / Pull) based on the last
    /// observed state, but the action always does the full sequence so users
    /// who haven't refreshed lately still get the right outcome.
    /// </summary>
    private void OnSyncClicked()
    {
        var s = DocumentTracker.Instance.State;
        if (s.RepoPath is null) return;

        try
        {
            var status = GLoomRepository.GetStatus(s.RepoPath);
            var branch = status.Branch;
            if (string.IsNullOrEmpty(branch) || branch == "(detached)")
            {
                MessageBox.Show("Cannot sync from a detached HEAD.",
                    "G-Loom", MessageBoxButtons.OK, MessageBoxType.Warning);
                return;
            }

            var remotes = GLoomRepository.GetRemotes(s.RepoPath);
            if (remotes.Count == 0)
            {
                MessageBox.Show("No remote configured. Use the Remote dropdown to add one.",
                    "G-Loom", MessageBoxButtons.OK, MessageBoxType.Warning);
                return;
            }

            var upstream = GLoomRepository.GetUpstream(s.RepoPath, branch);
            var remoteName = upstream?.Remote ?? remotes[0].Name;
            var remoteBranch = upstream?.RemoteBranch ?? branch;

            _syncButton.Enabled = false;
            _syncStatusLabel.Text = $"Fetching {remoteName}...";

            var fetch = GLoomRepository.Fetch(s.RepoPath, remoteName);
            if (!fetch.Success)
            {
                ReportSyncFailure("Fetch", fetch.Message);
                return;
            }
            RhinoApp.WriteLine($"[G-Loom] Fetched {remoteName}.");

            // No upstream yet: this is a fresh branch; push with -u and we're done.
            if (upstream is null)
            {
                _syncStatusLabel.Text = $"Pushing to {remoteName}/{branch}...";
                var firstPush = GLoomRepository.Push(s.RepoPath, remoteName, branch, setUpstream: true);
                if (!firstPush.Success)
                {
                    ReportSyncFailure("Push", firstPush.Message);
                    return;
                }
                RhinoApp.WriteLine($"[G-Loom] Pushed {branch} -> {remoteName} (upstream set).");
                FinishSync();
                return;
            }

            // Recompute ahead/behind after the fetch.
            var ab = GLoomRepository.GetAheadBehind(s.RepoPath, branch);

            if (ab.Behind > 0)
            {
                _syncStatusLabel.Text = $"Pulling from {remoteName}/{remoteBranch}...";
                var pull = GLoomRepository.Pull(s.RepoPath, remoteName, remoteBranch);
                if (!pull.Success)
                {
                    ReportSyncFailure("Pull", pull.Message +
                        "\n\nTip: G-Loom only supports fast-forward pulls today. If the branches " +
                        "have diverged, real merges will land in a future phase.");
                    return;
                }
                RhinoApp.WriteLine($"[G-Loom] Pulled from {remoteName}/{remoteBranch}.");

                // The pull may have changed the working tree - reload every
                // affected .gh so open canvases reflect the new state.
                DocumentTracker.Instance.ReloadAllInRepo(s.RepoPath);

                // Recompute ahead/behind after the pull.
                ab = GLoomRepository.GetAheadBehind(s.RepoPath, branch);
            }

            if (ab.Ahead > 0)
            {
                _syncStatusLabel.Text = $"Pushing to {remoteName}/{remoteBranch}...";
                var push = GLoomRepository.Push(s.RepoPath, remoteName, remoteBranch, setUpstream: false);
                if (!push.Success)
                {
                    ReportSyncFailure("Push", push.Message);
                    return;
                }
                RhinoApp.WriteLine($"[G-Loom] Pushed to {remoteName}/{remoteBranch}.");
            }

            FinishSync();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Sync failed: {ex.Message}");
            ReportSyncFailure("Sync", ex.Message);
        }
    }

    private void FinishSync()
    {
        DocumentTracker.Instance.Refresh();
        Refresh();
    }

    private void ReportSyncFailure(string stage, string message)
    {
        MessageBox.Show($"{stage} failed:\n\n{message}",
            "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
        Refresh();
    }

    // ---- tag handlers ------------------------------------------------

    private void OnCreateTagClicked(string commitSha, string commitMessage)
    {
        var s = DocumentTracker.Instance.State;
        if (s.RepoPath is null) return;

        var dialog = new TagCreationDialog(commitSha, commitMessage);
        var result = dialog.ShowModal();
        if (result is null) return;

        var sha7 = commitSha[..7];
        try
        {
            // Annotated tag with metadata embedded as JSON in the message.
            // Schema v2 covers: toolchain (always), free-text notes, and
            // optional AEC/Product/Release sub-records the user filled in.
            var metadata = new TagMetadata(
                SchemaVersion: 2,
                TagName: result.Name,
                CommitSha: commitSha,
                CreatedAt: DateTimeOffset.UtcNow,
                CreatedBy: "iSamacA",
                Toolchain: ToolchainSnapshot.Capture(),
                Notes: result.Notes,
                Aec: result.Aec,
                Product: result.Product,
                Release: result.Release);
            var json = TagMetadataJson.Write(metadata);

            GLoomRepository.CreateAnnotatedTag(
                s.RepoPath, result.Name, commitSha, json,
                taggerName: "iSamacA", taggerEmail: "samaca163@gmail.com");
            RhinoApp.WriteLine($"[G-Loom] Tagged {sha7} as '{result.Name}'.");
            DocumentTracker.Instance.Refresh();
            Refresh();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Tag failed: {ex.Message}");
            MessageBox.Show($"Tag failed:\n\n{ex.Message}",
                "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    private void OnDeleteTagClicked(string tagName)
    {
        var s = DocumentTracker.Instance.State;
        if (s.RepoPath is null) return;

        var prompt = $"Delete tag '{tagName}'?\n\nThis cannot be undone.";
        var result = MessageBox.Show(prompt, "G-Loom: Delete tag",
            MessageBoxButtons.OKCancel, MessageBoxType.Warning);
        if (result != DialogResult.Ok) return;

        try
        {
            GLoomRepository.DeleteTag(s.RepoPath, tagName);
            RhinoApp.WriteLine($"[G-Loom] Deleted tag '{tagName}'.");
            DocumentTracker.Instance.Refresh();
            Refresh();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Tag delete failed: {ex.Message}");
            MessageBox.Show($"Tag delete failed:\n\n{ex.Message}",
                "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    // ---- commit handler ----------------------------------------------

    private void OnCommitClicked()
    {
        var s = DocumentTracker.Instance.State;
        if (!s.IsTracked || s.Document is null || s.RepoPath is null || s.FilePath is null) return;

        try
        {
            string? sha;
            string msg;

            // The mid-commit IsModified=false below raises ModifiedChanged,
            // which used to trigger a full tracker recompute (and a panel
            // refresh, and an overlay recompute) before the commit even ran.
            // The scope swallows all of it; its dispose performs the single
            // post-commit recompute, so the explicit Refresh() is gone too.
            using (DocumentTracker.Instance.SuspendUpdates())
            {
                // If the canvas has unsaved edits, persist the .gh to disk first.
                // Otherwise the committed pair (.gh + .gloom.json) is split-brain:
                // the JSON reflects live state but the .gh is whatever was last
                // saved - so a future Restore would bring back stale canvas
                // content. Also: DocumentSerializer is structural-only (Phase 1a),
                // so a slider/panel-text edit produces an identical JSON; without
                // saving the .gh first, git sees no changes and the commit is a no-op.
                if (s.HasUnsavedChanges)
                {
                    var io = new GH_DocumentIO { Document = s.Document };
                    if (!io.SaveQuiet(s.FilePath))
                    {
                        MessageBox.Show(
                            "Could not save the .gh file before commit. Aborting.",
                            "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
                        return;
                    }
                    s.Document.IsModified = false;
                }

                var canonical = DocumentSerializer.Serialize(s.Document);
                var json = CanonicalJson.Write(canonical);

                var ghBase = Path.GetFileNameWithoutExtension(s.FilePath);
                var jsonFull = s.CanonicalJsonFullPath!;
                var ghRel = Path.GetRelativePath(s.RepoPath, s.FilePath);
                var jsonRel = Path.GetRelativePath(s.RepoPath, jsonFull);
                // Count commits touching either file. JSON-only would miss
                // slider-only commits (Phase 1a serializer is structural-only,
                // so those commits don't touch the JSON), causing the message
                // to repeat a previous version number.
                var nextV = CommitVersioning.NextVersion(s.RepoPath, ghRel, jsonRel);
                msg = CommitVersioning.FormatMessage(ghBase, nextV);

                sha = GLoomRepository.Commit(
                    s.RepoPath, json, jsonFull, msg,
                    authorName: "iSamacA", authorEmail: "samaca163@gmail.com",
                    alsoStageFullPath: s.FilePath);
            }

            if (sha is null)
            {
                MessageBox.Show("Nothing to commit (no changes since last commit).",
                    "G-Loom", MessageBoxButtons.OK, MessageBoxType.Information);
            }
            else
            {
                RhinoApp.WriteLine($"[G-Loom] Committed {sha[..7]} ({msg})");
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Commit failed: {ex.Message}");
            MessageBox.Show($"Commit failed:\n\n{ex.Message}",
                "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    // ---- restore handler ---------------------------------------------

    private void OnRestoreClicked(string sha, string message)
    {
        var s = DocumentTracker.Instance.State;
        if (!s.IsTracked || s.RepoPath is null || s.FilePath is null) return;

        var sha7 = sha[..7];
        var prompt = s.HasUnsavedChanges
            ? $"Restore commit {sha7} ({message})?\n\n" +
              "WARNING: you have unsaved canvas edits. They will be discarded " +
              "and cannot be recovered.\n\n" +
              "The .gh file on disk will be replaced with this version and " +
              "reloaded automatically."
            : $"Restore commit {sha7} ({message})?\n\n" +
              "Your current canvas state will be replaced with this version, and the " +
              "file will be reloaded automatically.";

        var result = MessageBox.Show(prompt, "G-Loom: Restore version",
            MessageBoxButtons.OKCancel, MessageBoxType.Warning);
        if (result != DialogResult.Ok) return;

        try
        {
            var ghRel = Path.GetRelativePath(s.RepoPath, s.FilePath);
            var jsonRel = Path.GetRelativePath(s.RepoPath, s.CanonicalJsonFullPath!);
            var filePath = s.FilePath;
            GH_Document? newDoc;

            // The reload fires DocumentRemoved + DocumentAdded, each a full
            // tracker recompute; the scope coalesces them into one at dispose.
            using (DocumentTracker.Instance.SuspendUpdates())
            {
                // 1. Replace the on-disk files with their content at <sha>.
                GLoomRepository.Restore(s.RepoPath, sha, new[] { ghRel, jsonRel });
                RhinoApp.WriteLine($"[G-Loom] Checked out {sha7} ({message}) on disk.");

                // 2. Reload the .gh from disk in-place. The tracker's forced
                //    recompute at scope end re-derives "current commit" from
                //    the working-tree blob pair. No explicit marker needed.
                newDoc = DocumentTracker.Instance.ReloadFromDisk(filePath);
            }

            if (newDoc is null)
            {
                MessageBox.Show(
                    $"Files restored to {sha7}, but the live canvas reload failed. " +
                    $"Close and reopen {Path.GetFileName(filePath)} manually to see the change.",
                    "G-Loom", MessageBoxButtons.OK, MessageBoxType.Warning);
            }
            else
            {
                RhinoApp.WriteLine($"[G-Loom] Reloaded {Path.GetFileName(filePath)} - canvas now matches {sha7}.");
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Restore failed: {ex.Message}");
            MessageBox.Show($"Restore failed:\n\n{ex.Message}",
                "G-Loom", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    // ===== Commit row control =========================================

    private sealed class CommitRow : Panel
    {
        public CommitRow(
            GLoomRepository.CommitInfo info,
            bool isCurrent,
            bool isActiveReference,
            IReadOnlyList<string>? forkParents,
            IReadOnlyList<GLoomRepository.TagInfo>? tags,
            bool initiallyExpanded,
            Func<DocumentDiff?> diffProvider,
            Action onRestore,
            Action onToggleReference,
            Action onCreateTag,
            Action<string> onDeleteTag,
            Action<bool> onExpansionChanged)
        {
            var version = CommitVersioning.ExtractVersionLabel(info.Message) ?? "—";
            var sha7 = info.Sha[..7];
            var date = info.When.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

            var versionLabel = new Label
            {
                Text = isCurrent ? $"▶ {version}" : version,
                Font = isCurrent ? new Font(SystemFont.Bold, 11) : new Font(SystemFont.Default, 11),
            };
            var msgLabel = new Label { Text = info.Message, Wrap = WrapMode.Word };
            var shaLabel = new Label { Text = sha7 };
            var dateLabel = new Label { Text = date };
            var restoreBtn = new Button
            {
                Text = "↺",
                ToolTip = "Restore this version to the working tree",
                Size = new Size(26, 26),
            };
            restoreBtn.Click += (_, _) => onRestore();

            var compareBtn = new Button
            {
                Text = isActiveReference ? "◉" : "◎",
                ToolTip = isActiveReference
                    ? "Stop comparing on canvas (return to HEAD)"
                    : "Use this version as the overlay reference (compare live vs this on canvas)",
                Size = new Size(26, 26),
            };
            compareBtn.Click += (_, _) => onToggleReference();

            // Drawer body = tags section + a slot we fill with the diff
            // section on first expansion (lazy so the panel doesn't pay
            // N git-show calls per refresh just to populate sections the
            // user may never open).
            var tagsPanel = BuildDetailsPanel(tags, onCreateTag, onDeleteTag);
            var diffSlot = new Panel();
            var drawerStack = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
            };
            drawerStack.Items.Add(tagsPanel);
            if (!isCurrent) drawerStack.Items.Add(diffSlot);

            var details = new Panel { Content = drawerStack, Visible = initiallyExpanded };

            var diffLoaded = false;
            void EnsureDiffLoaded()
            {
                if (diffLoaded || isCurrent) return;
                diffLoaded = true;
                diffSlot.Content = BuildDiffSection(diffProvider());
            }

            if (initiallyExpanded) EnsureDiffLoaded();

            var toggleBtn = new Button
            {
                Text = initiallyExpanded ? "▴" : "▾",
                ToolTip = "Show commit details (tags, changes from this version)",
                Size = new Size(26, 26),
            };
            toggleBtn.Click += (_, _) =>
            {
                var newState = !details.Visible;
                details.Visible = newState;
                toggleBtn.Text = newState ? "▴" : "▾";
                if (newState) EnsureDiffLoaded();
                onExpansionChanged(newState);
            };

            var mainRow = new TableLayout
            {
                Spacing = new Size(8, 0),
                Rows =
                {
                    new TableRow(
                        new TableCell(versionLabel, false),
                        new TableCell(shaLabel, false),
                        new TableCell(msgLabel, true),
                        new TableCell(dateLabel, false),
                        new TableCell(compareBtn, false),
                        new TableCell(restoreBtn, false),
                        new TableCell(toggleBtn, false)),
                },
            };

            Padding = new Padding(12, 4, 12, 4);

            var rows = new TableLayout { Spacing = new Size(0, 2) };
            if (forkParents is { Count: > 0 })
            {
                var badge = new Label
                {
                    Text = "↰ branched from " + string.Join(" · ", forkParents),
                    Font = new Font(SystemFont.Default, 9),
                    TextColor = Color.FromGrayscale(0.5f),
                };
                rows.Rows.Add(new TableRow(badge));
            }
            rows.Rows.Add(new TableRow(mainRow));
            rows.Rows.Add(new TableRow(details));
            Content = rows;
        }

        private static Panel BuildDetailsPanel(
            IReadOnlyList<GLoomRepository.TagInfo>? tags,
            Action onCreateTag,
            Action<string> onDeleteTag)
        {
            var stack = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(20, 6, 12, 6),
            };

            var tagsHeader = new Label
            {
                Text = "Tags",
                Font = new Font(SystemFont.Bold, 9),
                TextColor = Color.FromGrayscale(0.5f),
            };
            stack.Items.Add(tagsHeader);

            if (tags is { Count: > 0 })
            {
                foreach (var t in tags)
                    stack.Items.Add(BuildTagBlock(t, onDeleteTag));
            }
            else
            {
                stack.Items.Add(new Label
                {
                    Text = "(no tags yet)",
                    TextColor = Color.FromGrayscale(0.55f),
                });
            }

            var addBtn = new Button { Text = "+ add tag" };
            addBtn.Click += (_, _) => onCreateTag();
            stack.Items.Add(addBtn);

            return new Panel { Content = stack };
        }

        private static StackLayout BuildTagBlock(
            GLoomRepository.TagInfo tag,
            Action<string> onDeleteTag)
        {
            var name = tag.Name;
            var nameLabel = new Label
            {
                Text = name,
                Font = new Font(SystemFont.Bold, 10),
            };
            var deleteBtn = new Button
            {
                Text = "×",
                ToolTip = $"Delete tag '{name}'",
                Size = new Size(22, 22),
            };
            deleteBtn.Click += (_, _) => onDeleteTag(name);

            var headerRow = new TableLayout
            {
                Spacing = new Size(6, 0),
                Rows =
                {
                    new TableRow(
                        new TableCell(nameLabel, true),
                        new TableCell(deleteBtn, false)),
                },
            };

            var block = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 1,
                Padding = new Padding(0, 2, 0, 4),
            };
            block.Items.Add(headerRow);

            var meta = TagMetadataJson.TryRead(tag.Message);
            if (meta is not null)
            {
                block.Items.Add(MetaLine(
                    $"Captured {meta.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} by {meta.CreatedBy}"));
                var tc = meta.Toolchain;
                var parts = new System.Collections.Generic.List<string>
                {
                    $"Rhino {tc.Rhino}",
                    $"GH {tc.Grasshopper}",
                };
                if (!string.IsNullOrEmpty(tc.RhinoInsideRevit))
                    parts.Add($"RiR {tc.RhinoInsideRevit}");
                parts.Add($"G-Loom {tc.Gloom}");
                block.Items.Add(MetaLine(string.Join("  ·  ", parts)));

                if (!string.IsNullOrEmpty(meta.Notes))
                    block.Items.Add(MetaLine($"Notes: {meta.Notes}"));

                if (meta.Aec is { } aec)
                    AppendModeBlock(block, "AEC", new[]
                    {
                        Field("Phase", aec.Phase),
                        Field("Submittal", aec.Submittal),
                        Field("Sheet set", aec.SheetSet),
                        Field("Notes", aec.Notes),
                    });

                if (meta.Product is { } prod)
                    AppendModeBlock(block, "Product", new[]
                    {
                        Field("SKU", prod.Sku),
                        Field("Variant", prod.Variant),
                        Field("Notes", prod.Notes),
                    });

                if (meta.Release is { } rel)
                    AppendModeBlock(block, "Release", new[]
                    {
                        Field("Version", rel.Version),
                        Field("Notes", rel.Notes),
                    });
            }
            else
            {
                block.Items.Add(MetaLine("(no toolchain metadata captured)"));
            }

            return block;
        }

        private static (string Label, string? Value) Field(string label, string? value) =>
            (label, value);

        private static void AppendModeBlock(
            StackLayout parent, string title, (string Label, string? Value)[] fields)
        {
            var hasAny = false;
            foreach (var f in fields) if (!string.IsNullOrEmpty(f.Value)) { hasAny = true; break; }
            if (!hasAny) return;

            var header = new Label
            {
                Text = title,
                Font = new Font(SystemFont.Bold, 9),
                TextColor = Color.FromGrayscale(0.4f),
            };
            parent.Items.Add(header);
            foreach (var f in fields)
            {
                if (string.IsNullOrEmpty(f.Value)) continue;
                parent.Items.Add(MetaLine($"   {f.Label}: {f.Value}"));
            }
        }

        private static Label MetaLine(string text) => new()
        {
            Text = text,
            Font = new Font(SystemFont.Default, 9),
            TextColor = Color.FromGrayscale(0.5f),
            Wrap = WrapMode.Word,
        };

        private static Panel BuildDiffSection(DocumentDiff? diff)
        {
            var stack = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                Padding = new Padding(20, 6, 12, 6),
            };

            var headerText = diff is null
                ? "Changes from this version (unavailable)"
                : diff.IsEmpty
                    ? "Changes from this version (none)"
                    : $"Changes from this version ({diff.TotalChanges})";

            stack.Items.Add(new Label
            {
                Text = headerText,
                Font = new Font(SystemFont.Bold, 9),
                TextColor = Color.FromGrayscale(0.5f),
            });

            if (diff is null)
            {
                stack.Items.Add(new Label
                {
                    Text = "(historic .gloom.json couldn't be read)",
                    TextColor = Color.FromGrayscale(0.55f),
                });
                return new Panel { Content = stack };
            }

            if (diff.IsEmpty) return new Panel { Content = stack };

            AppendCategory(stack, "Added",
                diff.ObjectsAdded.Select(DocumentDiff.DisplayName).ToList());
            AppendCategory(stack, "Removed",
                diff.ObjectsRemoved.Select(DocumentDiff.DisplayName).ToList());
            AppendCategoryWithSummary(stack, "Modified",
                diff.ObjectsModified
                    .Select(c => (DocumentDiff.DisplayName(c.To), c.Summary))
                    .ToList());

            if (diff.GroupsAdded.Count > 0 || diff.GroupsRemoved.Count > 0 || diff.GroupsModified.Count > 0)
            {
                AppendCategory(stack, "Groups added",
                    diff.GroupsAdded.Select(g => g.Name).ToList());
                AppendCategory(stack, "Groups removed",
                    diff.GroupsRemoved.Select(g => g.Name).ToList());
                AppendCategoryWithSummary(stack, "Groups modified",
                    diff.GroupsModified
                        .Select(c => (c.To.Name, c.Summary))
                        .ToList());
            }

            return new Panel { Content = stack };
        }

        private static void AppendCategory(
            StackLayout parent, string title, IReadOnlyList<string> items)
        {
            if (items.Count == 0) return;
            parent.Items.Add(new Label
            {
                Text = $"{title} ({items.Count})",
                Font = new Font(SystemFont.Bold, 9),
                TextColor = Color.FromGrayscale(0.4f),
            });
            foreach (var item in items)
                parent.Items.Add(MetaLine($"   • {item}"));
        }

        private static void AppendCategoryWithSummary(
            StackLayout parent, string title,
            IReadOnlyList<(string Name, string Summary)> items)
        {
            if (items.Count == 0) return;
            parent.Items.Add(new Label
            {
                Text = $"{title} ({items.Count})",
                Font = new Font(SystemFont.Bold, 9),
                TextColor = Color.FromGrayscale(0.4f),
            });
            foreach (var (name, summary) in items)
                parent.Items.Add(MetaLine($"   • {name} — {summary}"));
        }
    }
}
