namespace AssemblyManagerPlugin.Core;

/// <summary>A stable, selectable column with the same meaning in every BOM section.</summary>
public sealed record PlacedBomColumn(string Id, string Title, bool DefaultSelected);

public sealed class PlacedBomResult
{
    public int ObjectCount { get; init; }
    public double TextHeight { get; init; }
    public bool WasReduced { get; init; }
}

/// <summary>Measured paper-space layout. Coordinates start at the top left; Y grows down.</summary>
public sealed class PlacedBomLayout
{
    public IReadOnlyList<PlacedBomColumn> Columns { get; init; } = Array.Empty<PlacedBomColumn>();
    public IReadOnlyList<double> ColumnWidths { get; init; } = Array.Empty<double>();
    public IReadOnlyList<PlacedBomLayoutRow> Rows { get; init; } = Array.Empty<PlacedBomLayoutRow>();
    public double Width { get; init; }
    public double Height { get; init; }
    public double TextHeight { get; init; }
    public double StandardTextHeight { get; init; }
    public bool WasReduced => TextHeight < StandardTextHeight * (1.0 - 1e-6);
}

public sealed class PlacedBomLayoutRow
{
    public bool IsSection { get; init; }
    public double Top { get; init; }
    public double Height { get; init; }
    public double TextHeight { get; init; }
    public double Padding { get; init; }
    public double LineHeight { get; init; }
    public IReadOnlyList<PlacedBomLayoutCell> Cells { get; init; } = Array.Empty<PlacedBomLayoutCell>();
}

public sealed class PlacedBomLayoutCell
{
    public double Left { get; init; }
    public double Width { get; init; }
    public string OriginalText { get; init; } = string.Empty;
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
}
