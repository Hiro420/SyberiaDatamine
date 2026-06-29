using System.Globalization;
using System.Text;

namespace SyberiaDatamine.CKFile.Objects;

public enum DataArrayColumnType
{
	Int = 0,
	Float = 1,
	String = 2,
	Object = 3,
	Parameter = 4,
	Unknown = -1,
}

public sealed class DataArrayColumn
{
	public required string Name { get; init; }
	public DataArrayColumnType Type { get; init; }

	public override string ToString() => $"{Name} ({Type})";
}

public sealed class CkDataArray : CkObject
{

	public IReadOnlyList<DataArrayColumn> Columns { get; init; } = [];

	public IReadOnlyList<object?[]> Rows { get; init; } = [];

	public override string Summary
		=> $"DataArray  {Columns.Count} column(s) × {Rows.Count} row(s)";

	public string ToTabSeparatedText(int maxRows = 200)
	{
		if (Columns.Count == 0)
			return "(no columns)";

		var widths = Columns.Select(c => c.Name.Length).ToArray();
		int scanLimit = Math.Min(Rows.Count, maxRows);
		for (int r = 0; r < scanLimit; r++)
			for (int c = 0; c < Columns.Count; c++)
				widths[c] = Math.Max(widths[c], FormatCell(Rows[r][c]).Length);

		var sb = new StringBuilder();

		for (int c = 0; c < Columns.Count; c++)
		{
			if (c > 0) sb.Append("  ");
			sb.Append(c < Columns.Count - 1
				? Columns[c].Name.PadRight(widths[c])
				: Columns[c].Name);
		}
		sb.AppendLine();

		int count = 0;
		foreach (var row in Rows)
		{
			if (count++ >= maxRows)
			{
				sb.AppendLine($"… ({Rows.Count - maxRows} more rows truncated)");
				break;
			}
			for (int c = 0; c < Columns.Count; c++)
			{
				if (c > 0) sb.Append("  ");
				string cell = FormatCell(row[c]);
				sb.Append(c < Columns.Count - 1 ? cell.PadRight(widths[c]) : cell);
			}
			sb.AppendLine();
		}

		return sb.ToString();
	}

	private static string FormatCell(object? cell) => cell switch
	{
		null => "",
		float f => f.ToString("G6", CultureInfo.InvariantCulture),
		double d => d.ToString("G6", CultureInfo.InvariantCulture),
		uint u => $"0x{u:X8}",
		_ => cell.ToString() ?? "",
	};
}
