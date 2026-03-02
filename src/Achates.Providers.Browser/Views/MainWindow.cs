using Achates.Providers.Browser.Services;
using Achates.Providers.Browser.ViewModels;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Achates.Providers.Browser.Views;

public sealed class MainWindow : Window
{
    private readonly ModelBrowserViewModel _viewModel;
    private readonly SearchBarView _searchBar;
    private readonly ModelTableView _tableView;
    private readonly ModelDetailView _detailView;

    public MainWindow(ModelService modelService)
    {
        Title = "OpenRouter Model Browser";

        _viewModel = new ModelBrowserViewModel(modelService);

        _searchBar = new SearchBarView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 3
        };

        _tableView = new ModelTableView
        {
            X = 0,
            Y = 3,
            Width = Dim.Percent(60),
            Height = Dim.Fill(1)
        };

        _detailView = new ModelDetailView
        {
            X = Pos.Right(_tableView),
            Y = 3,
            Width = Dim.Fill(),
            Height = Dim.Fill(1)
        };

        var statusBar = new StatusBar(
        [
            new Shortcut(Key.Esc, "Quit", Quit, ""),
            new Shortcut(Key.F.WithCtrl, "Search", () => _searchBar.FocusSearch(), ""),
            new Shortcut(Key.F1, "Name", () => _viewModel.SetSort(SortColumn.Name), ""),
            new Shortcut(Key.F2, "Context", () => _viewModel.SetSort(SortColumn.ContextLength), ""),
            new Shortcut(Key.F3, "Input$", () => _viewModel.SetSort(SortColumn.InputPrice), ""),
            new Shortcut(Key.F4, "Output$", () => _viewModel.SetSort(SortColumn.OutputPrice), ""),
            new Shortcut(Key.F5, "Created", () => _viewModel.SetSort(SortColumn.Created), "")
        ])
        {
            X = 0,
            Y = Pos.AnchorEnd()
        };

        Add(_searchBar, _tableView, _detailView, statusBar);

        _searchBar.SearchTextChanged += text => _viewModel.SetSearchText(text);

        _tableView.ModelSelected += model =>
        {
            _viewModel.SelectedModel = model;
            _detailView.ShowModel(model);
        };

        _tableView.SortRequested += column => _viewModel.SetSort(column);

        _viewModel.StateChanged += () =>
        {
            App?.Invoke(() =>
            {
                _tableView.SetData(_viewModel.FilteredModels);
                _searchBar.SetStatusText(
                    $"{_viewModel.FilteredCount} of {_viewModel.TotalCount} models");
            });
        };

        Initialized += (_, _) => _ = LoadModelsAsync();
    }

    private void Quit() => App!.RequestStop(this);

    private async Task LoadModelsAsync()
    {
        App!.Invoke(() => _tableView.ShowLoading(true));

        try
        {
            await _viewModel.LoadModelsAsync();
            App.Invoke(() =>
            {
                _tableView.ShowLoading(false);
                _tableView.SetData(_viewModel.FilteredModels);
                _searchBar.SetStatusText(
                    $"{_viewModel.FilteredCount} of {_viewModel.TotalCount} models");
                _tableView.FocusTable();
            });
        }
        catch (Exception ex)
        {
            App.Invoke(() =>
            {
                _tableView.ShowLoading(false);
                MessageBox.ErrorQuery(App, "Error", $"Failed to load models: {ex.Message}", "Quit");
                App.RequestStop(this);
            });
        }
    }
}
