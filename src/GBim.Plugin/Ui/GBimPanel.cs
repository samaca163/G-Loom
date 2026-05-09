using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Eto.Drawing;
using GBim.Serialization;
using GBim.Vcs;
using Rhino;

// The plugin compiles against System.Windows.Forms (for GH_Canvas), so we
// alias every Eto.Forms type used here to keep names unambiguous.
using Application = Eto.Forms.Application;
using Button = Eto.Forms.Button;
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
using Size = Eto.Drawing.Size;
using StackLayout = Eto.Forms.StackLayout;
using StackLayoutItem = Eto.Forms.StackLayoutItem;
using TableCell = Eto.Forms.TableCell;
using TableLayout = Eto.Forms.TableLayout;
using TableRow = Eto.Forms.TableRow;
using WrapMode = Eto.Forms.WrapMode;

namespace GBim.Ui;

[Guid("55f07e53-ad04-44a9-ab21-059f32207842")]
public sealed class GBimPanel : Panel
{
    public static Guid PanelId => typeof(GBimPanel).GUID;

    private const int DefaultHistoryLimit = 10;
    private const int HistoryIncrement = 10;

    // Top metadata labels
    private readonly Label _filePathLabel = NewValueLabel();
    private readonly Label _repoLabel = NewValueLabel();
    private readonly Label _branchLabel = NewValueLabel();
    private readonly Label _currentVersionLabel = NewValueLabel();
    private readonly Label _lastCommitLabel = NewValueLabel();
    private readonly Label _nextVersionLabel = NewValueLabel();
    private readonly Label _dirtyLabel = NewValueLabel();
    private readonly Button _commitButton;
    private readonly Button _refreshButton;

    // History list
    private readonly Label _historyHeaderLabel = new() { Text = "History" };
    private readonly StackLayout _historyContainer = new() { Orientation = Eto.Forms.Orientation.Vertical, Spacing = 0 };
    private readonly Button _showMoreButton;
    private int _historyLimit = DefaultHistoryLimit;

    public GBimPanel()
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

        var headerFont = new Font(SystemFont.Bold, 13);
        var sectionFont = new Font(SystemFont.Bold, 11);
        _historyHeaderLabel.Font = sectionFont;

        var meta = new TableLayout
        {
            Padding = new Padding(12, 12, 12, 6),
            Spacing = new Size(8, 6),
            Rows =
            {
                new TableRow(new Label { Text = "G-BIM", Font = headerFont }),
                LabelRow("File:",            _filePathLabel),
                LabelRow("Repository:",      _repoLabel),
                LabelRow("Branch:",          _branchLabel),
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

    private static TableRow LabelRow(string caption, Label valueLabel) =>
        new(new TableCell(new Label { Text = caption }, false),
            new TableCell(valueLabel, true));

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
            var status = GBimRepository.GetStatus(s.RepoPath, fileScope);
            _branchLabel.Text = status.Branch;
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
            _branchLabel.Text = "-";
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
        var recent = GBimRepository.Log(s.RepoPath!, Math.Max(_historyLimit, 50), fileScope);
        foreach (var c in recent)
            if (c.Sha == currentSha)
                return CommitVersioning.ExtractVersionLabel(c.Message)
                       ?? c.Message;

        return currentSha[..7];
    }

    private void RefreshHistory(TrackedState s, string? currentSha, IReadOnlyList<string> fileScope)
    {
        if (s.RepoPath is null || s.FilePath is null) { _historyContainer.Items.Clear(); _showMoreButton.Visible = false; return; }

        var commits = GBimRepository.Log(s.RepoPath, _historyLimit, fileScope);

        // Restore is allowed unless the user has UNSAVED canvas edits in the
        // active document. We deliberately do NOT use `git diff` here:
        // chained restores naturally leave the working tree differing from
        // HEAD ("V003 on disk, HEAD at V005") even though the user has made
        // no edits, and we want the buttons enabled in that state. The
        // confirm dialog is the safety net for the saved-but-uncommitted case.
        var canRestore = !s.HasUnsavedChanges;

        _historyContainer.Items.Clear();
        foreach (var c in commits)
        {
            var isCurrent = currentSha is not null && c.Sha == currentSha;
            var row = new CommitRow(
                info: c,
                isCurrent: isCurrent,
                restoreEnabled: canRestore,
                disabledReason: canRestore ? null : "unsaved canvas edits - save and commit, or undo them first",
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
        _branchLabel.Text = "-";
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
        _branchLabel.Text = "-";
        _currentVersionLabel.Text = "-";
        _lastCommitLabel.Text = "-";
        _nextVersionLabel.Text = "-";
        _dirtyLabel.Text = dirty ? "unsaved changes" : "clean";
        _commitButton.Enabled = false;
        _historyContainer.Items.Clear();
        _historyHeaderLabel.Text = "History";
        _showMoreButton.Visible = false;
    }

    // ---- commit handler ----------------------------------------------

    private void OnCommitClicked()
    {
        var s = DocumentTracker.Instance.State;
        if (!s.IsTracked || s.Document is null || s.RepoPath is null || s.FilePath is null) return;

        try
        {
            var canonical = DocumentSerializer.Serialize(s.Document);
            var json = CanonicalJson.Write(canonical);

            var ghBase = Path.GetFileNameWithoutExtension(s.FilePath);
            var jsonFull = s.CanonicalJsonFullPath!;
            var jsonRel = Path.GetRelativePath(s.RepoPath, jsonFull);
            var nextV = CommitVersioning.NextVersion(s.RepoPath, jsonRel);
            var msg = CommitVersioning.FormatMessage(ghBase, nextV);

            var sha = GBimRepository.Commit(
                s.RepoPath, json, jsonFull, msg,
                authorName: "iSamacA", authorEmail: "samaca163@gmail.com",
                alsoStageFullPath: s.FilePath);

            if (sha is null)
            {
                MessageBox.Show("Nothing to commit (no changes since last commit).",
                    "G-BIM", MessageBoxButtons.OK, MessageBoxType.Information);
            }
            else
            {
                RhinoApp.WriteLine($"[G-BIM] Committed {sha[..7]} ({msg})");
                DocumentTracker.Instance.Refresh();
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-BIM] Commit failed: {ex.Message}");
            MessageBox.Show($"Commit failed:\n\n{ex.Message}",
                "G-BIM", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    // ---- restore handler ---------------------------------------------

    private void OnRestoreClicked(string sha, string message)
    {
        var s = DocumentTracker.Instance.State;
        if (!s.IsTracked || s.RepoPath is null || s.FilePath is null) return;

        var sha7 = sha[..7];
        var prompt =
            $"Restore commit {sha7} ({message})?\n\n" +
            "Your current canvas state will be replaced with this version, and the " +
            "file will be reloaded automatically.";

        var result = MessageBox.Show(prompt, "G-BIM: Restore version",
            MessageBoxButtons.OKCancel, MessageBoxType.Warning);
        if (result != DialogResult.Ok) return;

        try
        {
            var ghRel = Path.GetRelativePath(s.RepoPath, s.FilePath);
            var jsonRel = Path.GetRelativePath(s.RepoPath, s.CanonicalJsonFullPath!);

            // 1. Replace the on-disk files with their content at <sha>.
            GBimRepository.Restore(s.RepoPath, sha, new[] { ghRel, jsonRel });
            RhinoApp.WriteLine($"[G-BIM] Checked out {sha7} ({message}) on disk.");

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
                    "G-BIM", MessageBoxButtons.OK, MessageBoxType.Warning);
            }
            else
            {
                RhinoApp.WriteLine($"[G-BIM] Reloaded {Path.GetFileName(filePath)} - canvas now matches {sha7}.");
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-BIM] Restore failed: {ex.Message}");
            MessageBox.Show($"Restore failed:\n\n{ex.Message}",
                "G-BIM", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    // ===== Commit row control =========================================

    private sealed class CommitRow : Panel
    {
        public CommitRow(
            GBimRepository.CommitInfo info,
            bool isCurrent,
            bool restoreEnabled,
            string? disabledReason,
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
                Enabled = restoreEnabled,
                ToolTip = restoreEnabled ? "Restore this version to the working tree" : disabledReason,
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
