using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace Attractor.App;

/// <summary>
/// The "More…" manufacturer picker: a searchable, checkable list of every
/// manufacturer in the catalog. Returns the chosen names via
/// <see cref="SelectedManufacturers"/> when applied.
/// </summary>
public partial class ManufacturerFilterWindow : Window
{
    private sealed class Row
    {
        public string Name { get; init; } = "";
        public int Count { get; init; }
        public bool IsSelected { get; set; }
        public string Display => $"{Name} ({Count})";
    }

    private readonly List<Row> _rows;
    private readonly ICollectionView _view;

    public ManufacturerFilterWindow(IReadOnlyList<ManufacturerCount> manufacturers, IReadOnlySet<string> selected)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Dwm.ApplyDarkTitleBar(this);

        _rows = manufacturers
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new Row { Name = m.Name, Count = m.Count, IsSelected = selected.Contains(m.Name) })
            .ToList();
        _view = CollectionViewSource.GetDefaultView(_rows);
        List.ItemsSource = _view;
    }

    /// <summary>The manufacturers ticked when APPLY was pressed.</summary>
    public IReadOnlyList<string> SelectedManufacturers { get; private set; } = [];

    private void SearchBox_TextChanged(object sender, RoutedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        _view.Filter = q.Length == 0
            ? null
            : o => ((Row)o).Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows)
            r.IsSelected = false;
        _view.Refresh(); // re-render the checkboxes from the reset state
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SelectedManufacturers = _rows.Where(r => r.IsSelected).Select(r => r.Name).ToArray();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
