using System;
using System.IO;
using System.Runtime.InteropServices;
using Eto.Drawing;
using GBim.Serialization;
using GBim.Vcs;
using LibGit2Sharp;
using Rhino;

// The plugin compiles against System.Windows.Forms (for GH_Canvas), so we
// alias every Eto.Forms type used here to keep names unambiguous.
using Application = Eto.Forms.Application;
using Button = Eto.Forms.Button;
using Font = Eto.Drawing.Font;
using Label = Eto.Forms.Label;
using MessageBox = Eto.Forms.MessageBox;
using MessageBoxButtons = Eto.Forms.MessageBoxButtons;
using MessageBoxType = Eto.Forms.MessageBoxType;
using Padding = Eto.Drawing.Padding;
using Panel = Eto.Forms.Panel;
using Size = Eto.Drawing.Size;
using TableCell = Eto.Forms.TableCell;
using TableLayout = Eto.Forms.TableLayout;
using TableRow = Eto.Forms.TableRow;
using WrapMode = Eto.Forms.WrapMode;

namespace GBim.Ui;

[Guid("55f07e53-ad04-44a9-ab21-059f32207842")]
public sealed class GBimPanel : Panel
{
    public static Guid PanelId => typeof(GBimPanel).GUID;

    private readonly Label _filePathLabel = NewValueLabel();
    private readonly Label _repoLabel = NewValueLabel();
    private readonly Label _branchLabel = NewValueLabel();
    private readonly Label _lastCommitLabel = NewValueLabel();
    private readonly Label _nextVersionLabel = NewValueLabel();
    private readonly Label _dirtyLabel = NewValueLabel();
    private readonly Button _commitButton;
    private readonly Button _refreshButton;

    public GBimPanel()
    {
        _commitButton = new Button { Text = "Commit current version" };
        _refreshButton = new Button { Text = "Refresh" };
        _commitButton.Click += (_, _) => OnCommitClicked();
        _refreshButton.Click += (_, _) => DocumentTracker.Instance.Refresh();

        var headerFont = new Font(SystemFont.Bold, 13);
        var grid = new TableLayout
        {
            Padding = new Padding(12),
            Spacing = new Size(8, 6),
            Rows =
            {
                new TableRow(new Label { Text = "G-BIM", Font = headerFont }),
                LabelRow("File:",         _filePathLabel),
                LabelRow("Repository:",   _repoLabel),
                LabelRow("Branch:",       _branchLabel),
                LabelRow("Last commit:",  _lastCommitLabel),
                LabelRow("Next version:", _nextVersionLabel),
                LabelRow("State:",        _dirtyLabel),
                new TableRow(_commitButton),
                new TableRow(_refreshButton),
                new TableRow { ScaleHeight = true },
            },
        };

        Content = grid;

        DocumentTracker.Instance.StateChanged += OnTrackerChanged;
        Refresh();
    }

    private static Label NewValueLabel() =>
        new() { Text = "-", Wrap = WrapMode.Word };

    private static TableRow LabelRow(string caption, Label valueLabel)
    {
        return new TableRow(
            new TableCell(new Label { Text = caption }, false),
            new TableCell(valueLabel, true));
    }

    private void OnTrackerChanged(object? sender, EventArgs e)
    {
        try { Application.Instance.AsyncInvoke(Refresh); }
        catch { /* Eto not yet attached to a UI loop; ignore */ }
    }

    private void Refresh()
    {
        var s = DocumentTracker.Instance.State;

        if (s.Document is null)
        {
            SetIdle("(no Grasshopper document loaded)");
            return;
        }
        if (string.IsNullOrEmpty(s.FilePath))
        {
            SetIdle("(unsaved document - save it first)");
            return;
        }
        if (!s.IsTracked || s.RepoPath is null)
        {
            _filePathLabel.Text = s.FilePath;
            _repoLabel.Text = "(not in a Git repo - run `git init` in the folder)";
            _branchLabel.Text = "-";
            _lastCommitLabel.Text = "-";
            _nextVersionLabel.Text = "-";
            _dirtyLabel.Text = s.HasUnsavedChanges ? "unsaved changes" : "clean";
            _commitButton.Enabled = false;
            return;
        }

        try
        {
            _filePathLabel.Text = s.FilePath;
            _repoLabel.Text = s.RepoPath;

            var status = GBimRepository.GetStatus(s.RepoPath);
            _branchLabel.Text = status.Branch;
            _lastCommitLabel.Text = status.LastCommit is null
                ? "(no commits yet)"
                : $"{status.LastCommit.Sha[..7]}  {status.LastCommit.Message}";

            var ghBase = Path.GetFileNameWithoutExtension(s.FilePath);
            var jsonRel = Path.GetRelativePath(s.RepoPath, s.CanonicalJsonFullPath!);
            var nextV = CommitVersioning.NextVersion(s.RepoPath, jsonRel);
            _nextVersionLabel.Text = CommitVersioning.FormatMessage(ghBase, nextV);

            _dirtyLabel.Text = s.HasUnsavedChanges
                ? "unsaved changes (commit will use live state, .gh on disk may be stale)"
                : "clean";
            _commitButton.Enabled = true;
        }
        catch (Exception ex)
        {
            _commitButton.Enabled = false;
            _branchLabel.Text = "-";
            _lastCommitLabel.Text = $"(error: {ex.Message})";
        }
    }

    private void SetIdle(string fileLabel)
    {
        _filePathLabel.Text = fileLabel;
        _repoLabel.Text = "-";
        _branchLabel.Text = "-";
        _lastCommitLabel.Text = "-";
        _nextVersionLabel.Text = "-";
        _dirtyLabel.Text = "-";
        _commitButton.Enabled = false;
    }

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

            var sig = new Signature("iSamacA", "samaca163@gmail.com", DateTimeOffset.Now);
            var sha = GBimRepository.Commit(s.RepoPath, json, jsonFull, msg, sig, s.FilePath);

            if (sha is null)
            {
                MessageBox.Show("Nothing to commit (no changes since last commit).",
                    "G-BIM", MessageBoxButtons.OK, MessageBoxType.Information);
            }
            else
            {
                RhinoApp.WriteLine($"[G-BIM] Committed {sha[..7]} ({msg})");
                Refresh();
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-BIM] Commit failed: {ex.Message}");
            MessageBox.Show($"Commit failed:\n\n{ex.Message}",
                "G-BIM", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }
}
