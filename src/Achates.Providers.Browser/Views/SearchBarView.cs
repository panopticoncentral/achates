using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Achates.Providers.Browser.Views;

public sealed class SearchBarView : FrameView
{
    private readonly TextField _searchField;
    private readonly Label _statusLabel;

    public event Action<string>? SearchTextChanged;

    public SearchBarView()
    {
        Title = "Search";

        var searchLabel = new Label
        {
            Text = "Filter: ",
            X = 0,
            Y = 0
        };

        _searchField = new TextField
        {
            X = Pos.Right(searchLabel),
            Y = 0,
            Width = Dim.Percent(50)
        };

        _statusLabel = new Label
        {
            X = Pos.Right(_searchField) + 2,
            Y = 0,
            Width = Dim.Fill()
        };

        _searchField.TextChanged += (_, _) =>
            SearchTextChanged?.Invoke(_searchField.Text);

        Add(searchLabel, _searchField, _statusLabel);
    }

    public void SetStatusText(string text) => _statusLabel.Text = text;

    public void FocusSearch() => _searchField.SetFocus();

    public void ClearSearch()
    {
        _searchField.Text = string.Empty;
    }
}
