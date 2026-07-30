using MediaFileRenamer.App.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace MediaFileRenamer.App;

public partial class OperationHistoryWindow : Window
{
    private readonly OperationJournalService _journals;

    public ObservableCollection<OperationJournalSummary> History { get; } = [];

    internal OperationJournalDetails? SelectedDetails { get; private set; }

    public OperationHistoryWindow(OperationJournalService journals)
    {
        _journals = journals;
        InitializeComponent();
        DataContext = this;
        RefreshHistory();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not OperationJournalSummary selected
            || !CanUndo(selected))
        {
            HistoryStatusTextBlock.Text =
                "Select a completed operation that is available to undo.";
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            this,
            $"Undo the selected {selected.Operation.ToString().ToLowerInvariant()} "
            + $"operation from {selected.CreatedAt.ToLocalTime():g}? "
            + "Undo will stop if any original path is occupied.",
            "Undo selected operation",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        var result = _journals.UndoCompleted(selected.Id);
        HistoryStatusTextBlock.Text = result.Message;
        RefreshHistory();
    }

    private void HistoryGrid_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSelectedOperation();
    }

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OperationDetailsTextBox.Text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(OperationDetailsTextBox.Text);
            HistoryStatusTextBlock.Text = "Operation details copied to the clipboard.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("Could not copy operation details.", ex);
            HistoryStatusTextBlock.Text =
                "Windows could not copy the operation details. Try again.";
        }
    }

    private void OpenDestination_Click(object sender, RoutedEventArgs e)
    {
        var destinationDirectory = GetOpenableDestinationDirectory(SelectedDetails);
        if (destinationDirectory is null)
        {
            HistoryStatusTextBlock.Text =
                "The selected destination folder is not currently available.";
            UpdateSelectedOperation();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(destinationDirectory)
            {
                UseShellExecute = true
            });
            HistoryStatusTextBlock.Text = "Opened the destination folder.";
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Current.Error("Could not open an operation destination.", ex);
            HistoryStatusTextBlock.Text =
                "Windows could not open the destination folder. Check its connection and permissions.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshHistory()
    {
        var selectedId =
            (HistoryGrid.SelectedItem as OperationJournalSummary)?.Id;
        History.Clear();
        foreach (var entry in _journals.GetHistory())
        {
            History.Add(entry);
        }

        if (History.Count == 0)
        {
            HistoryStatusTextBlock.Text = "No operation journals yet.";
        }

        HistoryGrid.SelectedItem = selectedId is null
            ? History.FirstOrDefault()
            : History.FirstOrDefault(value => value.Id == selectedId)
                ?? History.FirstOrDefault();
        UpdateSelectedOperation();
    }

    private void UpdateSelectedOperation()
    {
        if (HistoryGrid.SelectedItem is not OperationJournalSummary selected)
        {
            SelectedDetails = null;
            OperationDetailsTextBox.Text = "Select an operation to view its files.";
            CopyDetailsButton.IsEnabled = false;
            OpenDestinationButton.IsEnabled = false;
            UndoButton.IsEnabled = false;
            return;
        }

        SelectedDetails = _journals.GetOperationDetails(selected.Id);
        OperationDetailsTextBox.Text = FormatDetails(SelectedDetails, selected);
        CopyDetailsButton.IsEnabled = SelectedDetails is not null;
        OpenDestinationButton.IsEnabled =
            GetOpenableDestinationDirectory(SelectedDetails) is not null;
        UndoButton.IsEnabled = CanUndo(selected);
    }

    private static bool CanUndo(OperationJournalSummary summary) =>
        summary.Status is OperationJournalStatus.Completed
            or OperationJournalStatus.UndoFailed;

    internal static string FormatDetails(
        OperationJournalDetails? details,
        OperationJournalSummary summary)
    {
        if (details is null)
        {
            return "The journal details are no longer available.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Operation ID: {details.Id}");
        builder.AppendLine($"Started: {details.CreatedAt.ToLocalTime():g}");
        builder.AppendLine($"Completed: {details.CompletedAt?.ToLocalTime().ToString("g") ?? "Not completed"}");
        builder.AppendLine($"Operation: {details.Operation}");
        builder.AppendLine($"Status: {details.Status}");
        builder.AppendLine($"Files: {details.Files.Count}");
        if (!string.IsNullOrWhiteSpace(details.Error))
        {
            builder.AppendLine($"Error: {details.Error}");
        }

        foreach (var file in details.Files)
        {
            builder.AppendLine();
            builder.AppendLine(file.IsCompanion ? "Companion file" : "Media file");
            builder.AppendLine($"  Source: {file.SourcePath}");
            builder.AppendLine($"  Destination: {file.DestinationPath}");
            builder.AppendLine($"  Status: {file.Status}");
            builder.AppendLine($"  Size: {file.Length:N0} bytes");
        }

        return builder.ToString().TrimEnd();
    }

    internal static string? GetOpenableDestinationDirectory(
        OperationJournalDetails? details)
    {
        var destinationPath = details?.Files
            .Select(file => file.DestinationPath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (destinationPath is null)
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            return !string.IsNullOrWhiteSpace(directory)
                && Directory.Exists(directory)
                    ? directory
                    : null;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return null;
        }
    }
}
