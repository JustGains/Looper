using System.Windows;

namespace JustCode;

public partial class SkillNameDialog : Window
{
    public string SkillName { get; private set; } = "";

    /// Override the dialog's field label (default: "Skill name"). Useful for
    /// repurposing the same dialog as a generic input prompt (e.g. new
    /// branch name).
    public string FieldLabelText
    {
        get => FieldLabel.Text;
        set => FieldLabel.Text = value;
    }

    /// Override the hint text shown below the input. Empty string hides it.
    public string HintTextValue
    {
        get => HintText.Text;
        set
        {
            HintText.Text = value ?? "";
            HintText.Visibility = string.IsNullOrEmpty(value)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    public SkillNameDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var n = (NameBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(n)) return;
        SkillName = n;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
