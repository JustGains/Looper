using System.Windows;

namespace JustCode;

public partial class SkillNameDialog : Window
{
    public string SkillName { get; private set; } = "";

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
