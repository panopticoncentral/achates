using Achates.Providers.Browser.Services;
using Achates.Providers.OpenRouter.Models;

namespace Achates.Providers.Browser.ViewModels;

public enum SortColumn
{
    Name,
    ContextLength,
    InputPrice,
    OutputPrice,
    Created
}

public sealed class ModelBrowserViewModel(ModelService modelService)
{
    private IReadOnlyList<OpenRouterModel> _allModels = [];
    private string _searchText = string.Empty;
    private SortColumn _sortColumn = SortColumn.Name;
    private bool _sortDescending;

    public IReadOnlyList<ModelRowData> FilteredModels { get; private set; } = [];
    public ModelRowData? SelectedModel { get; set; }
    public bool IsLoading { get; private set; }
    public int TotalCount => _allModels.Count;
    public int FilteredCount => FilteredModels.Count;

    public event Action? StateChanged;

    public async Task LoadModelsAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StateChanged?.Invoke();

        try
        {
            _allModels = await modelService.GetModelsAsync(cancellationToken)
                .ConfigureAwait(false);
            ApplyFilterAndSort();
        }
        finally
        {
            IsLoading = false;
            StateChanged?.Invoke();
        }
    }

    public void SetSearchText(string text)
    {
        _searchText = text;
        ApplyFilterAndSort();
        StateChanged?.Invoke();
    }

    public void SetSort(SortColumn column)
    {
        if (_sortColumn == column)
            _sortDescending = !_sortDescending;
        else
        {
            _sortColumn = column;
            _sortDescending = false;
        }

        ApplyFilterAndSort();
        StateChanged?.Invoke();
    }

    private void ApplyFilterAndSort()
    {
        var filtered = string.IsNullOrWhiteSpace(_searchText)
            ? _allModels
            : _allModels.Where(m =>
                m.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                m.Id.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
              .ToList();

        var sorted = _sortColumn switch
        {
            SortColumn.Name => _sortDescending
                ? filtered.OrderByDescending(m => m.Name)
                : filtered.OrderBy(m => m.Name),
            SortColumn.ContextLength => _sortDescending
                ? filtered.OrderByDescending(m => m.ContextLength)
                : filtered.OrderBy(m => m.ContextLength),
            SortColumn.InputPrice => _sortDescending
                ? filtered.OrderByDescending(m => ParsePrice(m.Pricing?.Prompt))
                : filtered.OrderBy(m => ParsePrice(m.Pricing?.Prompt)),
            SortColumn.OutputPrice => _sortDescending
                ? filtered.OrderByDescending(m => ParsePrice(m.Pricing?.Completion))
                : filtered.OrderBy(m => ParsePrice(m.Pricing?.Completion)),
            SortColumn.Created => _sortDescending
                ? filtered.OrderByDescending(m => m.Created)
                : filtered.OrderBy(m => m.Created),
            _ => filtered.OrderBy(m => m.Name)
        };

        FilteredModels = sorted.Select(ModelRowData.FromModel).ToList();
    }

    private static decimal ParsePrice(string? price) =>
        decimal.TryParse(price, out var val) ? val : decimal.MaxValue;
}
