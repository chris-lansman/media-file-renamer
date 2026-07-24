using MediaFileRenamer.App.Services;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace MediaFileRenamer.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetEntryAssembly()?.GetName().Version
            ?? Assembly.GetExecutingAssembly().GetName().Version;
        VersionTextBlock.Text = $"Version {version?.ToString(3) ?? "unknown"}";
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("Could not open an attribution link.", ex);
            System.Windows.MessageBox.Show(
                this,
                "Windows could not open this link. Check your default browser and try again.",
                "Could not open link",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(DiagnosticLog.Current.BuildDiagnosticSummary());
            CopyDiagnosticsButton.Content = "Copied";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("Could not copy the diagnostic summary.", ex);
            System.Windows.MessageBox.Show(
                this,
                "Windows could not copy the diagnostic summary to the clipboard.",
                "Could not copy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
