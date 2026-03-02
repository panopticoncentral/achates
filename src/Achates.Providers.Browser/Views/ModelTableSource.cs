using Achates.Providers.Browser.ViewModels;
using Terminal.Gui.Views;

namespace Achates.Providers.Browser.Views;

internal sealed class ModelTableSource(IReadOnlyList<ModelRowData> data) : ITableSource
{
    private static readonly string[] Columns =
        ["Name", "Context", "Input $/M", "Output $/M", "Modality", "Created"];

    public string[] ColumnNames => Columns;

    public int Rows => data.Count;

    public int Columns1 => Columns.Length;

    int ITableSource.Columns => Columns.Length;

    public object this[int row, int col] => col switch
    {
        0 => data[row].Name,
        1 => data[row].ContextLength,
        2 => data[row].InputPrice,
        3 => data[row].OutputPrice,
        4 => data[row].Modality,
        5 => data[row].Created,
        _ => string.Empty
    };
}
