using Color = Eto.Drawing.Color;
using Font = Eto.Drawing.Font;
using Padding = Eto.Drawing.Padding;
using Size = Eto.Drawing.Size;
using SystemFont = Eto.Drawing.SystemFont;

// The project enables WinForms (for GH_Canvas), so Eto.Forms control names
// collide with System.Windows.Forms ones. Alias the Eto types we use here,
// matching the pattern in TagCreationDialog.cs / GLoomPanel.cs.
using Button = Eto.Forms.Button;
using Dialog = Eto.Forms.Dialog<GLoom.Ui.CommitDialogResult?>;
using HorizontalAlignment = Eto.Forms.HorizontalAlignment;
using Label = Eto.Forms.Label;
using Orientation = Eto.Forms.Orientation;
using StackLayout = Eto.Forms.StackLayout;
using TableCell = Eto.Forms.TableCell;
using TableLayout = Eto.Forms.TableLayout;
using TableRow = Eto.Forms.TableRow;
using TextArea = Eto.Forms.TextArea;
using TextBox = Eto.Forms.TextBox;
using WrapMode = Eto.Forms.WrapMode;

namespace GLoom.Ui;

public sealed record CommitDialogResult(string Subject, string Description);

/// <summary>
/// Modal dialog shown on every commit. The Message (subject) and Description
/// (body) come pre-filled with a draft generated from the diff against the
/// last committed version, so the common path is "glance, maybe tweak, commit"
/// rather than writing prose from a blank box. The subject is never allowed to
/// be empty: a blank box falls back to the generated draft.
/// </summary>
public sealed class CommitDialog : Dialog
{
    private readonly TextBox _subjectBox = new();
    private readonly TextArea _descriptionBox = new() { Height = 120 };
    private readonly string _draftSubject;

    public CommitDialog(string ghBase, string draftSubject, string draftDescription)
    {
        _draftSubject = draftSubject ?? string.Empty;
        Title = "G-Loom: Commit version";
        Padding = new Padding(12);
        Resizable = true;
        ClientSize = new Size(460, -1);

        _subjectBox.Text = _draftSubject;
        _descriptionBox.Text = draftDescription ?? string.Empty;

        var headerLabel = new Label
        {
            Text = $"Describe this version of {ghBase}. The draft is generated from what changed — edit freely.",
            Font = new Font(SystemFont.Default, 9),
            TextColor = Color.FromGrayscale(0.5f),
            Wrap = WrapMode.Word,
        };

        var form = new TableLayout
        {
            Spacing = new Size(8, 6),
            Rows =
            {
                new TableRow(
                    new TableCell(new Label { Text = "Message:" }, false),
                    new TableCell(_subjectBox, true)),
                new TableRow(
                    new TableCell(new Label { Text = "Description:" }, false),
                    new TableCell(_descriptionBox, true)),
            },
        };

        var stack = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { headerLabel, form },
        };

        var commitBtn = new Button { Text = "Commit" };
        var cancelBtn = new Button { Text = "Cancel" };
        commitBtn.Click += (_, _) => OnCommit();
        cancelBtn.Click += (_, _) => Close();
        DefaultButton = commitBtn;
        AbortButton = cancelBtn;
        PositiveButtons.Add(commitBtn);
        NegativeButtons.Add(cancelBtn);

        Content = stack;
    }

    private void OnCommit()
    {
        var subject = _subjectBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(subject)) subject = _draftSubject.Trim();
        if (string.IsNullOrWhiteSpace(subject)) subject = "Updated version";

        var description = _descriptionBox.Text?.Trim() ?? string.Empty;
        Result = new CommitDialogResult(subject, description);
        Close();
    }
}
