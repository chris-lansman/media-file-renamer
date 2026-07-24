using MediaFileRenamer.App.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace MediaFileRenamer.App;

public partial class RecoveryWindow : Window
{
    private readonly OperationJournalService _journals;
    private readonly IReadOnlyList<InterruptedOperationRecovery>? _initialRecoveries;
    private bool _loadedRecoveries;

    public ObservableCollection<InterruptedOperationRecovery> Recoveries { get; } = [];

    public RecoveryWindow(
        OperationJournalService journals,
        IReadOnlyList<InterruptedOperationRecovery>? initialRecoveries = null)
    {
        _journals = journals;
        _initialRecoveries = initialRecoveries;
        InitializeComponent();
        DataContext = this;
        SetRecoveries(initialRecoveries ?? []);
        if (initialRecoveries is null)
        {
            RecoveryStatusTextBlock.Text =
                "Inspecting operation journals and recorded file paths...";
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadedRecoveries)
        {
            return;
        }

        _loadedRecoveries = true;
        if (_initialRecoveries is null)
        {
            await RefreshRecoveriesAsync();
        }
    }

    private void RecoveryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedRecovery();
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (RecoveryList.SelectedItem is not InterruptedOperationRecovery recovery
            || !recovery.CanRollback)
        {
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            this,
            "Roll back the selected interrupted operation?\n\n"
            + "The app will reinspect every path, byte-compare duplicate copies when needed, "
            + "and stop without overwriting if the state changed.",
            "Confirm safe rollback",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        SetBusy(true, "Reinspecting paths and performing safe rollback...");
        InterruptedOperationResult result;
        try
        {
            result = await Task.Run(() => _journals.RecoverInterrupted(
                recovery.Id,
                InterruptedOperationAction.Rollback));
        }
        catch (Exception ex)
        {
            HandleUnexpectedRecoveryFailure(ex);
            return;
        }

        SetBusy(false);
        ShowResult(result, "Recovery result");
        await RefreshRecoveriesAsync();
    }

    private async void MarkResolved_Click(object sender, RoutedEventArgs e)
    {
        if (RecoveryList.SelectedItem is not InterruptedOperationRecovery recovery)
        {
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            this,
            "Mark this operation resolved without changing any media files?\n\n"
            + "Use this only after you have manually reviewed the listed source, destination, "
            + "and partial paths. The startup warning will no longer appear for this journal.",
            "Mark operation resolved",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        SetBusy(true, "Updating the recovery journal...");
        InterruptedOperationResult result;
        try
        {
            result = await Task.Run(() => _journals.RecoverInterrupted(
                recovery.Id,
                InterruptedOperationAction.MarkResolved));
        }
        catch (Exception ex)
        {
            HandleUnexpectedRecoveryFailure(ex);
            return;
        }

        SetBusy(false);
        ShowResult(result, "Journal updated");
        await RefreshRecoveriesAsync();
    }

    private void ShowResult(InterruptedOperationResult result, string title)
    {
        RecoveryStatusTextBlock.Text = result.Message;
        System.Windows.MessageBox.Show(
            this,
            result.Message,
            title,
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async Task RefreshRecoveriesAsync()
    {
        SetBusy(
            true,
            "Inspecting operation journals and recorded file paths...",
            allowClose: true);
        IReadOnlyList<InterruptedOperationRecovery> recoveries;
        try
        {
            recoveries = await Task.Run(_journals.GetInterruptedOperations);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error(
                "Could not inspect interrupted operations.",
                ex);
            RecoveryStatusTextBlock.Text =
                "Recovery inspection failed. No media files were changed.";
            SetBusy(false);
            return;
        }

        if (!IsLoaded)
        {
            return;
        }

        SetBusy(false);
        SetRecoveries(recoveries);
    }

    private void SetRecoveries(
        IEnumerable<InterruptedOperationRecovery> recoveries)
    {
        Recoveries.Clear();
        foreach (var recovery in recoveries)
        {
            Recoveries.Add(recovery);
        }

        if (Recoveries.Count == 0)
        {
            RecoveryDetailsGrid.ItemsSource = null;
            RecoveryStatusTextBlock.Text = "No interrupted operations need attention.";
            RollbackButton.IsEnabled = false;
            MarkResolvedButton.IsEnabled = false;
            return;
        }

        RecoveryList.SelectedIndex = 0;
        UpdateSelectedRecovery();
    }

    private void SetBusy(
        bool isBusy,
        string? message = null,
        bool allowClose = false)
    {
        RecoveryList.IsEnabled = !isBusy;
        RollbackButton.IsEnabled = !isBusy
            && RecoveryList.SelectedItem is InterruptedOperationRecovery recovery
            && recovery.CanRollback;
        MarkResolvedButton.IsEnabled = !isBusy
            && RecoveryList.SelectedItem is InterruptedOperationRecovery;
        CloseButton.IsEnabled = !isBusy || allowClose;
        if (!string.IsNullOrWhiteSpace(message))
        {
            RecoveryStatusTextBlock.Text = message;
        }
    }

    private void HandleUnexpectedRecoveryFailure(Exception exception)
    {
        DiagnosticLog.Current.Error(
            "An unexpected recovery operation failure occurred.",
            exception);
        SetBusy(false);
        RecoveryStatusTextBlock.Text =
            "Recovery stopped unexpectedly. No further automatic changes will be attempted.";
        System.Windows.MessageBox.Show(
            this,
            "Recovery stopped unexpectedly. Review the listed paths and diagnostic log before retrying.",
            "Recovery stopped",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void UpdateSelectedRecovery()
    {
        if (RecoveryList.SelectedItem is not InterruptedOperationRecovery recovery)
        {
            RecoveryDetailsGrid.ItemsSource = null;
            RollbackButton.IsEnabled = false;
            MarkResolvedButton.IsEnabled = false;
            return;
        }

        RecoveryDetailsGrid.ItemsSource = recovery.Entries;
        RecoveryStatusTextBlock.Text = recovery.Message;
        RollbackButton.IsEnabled = recovery.CanRollback;
        RollbackButton.ToolTip = recovery.CanRollback
            ? "Reinspect and safely restore or remove only files proven to belong to this operation."
            : "Automatic rollback is disabled because the file state requires manual review.";
        MarkResolvedButton.IsEnabled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
