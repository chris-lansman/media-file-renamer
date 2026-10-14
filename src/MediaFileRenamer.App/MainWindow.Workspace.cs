using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace MediaFileRenamer.App;

public partial class MainWindow
{
    private readonly WorkspaceSessionService _workspaceStore = new(Path.Combine(AppDataPaths.Current.RootDirectory, "workspace.json"));
    private readonly DispatcherTimer _workspaceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBox _search = new() { Width = 210, Margin = new Thickness(0, 0, 8, 6), ToolTip = "Search filenames, titles, or source folders" };
    private readonly CheckBox _groupShows = new() { Content = "Group by show / season", Margin = new Thickness(0, 7, 8, 6) };
    private readonly TextBlock _queuePosition = new() { Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _difference = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBox _paths = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0) };
    private readonly TextBox _companions = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _formatExample = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock _completionText = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly TextBox _completionDetails = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _completionPanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 8) };
    private readonly StackPanel _inspection = new() { Visibility = Visibility.Collapsed };
    private readonly List<FrameworkElement> _tvFields = [];
    private readonly List<FrameworkElement> _workspaceActions = [];
    private readonly StackPanel _customTools = new();
    private readonly Expander _setupExpander = new();
    private readonly TextBlock _setupSummary = new() { TextWrapping = TextWrapping.Wrap };
    private Border _setupBorder = null!;
    private readonly StackPanel _filterEmptyPanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(12), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _filterEmptyText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 430 };
    private Button _restoreClearedButton = null!;
    private List<MediaPreviewItem>? _clearedBatch;
    private string _clearedSelection = "";
    private bool? _compactLayout;
    private Grid _headingGrid = null!;
    private StackPanel _headingButtons = null!;
    private Grid _headingText = null!;
    private Grid _mainWorkspaceGrid = null!;
    private Grid _reviewGrid = null!;
    private Grid _tablePanel = null!;
    private Border _inspectionBorder = null!;
    private WrapPanel? _setupControls;
    private bool _workspaceReady;
    private bool _workspaceLoaded;
    private bool _savingWorkspace;
    private bool _restoredColumnWidths;
    private string _completedDestination = "";

    private Button WorkspaceButton(string label, RoutedEventHandler action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 6) };
        AutomationProperties.SetName(button, label);
        button.Click += action;
        _workspaceActions.Add(button);
        return button;
    }

    private static void Detach(UIElement element)
    {
        if (VisualTreeHelper.GetParent(element) is Panel panel) panel.Children.Remove(element);
        else if (VisualTreeHelper.GetParent(element) is Decorator decorator) decorator.Child = null;
    }

    private void InitializeReviewWorkspace()
    {
        AutomationProperties.SetName(_search, "Search staged files");
        AutomationProperties.SetName(_paths, "Full original and destination paths");
        AutomationProperties.SetName(_companions, "Companion file destinations");
        AutomationProperties.SetLiveSetting(_queuePosition, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_completionText, AutomationLiveSetting.Polite);

        // Reflow setup controls at narrow widths rather than scrolling the entire window.
        var setupGrid = (Grid)((StackPanel)OutputFolderLabel.Parent).Parent;
        var setupBorder = (Border)setupGrid.Parent;
        _setupBorder = setupBorder;
        var setupChildren = setupGrid.Children.Cast<UIElement>().ToArray();
        setupGrid.Children.Clear();
        var setup = new WrapPanel();
        _setupControls = setup;
        foreach (var child in setupChildren)
        {
            if (child is FrameworkElement element) element.Margin = new Thickness(0, 0, 12, 8);
            setup.Children.Add(child);
        }
        ((FrameworkElement)setupChildren[0]).Width = 320;
        _setupExpander.Header = _setupSummary;
        _setupExpander.IsExpanded = true;
        AutomationProperties.SetName(_setupExpander, "Staging options");
        AutomationProperties.SetAutomationId(_setupExpander, "StagingOptionsExpander");
        setupBorder.Child = setup;
        MatchAllButton.Style = (Style)FindResource(typeof(Button));

        var headingButtons = (StackPanel)AddFilesButton.Parent;
        var heading = (Grid)headingButtons.Parent;
        _headingGrid = heading;
        _headingButtons = headingButtons;
        _mainWorkspaceGrid = (Grid)heading.Parent;
        heading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        heading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(headingButtons, 0);
        Grid.SetRow(headingButtons, 1);
        Grid.SetColumnSpan(headingButtons, 2);
        headingButtons.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        headingButtons.Margin = new Thickness(0, 10, 0, 0);
        var headingText = heading.Children.OfType<Grid>().First();
        _headingText = headingText;
        Grid.SetColumnSpan(headingText, 2);
        var reviewSummary = (StackPanel)ReviewCountTextBlock.Parent;
        var summaryGrid = (Grid)reviewSummary.Parent;
        var summaryChildren = reviewSummary.Children.Cast<UIElement>().ToArray();
        reviewSummary.Children.Clear();
        summaryGrid.Children.Remove(reviewSummary);
        var summary = new WrapPanel();
        foreach (var child in summaryChildren) summary.Children.Add(child);
        Grid.SetColumnSpan(summary, 2);
        summaryGrid.Children.Add(summary);

        var guidanceGrid = (Grid)ReviewGuidancePanel.Child;
        var guidanceChildren = guidanceGrid.Children.Cast<UIElement>().ToArray();
        guidanceGrid.Children.Clear();
        var guidance = new StackPanel();
        foreach (var child in guidanceChildren) guidance.Children.Add(child);
        ((FrameworkElement)guidanceChildren[1]).Margin = new Thickness(0, 8, 0, 0);
        ReviewGuidancePanel.Child = guidance;

        var toolbar = (WrapPanel)MatchSelectedButton.Parent;
        // Batch filtering and navigation stay above the table. Selected-file actions
        // live beside the filename comparison so the scope of each action is clear.
        Detach(MatchSelectedButton);
        Detach(ChooseSelectedButton);
        Detach(RetryUnresolvedButton);
        toolbar.Children.Insert(0, _search);
        toolbar.Children.Insert(1, _groupShows);
        toolbar.Children.Insert(0, new TextBlock { Text = "Search", Margin = new Thickness(0, 10, 6, 0) });
        MainScrollViewer.ScrollChanged += (_, args) =>
        {
            if (args.ViewportWidthChange != 0)
            {
                OriginalGrid.Width = Math.Max(500, MainScrollViewer.ViewportWidth - 28);
                UpdateCompactLayout(MainScrollViewer.ViewportWidth);
            }
        };
        MainScrollViewer.SizeChanged += (_, _) => UpdateCompactLayout(MainScrollViewer.ActualWidth);
        OriginalGrid.MinColumnWidth = 110;
        OriginalGrid.SizeChanged += (_, _) =>
        {
            if (_restoredColumnWidths) return;
            var available = Math.Max(220, OriginalGrid.ActualWidth - 180);
            OriginalGrid.Columns[0].Width = available / 2;
            OriginalGrid.Columns[2].Width = available / 2;
        };
        var previous = WorkspaceButton("Previous review", (_, _) => NavigateReview(-1));
        var next = WorkspaceButton("Next review", (_, _) => NavigateReview(1));
        toolbar.Children.Add(previous);
        toolbar.Children.Add(next);
        toolbar.Children.Add(_queuePosition);
        _restoreClearedButton = WorkspaceButton("Restore cleared batch", RestoreClearedBatch_Click);
        AutomationProperties.SetAutomationId(_restoreClearedButton, "RestoreClearedBatchButton");
        _restoreClearedButton.Visibility = Visibility.Collapsed;
        headingButtons.Children.Add(_restoreClearedButton);
        _tablePanel = (Grid)OriginalGrid.Parent;
        _reviewGrid = (Grid)_tablePanel.Parent;
        _reviewGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _filterEmptyPanel.SetResourceReference(Panel.BackgroundProperty, "SurfaceBrush");
        _filterEmptyText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _filterEmptyPanel.Children.Add(_filterEmptyText);
        var showAll = WorkspaceButton("Show all files", (_, _) => ShowAllFiles());
        showAll.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        AutomationProperties.SetAutomationId(showAll, "ShowAllFilesButton");
        showAll.Margin = new Thickness(0, 10, 0, 0);
        _filterEmptyPanel.Children.Add(showAll);
        AutomationProperties.SetLiveSetting(_filterEmptyText, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_filterEmptyPanel, "Filtered batch status");
        _tablePanel.Children.Add(_filterEmptyPanel);
        OriginalGrid.GroupStyle.Add(new GroupStyle
        {
            HeaderTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding Name}' Foreground='{DynamicResource TextBrush}' FontWeight='SemiBold' Margin='8,12,8,6'/></DataTemplate>")
        });
        _search.TextChanged += (_, _) => ApplyWorkspaceFilter();
        _groupShows.Checked += (_, _) => ApplyWorkspaceFilter();
        _groupShows.Unchecked += (_, _) => ApplyWorkspaceFilter();

        // Keep the final operation and progress visible outside the scrolling workspace.
        var root = (DockPanel)Content;
        var footer = new DockPanel { Margin = new Thickness(14, 8, 14, 12), LastChildFill = true };
        Detach(RenameButton);
        DockPanel.SetDock(RenameButton, Dock.Right);
        RenameButton.Margin = new Thickness(12, 0, 0, 0);
        footer.Children.Add(RenameButton);
        var statusBorder = (Border)((Grid)StatusTextBlock.Parent).Parent;
        Detach(statusBorder);
        statusBorder.Margin = new Thickness(0);
        footer.Children.Add(statusBorder);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Insert(1, footer);

        var editorParent = (StackPanel)SelectedFileEditor.Parent;
        _inspectionBorder = (Border)editorParent.Parent;
        heading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_completionPanel, 2);
        Grid.SetColumnSpan(_completionPanel, 2);
        heading.Children.Add(_completionPanel);
        editorParent.Children.Insert(0, _inspection);
        _inspection.Children.Add(new TextBlock { Text = "Filename preview · highlighted text changes", FontWeight = FontWeights.SemiBold });
        _inspection.Children.Add(_difference);
        var selectedActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        Detach(ResolveReviewButton);
        Detach(ChooseDifferentShowButton);
        selectedActions.Children.Add(ResolveReviewButton);
        selectedActions.Children.Add(ChooseSelectedButton);
        selectedActions.Children.Add(MatchSelectedButton);
        selectedActions.Children.Add(WorkspaceButton("Remove from batch", RemoveSelected_Click));
        _inspection.Children.Add(selectedActions);
        ChooseSelectedButton.ToolTip = "Choose the correct movie or show for this file.";
        var moreMatching = new Expander { Header = "More matching options", Content = RetryUnresolvedButton, Margin = new Thickness(0, 4, 0, 0) };
        toolbar.Children.Add(moreMatching);
        var pathExpander = new Expander { Header = "Full paths and companion files", Content = new StackPanel() };
        var pathPanel = (StackPanel)pathExpander.Content;
        pathPanel.Children.Add(_paths);
        var pathActions = new WrapPanel();
        pathActions.Children.Add(WorkspaceButton("Copy source path", (_, _) => CopySelectedPath(false)));
        pathActions.Children.Add(WorkspaceButton("Copy destination path", (_, _) => CopySelectedPath(true)));
        pathActions.Children.Add(WorkspaceButton("Open source folder", (_, _) => OpenSelectedFolder(false)));
        pathActions.Children.Add(WorkspaceButton("Open destination folder", (_, _) => OpenSelectedFolder(true)));
        pathPanel.Children.Add(pathActions);
        pathPanel.Children.Add(_companions);
        _inspection.Children.Add(pathExpander);

        // Keep existing bindings and automation names while letting fields wrap naturally.
        var oldChildren = SelectedFileEditor.Children.Cast<UIElement>().ToArray();
        SelectedFileEditor.Children.Clear();
        var editorBody = new StackPanel();
        var fields = new WrapPanel();
        void Field(FrameworkElement label, FrameworkElement control, double width, bool tv = false)
        {
            var field = new StackPanel { Width = width, Margin = new Thickness(0, 8, 12, 0) };
            label.Margin = new Thickness(0, 0, 0, 4);
            control.Margin = new Thickness(0);
            field.Children.Add(label);
            field.Children.Add(control);
            fields.Children.Add(field);
            if (tv) _tvFields.Add(field);
        }
        Field(SelectedTitleLabel, SelectedTitleTextBox, 230);
        Field(SelectedYearLabel, SelectedYearTextBox, 80);
        Field(SelectedTypeLabel, SelectedTypeComboBox, 110);
        Field(SelectedSeasonLabel, SelectedSeasonTextBox, 80, true);
        Field(SelectedEpisodeLabel, SelectedEpisodeTextBox, 80, true);
        Field(SelectedEpisodeTitleLabel, SelectedEpisodeTitleTextBox, 230, true);
        Field(EpisodeOrderLabel, EpisodeOrderComboBox, 160, true);
        editorBody.Children.Add(fields);
        var advanced = new Expander { Header = "Provider details", Margin = new Thickness(0, 8, 0, 0) };
        var providerFields = new WrapPanel();
        foreach (var child in oldChildren.Where(child => Grid.GetRow(child) == 2 && Grid.GetColumn(child) >= 5)) providerFields.Children.Add(child);
        advanced.Content = providerFields;
        editorBody.Children.Add(advanced);
        editorBody.Children.Add(oldChildren.Single(child => child is WrapPanel));
        var editExpander = new Expander { Header = "Edit media details", Content = editorBody, Margin = new Thickness(0, 8, 0, 0) };
        SelectedFileEditor.Children.Add(editExpander);
        Grid.SetColumnSpan(editExpander, 10);
        Grid.SetRowSpan(editExpander, 5);
        _customTools.Children.Add(CustomFormatLabel);
        _customTools.Children.Add(CustomFormatTextBox);
        var tokens = new WrapPanel();
        foreach (var token in RenamePlanner.FormatTokens)
        {
            var text = "{" + token + "}";
            tokens.Children.Add(WorkspaceButton(text, (_, _) =>
            {
                CustomFormatTextBox.SelectedText = text;
                CustomFormatTextBox.CaretIndex = CustomFormatTextBox.SelectionStart + text.Length;
                CustomFormatTextBox.Focus();
            }));
        }
        _customTools.Children.Add(new Expander { Header = "Insert a naming token", Content = tokens });
        _customTools.Children.Add(_formatExample);
        editorParent.Children.Add(_customTools);

        _completionPanel.Children.Add(_completionText);
        var completeActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        completeActions.Children.Add(WorkspaceButton("Open completed destination", (_, _) => OpenFolder(_completedDestination)));
        completeActions.Children.Add(WorkspaceButton("Undo last operation", UndoLastOperationMenuItem_Click));
        completeActions.Children.Add(WorkspaceButton("Dismiss result", (_, _) => _completionPanel.Visibility = Visibility.Collapsed));
        _completionPanel.Children.Add(completeActions);
        _completionPanel.Children.Add(new Expander { Header = "Operation and cleanup details", Content = _completionDetails });

        // Poster fallback remains visible underneath images when artwork cannot load.
        PreviewItems.CollectionChanged += (_, _) => ScheduleWorkspaceSave();
        _workspaceTimer.Tick += async (_, _) =>
        {
            _workspaceTimer.Stop();
            if (_isBusy || _savingWorkspace) { ScheduleWorkspaceSave(); return; }
            var snapshot = CaptureWorkspace();
            _savingWorkspace = true;
            try { await Task.Run(() => _workspaceStore.Save(snapshot)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { StatusTextBlock.Text = "Could not save the review workspace. Your current files are still available in this window."; }
            finally { _savingWorkspace = false; }
        };
        SizeChanged += (_, _) => ScheduleWorkspaceSave();
        _workspaceReady = true;
        RefreshWorkspaceDetails();
    }

    private void UpdateCompactLayout(double width)
    {
        if (!_workspaceReady || width <= 0) return;
        var compact = width < 850;
        if (_compactLayout == compact) return;
        _compactLayout = compact;
        _setupBorder.Child = null;
        _setupExpander.Content = null;
        if (compact)
        {
            _setupExpander.Content = _setupControls;
            _setupBorder.Child = _setupExpander;
        }
        else _setupBorder.Child = _setupControls;
        _setupExpander.IsExpanded = !compact;
        _setupSummary.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        _mainWorkspaceGrid.MinHeight = compact ? 400 : 600;
        Grid.SetRow(_headingButtons, compact ? 1 : 0);
        Grid.SetColumn(_headingButtons, compact ? 0 : 1);
        Grid.SetColumnSpan(_headingButtons, compact ? 2 : 1);
        Grid.SetColumnSpan(_headingText, compact ? 2 : 1);
        _headingButtons.Margin = new Thickness(0, compact ? 6 : 0, 0, 0);
        _headingGrid.Margin = new Thickness(0, 0, 0, compact ? 8 : 14);
        OriginalGrid.MinHeight = compact ? 130 : 200;
        Detach(_inspectionBorder);
        if (compact)
        {
            _reviewGrid.RowDefinitions[2].Height = GridLength.Auto;
            _reviewGrid.RowDefinitions[3].Height = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(_inspectionBorder, 2);
            Grid.SetRow(_tablePanel, 3);
            _inspectionBorder.Margin = new Thickness(0, 0, 0, 8);
            _inspectionBorder.Padding = new Thickness(10);
            _reviewGrid.Children.Add(_inspectionBorder);
        }
        else
        {
            _reviewGrid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
            _reviewGrid.RowDefinitions[3].Height = GridLength.Auto;
            Grid.SetRow(_tablePanel, 2);
            Grid.SetRow(_inspectionBorder, 3);
            _inspectionBorder.Margin = new Thickness(0, 14, 0, 0);
            _inspectionBorder.Padding = new Thickness(16);
            _mainWorkspaceGrid.Children.Add(_inspectionBorder);
        }
    }

    private void ShowAllFiles()
    {
        if (_isBusy) return;
        _search.Clear();
        ReviewFilterComboBox.SelectedIndex = 0;
        OriginalGrid.SelectedItem ??= PreviewItems.FirstOrDefault();
        if (OriginalGrid.SelectedItem is not null) OriginalGrid.ScrollIntoView(OriginalGrid.SelectedItem);
        RefreshWorkspaceDetails();
    }

    private void FocusNextUnresolved(int previousIndex)
    {
        var next = PreviewItems.Skip(previousIndex + 1).Concat(PreviewItems.Take(previousIndex + 1))
            .FirstOrDefault(item => item.RequiresReview);
        if (next is null)
        {
            RefreshWorkspaceDetails();
            return;
        }
        _search.Clear();
        ReviewFilterComboBox.SelectedIndex = 1;
        OriginalGrid.SelectedItem = next;
        OriginalGrid.ScrollIntoView(next);
    }

    private async void RestoreClearedBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _clearedBatch is null) return;
        await RunBusyAsync(RestoreClearedBatchAsync);
    }

    private async Task RestoreClearedBatchAsync()
    {
        if (_clearedBatch is null) return;
        var saved = _clearedBatch.ToDictionary(item => item.SourcePath, StringComparer.OrdinalIgnoreCase);
        var scanned = await Task.Run(() => _scanner.Scan(saved.Keys));
        var restored = 0;
        foreach (var item in scanned)
        {
            if (PreviewItems.Any(existing => string.Equals(existing.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase))) continue;
            WorkspaceSessionService.RestoreDetails(saved[item.SourcePath], item);
            AttachPreviewItem(item);
            restored++;
        }
        _clearedBatch = null;
        _search.Clear();
        ReviewFilterComboBox.SelectedIndex = 0;
        OriginalGrid.SelectedItem = PreviewItems.FirstOrDefault(item => item.SourcePath == _clearedSelection) ?? PreviewItems.FirstOrDefault();
        StatusTextBlock.Text = $"Restored {restored} file(s); {saved.Count - restored} unavailable or already in the batch. Confirm restored details before applying.";
    }

    private void ScheduleWorkspaceSave()
    {
        if (!_workspaceReady || !_workspaceLoaded || !IsLoaded) return;
        _workspaceTimer.Stop();
        _workspaceTimer.Start();
    }

    private WorkspaceSession CaptureWorkspace() => new()
    {
        Width = WindowState == WindowState.Normal ? ActualWidth : RestoreBounds.Width,
        Height = WindowState == WindowState.Normal ? ActualHeight : RestoreBounds.Height,
        Maximized = WindowState == WindowState.Maximized,
        Preset = PresetComboBox.SelectedIndex,
        Operation = OperationComboBox.SelectedIndex,
        OutputFolder = OutputFolderTextBox.Text,
        CustomFormat = CustomFormatTextBox.Text,
        SelectedSource = (OriginalGrid.SelectedItem as MediaPreviewItem)?.SourcePath ?? "",
        ColumnWidths = OriginalGrid.Columns.Select(column => column.ActualWidth).ToList(),
        Items = PreviewItems.Select(item => item.CreateTransferSnapshot()).ToList()
    };

    private async Task RestoreWorkspaceAsync()
    {
        if (_workspaceLoaded) return;
        try
        {
            var session = await Task.Run(_workspaceStore.Load);
            if (session is null) return;
            Width = double.IsFinite(session.Width) ? Math.Clamp(session.Width, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width)) : 1360;
            Height = double.IsFinite(session.Height) ? Math.Clamp(session.Height, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height)) : 860;
            if (session.Maximized) WindowState = WindowState.Maximized;
            PresetComboBox.SelectedIndex = Math.Clamp(session.Preset, 0, 2);
            OperationComboBox.SelectedIndex = Math.Clamp(session.Operation, 0, 1);
            if (Path.IsPathFullyQualified(session.OutputFolder)) OutputFolderTextBox.Text = session.OutputFolder;
            if (!string.IsNullOrWhiteSpace(session.CustomFormat)) CustomFormatTextBox.Text = session.CustomFormat;
            for (var index = 0; index < Math.Min(session.ColumnWidths.Count, OriginalGrid.Columns.Count); index++)
                if (double.IsFinite(session.ColumnWidths[index]) && session.ColumnWidths[index] >= 40)
                {
                    OriginalGrid.Columns[index].Width = Math.Clamp(session.ColumnWidths[index], 40, 700);
                    _restoredColumnWidths = true;
                }
            var restored = 0;
            await RunBusyAsync(async () =>
            {
                var scanned = await Task.Run(() => _scanner.Scan(session.Items.Select(item => item.SourcePath)));
                foreach (var item in scanned)
                {
                    if (PreviewItems.Any(existing => string.Equals(existing.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase))) continue;
                    var saved = session.Items.FirstOrDefault(candidate => string.Equals(candidate.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase));
                    if (saved is null) continue;
                    WorkspaceSessionService.RestoreDetails(saved, item);
                    AttachPreviewItem(item);
                    restored++;
                }
            });
            OriginalGrid.SelectedItem = PreviewItems.FirstOrDefault(item => item.SourcePath == session.SelectedSource) ?? PreviewItems.FirstOrDefault();
            StatusTextBlock.Text = $"Restored {restored} file(s) for review; {session.Items.Count - restored} unavailable or already staged. Confirm restored details before applying.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { StatusTextBlock.Text = "The saved workspace could not be restored. Add your files to start a new review."; }
        finally { _workspaceLoaded = true; UpdateActionState(); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isBusy || _savingWorkspace)
        {
            e.Cancel = true;
            StatusTextBlock.Text = "Wait for the current operation to finish before closing. Use Cancel to safely stop a transfer.";
        }
        else if (_workspaceLoaded)
        {
            _workspaceTimer.Stop();
            try { _workspaceStore.Save(CaptureWorkspace()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                e.Cancel = System.Windows.MessageBox.Show(this, "The review workspace could not be saved. Close without saving it?", "Workspace save failed", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes;
            }
        }
        base.OnClosing(e);
    }

    private void ApplyWorkspaceFilter()
    {
        if (!_workspaceReady) return;
        var view = CollectionViewSource.GetDefaultView(PreviewItems);
        var query = _search.Text.Trim();
        using (view.DeferRefresh())
        {
            view.Filter = value => value is MediaPreviewItem item
                && (query.Length == 0 || $"{item.SourcePath} {item.MatchedTitle} {item.DestinationFileName}".Contains(query, StringComparison.OrdinalIgnoreCase))
                && (ReviewFilterComboBox.SelectedIndex switch
                {
                    1 => item.RequiresReview,
                    2 => !item.RequiresReview,
                    3 => item.MatchState == "Blocked",
                    _ => true
                });
            view.GroupDescriptions.Clear();
            if (_groupShows.IsChecked == true) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MediaPreviewItem.ReviewGroup)));
        }
        RefreshWorkspaceDetails();
    }

    private void NavigateReview(int direction)
    {
        if (_isBusy) return;
        var items = PreviewItems.Where(item => item.RequiresReview).ToList();
        if (items.Count == 0) return;
        var selected = OriginalGrid.SelectedItem as MediaPreviewItem;
        var selectedIndex = selected is null ? -1 : PreviewItems.IndexOf(selected);
        var ordered = direction > 0 ? PreviewItems.ToList() : PreviewItems.Reverse().ToList();
        var anchor = selectedIndex < 0 ? -1 : ordered.IndexOf(selected!);
        var next = ordered.Skip(anchor + 1).Concat(ordered.Take(anchor + 1)).First(item => item.RequiresReview);
        // Navigation deliberately clears filters so the selected review is visible.
        _search.Clear();
        ReviewFilterComboBox.SelectedIndex = 1;
        OriginalGrid.SelectedItem = next;
        OriginalGrid.ScrollIntoView(next);
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || OriginalGrid.SelectedItem is not MediaPreviewItem item) return;
        var index = PreviewItems.IndexOf(item);
        PreviewItems.Remove(item);
        OriginalGrid.SelectedItem = PreviewItems.ElementAtOrDefault(Math.Min(index, PreviewItems.Count - 1));
        StatusTextBlock.Text = $"Removed {item.SourceFileName} from this batch. The file was not deleted.";
        UpdateActionState();
    }

    private void CopySelectedPath(bool destination)
    {
        if (OriginalGrid.SelectedItem is not MediaPreviewItem item) return;
        try { System.Windows.Clipboard.SetText(destination ? item.DestinationPath : item.SourcePath); }
        catch (System.Runtime.InteropServices.ExternalException) { StatusTextBlock.Text = "The clipboard is busy. Try copying the path again."; }
    }

    private void OpenSelectedFolder(bool destination)
    {
        if (OriginalGrid.SelectedItem is MediaPreviewItem item) OpenFolder(destination ? item.DestinationFolder : item.SourceFolder);
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) { StatusTextBlock.Text = "This folder does not exist yet or is unavailable."; return; }
            Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or ArgumentException or UnauthorizedAccessException)
        { StatusTextBlock.Text = "Windows could not open that folder."; }
    }

    private void RefreshWorkspaceDetails()
    {
        if (!_workspaceReady) return;
        var item = OriginalGrid.SelectedItem as MediaPreviewItem;
        _inspection.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        var reviews = PreviewItems.Where(row => row.RequiresReview).ToList();
        var index = item is null ? -1 : reviews.IndexOf(item);
        var visible = CollectionViewSource.GetDefaultView(PreviewItems).Cast<object>().Count();
        _filterEmptyPanel.Visibility = PreviewItems.Count > 0 && visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        _filterEmptyText.Text = ReviewFilterComboBox.SelectedIndex == 1 && reviews.Count == 0
            ? "Review complete — show all files to check the batch before moving or copying."
            : "No files match these filters. Your batch is still here.";
        _restoreClearedButton.Visibility = _clearedBatch is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        _setupSummary.Text = $"Staging: {Path.GetFileName(OutputFolderTextBox.Text.TrimEnd(Path.DirectorySeparatorChar))} · {GetOperationLabel()} · {((ComboBoxItem)PresetComboBox.SelectedItem).Content}";
        _setupSummary.ToolTip = OutputFolderTextBox.Text;
        _inspectionBorder.Visibility = item is not null || GetPreset() == RenamePreset.Custom ? Visibility.Visible : Visibility.Collapsed;
        var canBrowseEpisodes = item?.MediaType == "TV" && (item.TmdbId is not null || item.TvdbId is not null)
            && _settings.UseTmdbLookup && !string.IsNullOrWhiteSpace(_settings.TmdbApiKey);
        ResolveReviewButton.Visibility = item?.RequiresReview == true ? Visibility.Visible : Visibility.Collapsed;
        ChooseSelectedButton.Visibility = item?.RequiresReview != true || canBrowseEpisodes ? Visibility.Visible : Visibility.Collapsed;
        ChooseSelectedButton.Content = canBrowseEpisodes ? "Choose Show..." : "Choose Match...";
        _queuePosition.Text = (index >= 0 ? $"Review {index + 1} of {reviews.Count}" : $"{reviews.Count} need review") + $" · {visible}/{PreviewItems.Count} shown";
        foreach (var field in _tvFields) field.Visibility = item?.MediaType == "TV" ? Visibility.Visible : Visibility.Collapsed;
        RememberShowMappingButton.Visibility = item?.MediaType == "TV" ? Visibility.Visible : Visibility.Collapsed;
        RememberShowMappingStatusTextBlock.Visibility = item?.MediaType == "TV" ? Visibility.Visible : Visibility.Collapsed;
        _customTools.Visibility = GetPreset() == RenamePreset.Custom ? Visibility.Visible : Visibility.Collapsed;
        foreach (var action in _workspaceActions)
            action.IsEnabled = !_isBusy && (action is Button button ? button.Content?.ToString() switch
            {
                "Remove from batch" => item is not null,
                "Previous review" or "Next review" => reviews.Count > 0,
                "Copy destination path" => !string.IsNullOrEmpty(item?.DestinationPath),
                "Restore cleared batch" => _clearedBatch is { Count: > 0 },
                _ => true
            } : true);
        if (_setupControls is not null) _setupControls.IsEnabled = !_isBusy;
        _setupExpander.IsEnabled = !_isBusy;
        ReviewFilterComboBox.IsEnabled = !_isBusy;
        ChooseSelectedButton.Style = (Style)FindResource(item?.RequiresReview == true ? "PrimaryButtonStyle" : typeof(Button));
        _customTools.IsEnabled = !_isBusy;
        _search.IsEnabled = !_isBusy;
        _groupShows.IsEnabled = !_isBusy;
        if (item is not null)
        {
            _difference.Inlines.Clear();
            var removedParts = FilenameDifference.Compare(Path.GetFileName(item.DestinationPath), Path.GetFileName(item.SourcePath));
            _difference.Inlines.Add(new Run(removedParts.Prefix));
            var removed = new Run(removedParts.Changed) { TextDecorations = TextDecorations.Strikethrough };
            removed.SetResourceReference(TextElement.ForegroundProperty, "BlockedBrush");
            _difference.Inlines.Add(removed);
            _difference.Inlines.Add(new Run(removedParts.Suffix + "\n→ "));
            var parts = FilenameDifference.Compare(Path.GetFileName(item.SourcePath), Path.GetFileName(item.DestinationPath));
            _difference.Inlines.Add(new Run(parts.Prefix));
            var changed = new Run(parts.Changed) { FontWeight = FontWeights.Bold };
            changed.SetResourceReference(TextElement.BackgroundProperty, "AccentSoftBrush");
            changed.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            _difference.Inlines.Add(changed);
            _difference.Inlines.Add(new Run(parts.Suffix));
            _paths.Text = $"Original: {item.SourcePath}\nDestination: {item.DestinationPath}";
            _companions.Text = item.CompanionCount == 0 ? "No companion files." : string.Join("\n\n", _planner.BuildCompanionDestinations(item).Select(pair => $"{pair.SourcePath}\n→ {pair.DestinationPath}"));
        }
        try
        {
            var example = item ?? new MediaPreviewItem { MatchedTitle = "Example Movie", Year = 2024, Extension = ".mkv", MediaType = "Movie" };
            _formatExample.Text = "Preview: " + _planner.BuildDestination(example, OutputFolderTextBox.Text, GetPreset(), CustomFormatTextBox.Text);
            _formatExample.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _formatExample.Text = ex.Message;
            _formatExample.SetResourceReference(TextBlock.ForegroundProperty, "BlockedBrush");
        }
        ScheduleWorkspaceSave();
    }

    private string GetOperationLabel() => OperationComboBox.SelectedIndex == 1 ? "Copy" : "Move";
}
