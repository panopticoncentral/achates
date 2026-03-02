using Achates.Providers.Browser.ViewModels;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Achates.Providers.Browser.Views;

public sealed class ModelTableView : FrameView
{
    private readonly TableView _table;
    private readonly Label _loadingLabel;
    private IReadOnlyList<ModelRowData> _currentData = [];

    public event Action<ModelRowData>? ModelSelected;
    public event Action<SortColumn>? SortRequested;

    public ModelTableView()
    {
        Title = "Models";

        _table = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            FullRowSelect = true
        };

        _loadingLabel = new Label
        {
            Text = "Loading models...",
            X = Pos.Center(),
            Y = Pos.Center(),
            Visible = false
        };

        _table.SelectedCellChanged += (_, args) =>
        {
            if (args.NewRow >= 0 && args.NewRow < _currentData.Count)
                ModelSelected?.Invoke(_currentData[args.NewRow]);
        };

        _table.CellActivated += (_, args) =>
        {
            if (args.Row >= 0 && args.Row < _currentData.Count)
                ModelSelected?.Invoke(_currentData[args.Row]);
        };

        _table.KeyDown += (_, args) =>
        {
            var column = args.KeyCode switch
            {
                KeyCode.F1 => (SortColumn?)SortColumn.Name,
                KeyCode.F2 => (SortColumn?)SortColumn.ContextLength,
                KeyCode.F3 => (SortColumn?)SortColumn.InputPrice,
                KeyCode.F4 => (SortColumn?)SortColumn.OutputPrice,
                KeyCode.F5 => (SortColumn?)SortColumn.Created,
                _ => null
            };

            if (column is not null)
            {
                SortRequested?.Invoke(column.Value);
                args.Handled = true;
            }
        };

        Add(_table, _loadingLabel);
    }

    public void ShowLoading(bool loading)
    {
        _loadingLabel.Visible = loading;
        _table.Visible = !loading;
    }

    public void SetData(IReadOnlyList<ModelRowData> data)
    {
        _currentData = data;
        _table.Table = new ModelTableSource(data);
    }

    public void FocusTable() => _table.SetFocus();
}
