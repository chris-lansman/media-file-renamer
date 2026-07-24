using MediaFileRenamer.App.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace MediaFileRenamer.App;

public partial class RecoveryWindow : Window
{
    private readonly OperationJournalService _journals;

    public ObservableCollection<InterruptedOperationRecovery> Recoveries { get; } = [];

    public RecoveryWindow(OperationJournalService journals)
    {
        _journals = journals;
        InitializeComponent();
        DataContext = this;
        RefreshRecoveries();
    }

    private void RecoveryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedRecovery();
    }

    private void Rollback_Click(object sender, RoutedEventArgs e)
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

        var result = _journals.RecoverInterrupted(
            recovery.Id,
            InterruptedOperationAction.Rollback);
        ShowResult(result, "Recovery result");
    }

    private void MarkResolved_Click(object sender, RoutedEventArgs e)
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

        var result = _journals.RecoverInterrupted(
            recovery.Id,
            InterruptedOperationAction.MarkResolved);
        ShowResult(result, "Journal updated");
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
        RefreshRecoveries();
    }

    private void RefreshRecoveries()
    {
        Recoveries.Clear();
        foreach (var recovery in _journals.GetInterruptedOperations())
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
