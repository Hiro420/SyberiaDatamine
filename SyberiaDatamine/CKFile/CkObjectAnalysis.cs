using SyberiaDatamine.Core;
using System.Text;

namespace SyberiaDatamine.CKFile;

public static class CkObjectAnalysis
{
	public static string BuildSummary(NemoObject obj, byte[] raw)
	{
		var sb = new StringBuilder();
		sb.AppendLine($"Raw payload: {raw.Length:N0} bytes");

		if (NemoClassRegistry.IsGeometry(obj.ClassId))
		{
			sb.AppendLine("Category: Mesh/3D payload");
			sb.AppendLine("Status: Structural parser scaffolded, field-level mesh decode pending.");
		}
		else if (obj.GroupName == "Behaviors")
		{
			sb.AppendLine("Category: Behavior graph payload");
			sb.AppendLine("Status: Token/edge decode pending.");
		}
		else if (obj.GroupName == "DataArrays")
		{
			sb.AppendLine("Category: DataArray payload");
			sb.AppendLine("Status: CSV-like table decode in progress.");
		}

		return sb.ToString().TrimEnd();
	}

	public static bool TryDecodeDataArrayPreview(byte[] raw, out string tablePreview)
	{
		tablePreview = string.Empty;
		if (raw.Length == 0)
			return false;

		var txt = Encoding.UTF8.GetString(raw);
		if (string.IsNullOrWhiteSpace(txt))
			return false;

		var lines = txt
			.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(l => l.Trim())
			.Where(l => l.Length > 0 && l.Any(ch => char.IsLetterOrDigit(ch)))
			.Take(60)
			.ToList();

		if (lines.Count < 2)
			return false;

		tablePreview = string.Join(Environment.NewLine, lines);
		return true;
	}
}
