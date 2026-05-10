using System;
using GLoom.Serialization;
using Color = Eto.Drawing.Color;
using Font = Eto.Drawing.Font;
using Padding = Eto.Drawing.Padding;
using Size = Eto.Drawing.Size;
using SystemFont = Eto.Drawing.SystemFont;

// The project enables WinForms (UseWindowsForms=true) for GH_Canvas, so
// Eto.Forms control names collide with System.Windows.Forms ones. Alias
// the Eto types we use here, matching the pattern in GLoomPanel.cs.
using Button = Eto.Forms.Button;
using Control = Eto.Forms.Control;
using Dialog = Eto.Forms.Dialog<GLoom.Ui.TagCreationDialogResult?>;
using HorizontalAlignment = Eto.Forms.HorizontalAlignment;
using Label = Eto.Forms.Label;
using MessageBox = Eto.Forms.MessageBox;
using MessageBoxButtons = Eto.Forms.MessageBoxButtons;
using MessageBoxType = Eto.Forms.MessageBoxType;
using Orientation = Eto.Forms.Orientation;
using Panel = Eto.Forms.Panel;
using StackLayout = Eto.Forms.StackLayout;
using TableCell = Eto.Forms.TableCell;
using TableLayout = Eto.Forms.TableLayout;
using TableRow = Eto.Forms.TableRow;
using TextArea = Eto.Forms.TextArea;
using TextBox = Eto.Forms.TextBox;
using WrapMode = Eto.Forms.WrapMode;

namespace GLoom.Ui;

public sealed record TagCreationDialogResult(
    string Name,
    string? Notes,
    AecMetadata? Aec,
    ProductMetadata? Product,
    ReleaseMetadata? Release);

/// <summary>
/// Modal dialog driving annotated-tag creation. The user always fills
/// Name and (optionally) Notes; the three mode sub-records sit behind
/// collapsible toggles so a typical tag-as-version operation isn't
/// buried under fields it doesn't need. Only sections the user has
/// expanded contribute to the result; collapsed sections always
/// resolve to null regardless of any text the user may have typed
/// before collapsing.
/// </summary>
public sealed class TagCreationDialog : Dialog
{
    private readonly TextBox _nameBox = new();
    private readonly TextArea _notesBox = new() { Height = 50 };

    private readonly TextBox _aecPhase = new();
    private readonly TextBox _aecSubmittal = new();
    private readonly TextBox _aecSheetSet = new();
    private readonly TextArea _aecNotes = new() { Height = 40 };
    private readonly Panel _aecPanel = new() { Visible = false };
    private readonly Button _aecToggle = new() { Text = "+ AEC submittal info" };

    private readonly TextBox _productSku = new();
    private readonly TextBox _productVariant = new();
    private readonly TextArea _productNotes = new() { Height = 40 };
    private readonly Panel _productPanel = new() { Visible = false };
    private readonly Button _productToggle = new() { Text = "+ Product" };

    private readonly TextBox _releaseVersion = new();
    private readonly TextArea _releaseNotes = new() { Height = 60 };
    private readonly Panel _releasePanel = new() { Visible = false };
    private readonly Button _releaseToggle = new() { Text = "+ Release" };

    public TagCreationDialog(string commitSha, string commitMessage)
    {
        var sha7 = commitSha.Length >= 7 ? commitSha[..7] : commitSha;
        Title = "G-Loom: Tag commit";
        Padding = new Padding(12);
        Resizable = true;
        ClientSize = new Size(420, -1);

        // Auto-replace spaces with hyphens as the user types. Git rejects
        // spaces in refnames (`git tag "my tag"` tokenizes as three args
        // and check-ref-format flags the space); hyphen is the conventional
        // substitute. The setter re-fires TextChanged but the second pass
        // exits early via the no-space guard, so no recursion.
        _nameBox.ToolTip = "Spaces are auto-replaced with hyphens.";
        _nameBox.TextChanged += (_, _) =>
        {
            var current = _nameBox.Text;
            if (string.IsNullOrEmpty(current) || !current.Contains(' ')) return;
            var caret = _nameBox.CaretIndex;
            _nameBox.Text = current.Replace(' ', '-');
            _nameBox.CaretIndex = caret;
        };

        _aecPanel.Content = SectionContent(new[]
        {
            ("Phase:",     (Control)_aecPhase),
            ("Submittal:", _aecSubmittal),
            ("Sheet set:", _aecSheetSet),
            ("Notes:",     _aecNotes),
        });
        _aecToggle.Click += (_, _) => Toggle(_aecPanel, _aecToggle, "AEC submittal info");

        _productPanel.Content = SectionContent(new[]
        {
            ("SKU:",     (Control)_productSku),
            ("Variant:", _productVariant),
            ("Notes:",   _productNotes),
        });
        _productToggle.Click += (_, _) => Toggle(_productPanel, _productToggle, "Product");

        _releasePanel.Content = SectionContent(new[]
        {
            ("Version:", (Control)_releaseVersion),
            ("Notes:",   _releaseNotes),
        });
        _releaseToggle.Click += (_, _) => Toggle(_releasePanel, _releaseToggle, "Release");

        var headerLabel = new Label
        {
            Text = $"Tag commit {sha7}  ({commitMessage})",
            Font = new Font(SystemFont.Default, 9),
            TextColor = Color.FromGrayscale(0.5f),
            Wrap = WrapMode.Word,
        };

        var topForm = new TableLayout
        {
            Spacing = new Size(8, 6),
            Rows =
            {
                new TableRow(new TableCell(new Label{ Text = "Name:" }, false), new TableCell(_nameBox, true)),
                new TableRow(new TableCell(new Label{ Text = "Notes:" }, false), new TableCell(_notesBox, true)),
            },
        };

        var stack = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                headerLabel,
                topForm,
                _aecToggle, _aecPanel,
                _productToggle, _productPanel,
                _releaseToggle, _releasePanel,
            },
        };

        var createBtn = new Button { Text = "Create tag" };
        var cancelBtn = new Button { Text = "Cancel" };
        createBtn.Click += (_, _) => OnCreate();
        cancelBtn.Click += (_, _) => Close();
        DefaultButton = createBtn;
        AbortButton = cancelBtn;
        PositiveButtons.Add(createBtn);
        NegativeButtons.Add(cancelBtn);

        Content = stack;
    }

    private static Panel SectionContent((string label, Control field)[] rows)
    {
        var tl = new TableLayout
        {
            Spacing = new Size(8, 4),
            Padding = new Padding(20, 4, 0, 4),
        };
        foreach (var (label, field) in rows)
            tl.Rows.Add(new TableRow(
                new TableCell(new Label { Text = label }, false),
                new TableCell(field, true)));
        return new Panel { Content = tl };
    }

    private static void Toggle(Panel panel, Button btn, string label)
    {
        panel.Visible = !panel.Visible;
        btn.Text = panel.Visible ? $"− {label}" : $"+ {label}";
    }

    private void OnCreate()
    {
        var name = _nameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Tag name is required.", "G-Loom",
                MessageBoxButtons.OK, MessageBoxType.Warning);
            return;
        }

        var notes = NullIfEmpty(_notesBox.Text);

        AecMetadata? aec = null;
        if (_aecPanel.Visible)
        {
            var phase = NullIfEmpty(_aecPhase.Text);
            var sub = NullIfEmpty(_aecSubmittal.Text);
            var sheets = NullIfEmpty(_aecSheetSet.Text);
            var aNotes = NullIfEmpty(_aecNotes.Text);
            if (phase != null || sub != null || sheets != null || aNotes != null)
                aec = new AecMetadata(phase, sub, sheets, aNotes);
        }

        ProductMetadata? product = null;
        if (_productPanel.Visible)
        {
            var sku = NullIfEmpty(_productSku.Text);
            var variant = NullIfEmpty(_productVariant.Text);
            var pNotes = NullIfEmpty(_productNotes.Text);
            if (sku != null || variant != null || pNotes != null)
                product = new ProductMetadata(sku, variant, pNotes);
        }

        ReleaseMetadata? release = null;
        if (_releasePanel.Visible)
        {
            var ver = NullIfEmpty(_releaseVersion.Text);
            var rNotes = NullIfEmpty(_releaseNotes.Text);
            if (ver != null || rNotes != null)
                release = new ReleaseMetadata(ver, rNotes);
        }

        Result = new TagCreationDialogResult(name, notes, aec, product, release);
        Close();
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
