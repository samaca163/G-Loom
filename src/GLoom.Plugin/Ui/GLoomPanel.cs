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
    private readonly Label _currentVersionLabel = NewValueLabel();
    private readonly Label _lastCommitLabel = NewValueLabel();
    private readonly Label _nextVersionLabel = NewValueLabel();
    private readonly Label _dirtyLabel = NewValueLabel();
    private readonly Button _commitButton;
    private readonly Button _refreshButton;
    private ContextMenu? _branchMenu;

    // History list
    private readonly Label _historyHeaderLabel = new() { Text = "History" };
    private readonly StackLayout _historyContainer = new() { Orientation = Eto.Forms.Orientation.Vertical, Spacing = 0 };
    private readonly Button _showMoreButton;
    private int _historyLimit = DefaultHistoryLimit;

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
                LabelRow("Current version:", _currentVersionLabel),
                LabelRow("Last commit:",     _lastCommitLabel),
                LabelRow("Next version:",    _nextVersionLabel),
                LabelRow("State:",           _dirtyLabel),
                new TableRow(_commitButton),
                new TableRow(_refreshButton),
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

            RefreshHistory(s, currentSha, fileScope);
        }
        catch (Exception ex)
        {
            _commitButton.Enabled = false;
            DisableBranchControl();
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

    private void RefreshHistory(TrackedState s, string? currentSha, IReadOnlyList<string> fileScope)
    {
        if (s.RepoPath is null || s.FilePath is null) { _historyContainer.Items.Clear(); _showMoreButton.Visible = false; return; }

        var commits = GLoomRepository.Log(s.RepoPath, _historyLimit, fileScope);

        _historyContainer.Items.Clear();
        foreach (var c in commits)
        {
            var isCurrent = currentSha is not null && c.Sha == currentSha;
            var row = new CommitRow(
                info: c,
                isCurrent: isCurrent,
                onRestore: () => OnRestoreClicked(c.Sha, c.Message));
            _historyContainer.Items.Add(new StackLayoutItem(row, HorizontalAlignment.Stretch));
        }

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

            DocumentTracker.Instance.ReloadAllInRepo(s.RepoPath);
            DocumentTracker.Instance.Refresh();
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

    // ---- commit handler ----------------------------------------------

    private void OnCommitClicked()
    {
        var s = DocumentTracker.Instance.State;
        if (!s.IsTracked || s.Document is null || s.RepoPath is null || s.FilePath is null) return;

        try
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
            var msg = CommitVersioning.FormatMessage(ghBase, nextV);

            var sha = GLoomRepository.Commit(
                s.RepoPath, json, jsonFull, msg,
                authorName: "iSamacA", authorEmail: "samaca163@gmail.com",
                alsoStageFullPath: s.FilePath);

            if (sha is null)
            {
                MessageBox.Show("Nothing to commit (no changes since last commit).",
                    "G-Loom", MessageBoxButtons.OK, MessageBoxType.Information);
            }
            else
            {
                RhinoApp.WriteLine($"[G-Loom] Committed {sha[..7]} ({msg})");
                DocumentTracker.Instance.Refresh();
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

            // 1. Replace the on-disk files with their content at <sha>.
            GLoomRepository.Restore(s.RepoPath, sha, new[] { ghRel, jsonRel });
            RhinoApp.WriteLine($"[G-Loom] Checked out {sha7} ({message}) on disk.");

            // 2. Reload the .gh from disk in-place. The DocumentServer's
            //    DocumentRemoved/Added events fire, so the tracker re-resolves
            //    the active document - and the new "current commit" derivation
            //    in DocumentTracker reads the working tree blob hash to figure
            //    out which commit we're now on. No explicit marker needed.
            var filePath = s.FilePath;
            var newDoc = DocumentTracker.Instance.ReloadFromDisk(filePath);

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
            Action onRestore)
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
                Text = "Restore",
                ToolTip = "Restore this version to the working tree",
            };
            restoreBtn.Click += (_, _) => onRestore();

            Padding = new Padding(12, 4, 12, 4);
            Content = new TableLayout
            {
                Spacing = new Size(8, 0),
                Rows =
                {
                    new TableRow(
                        new TableCell(versionLabel, false),
                        new TableCell(shaLabel, false),
                        new TableCell(msgLabel, true),
                        new TableCell(dateLabel, false),
                        new TableCell(restoreBtn, false)),
                },
            };
        }
    }
}
