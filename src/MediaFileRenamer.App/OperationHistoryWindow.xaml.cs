using MediaFileRenamer.App.Services;
using System.Collections.ObjectModel;
using System.Windows;

namespace MediaFileRenamer.App;

public partial class OperationHistoryWindow : Window
{
    private readonly OperationJournalService _journals;

    public ObservableCollection<OperationJournalSummary> History { get; } = [];

    public OperationHistoryWindow(OperationJournalService journals)
    {
        _journals = journals;
        InitializeComponent();
        DataContext = this;
        RefreshHistory();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = System.Windows.MessageBox.Show(
            this,
            "Undo the most recent completed move or copy operation? "
            + "Undo will stop if any original path is occupied.",
            "Undo last operation",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        var result = _journals.UndoLastCompleted();
        HistoryStatusTextBlock.Text = result.Message;
        RefreshHistory();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshHistory()
    {
        History.Clear();
        foreach (var entry in _journals.GetHistory())
        {
            History.Add(entry);
        }

        if (History.Count == 0)
        {
            HistoryStatusTextBlock.Text = "No operation journals yet.";
        }
    }
}
