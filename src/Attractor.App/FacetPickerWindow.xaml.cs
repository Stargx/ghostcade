using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace Attractor.App;

/// <summary>
/// A "More…" facet picker: a searchable, checkable list of every value in the
/// catalog for one filter facet (manufacturers, genres, …). Returns the chosen
/// names via <see cref="SelectedNames"/> when applied.
/// </summary>
public partial class FacetPickerWindow : Window
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

    public FacetPickerWindow(
        string title, string heading, string hint,
        IEnumerable<(string Name, int Count)> items, IReadOnlySet<string> selected)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Dwm.ApplyDarkTitleBar(this);
        Title = title;
        Heading.Text = heading;
        Hint.Text = hint;

        _rows = items
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new Row { Name = i.Name, Count = i.Count, IsSelected = selected.Contains(i.Name) })
            .ToList();
        _view = CollectionViewSource.GetDefaultView(_rows);
        List.ItemsSource = _view;
    }

    /// <summary>The names ticked when APPLY was pressed.</summary>
    public IReadOnlyList<string> SelectedNames { get; private set; } = [];

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
        SelectedNames = _rows.Where(r => r.IsSelected).Select(r => r.Name).ToArray();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
