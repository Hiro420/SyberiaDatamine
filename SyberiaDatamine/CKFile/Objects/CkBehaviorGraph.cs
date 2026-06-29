using SyberiaDatamine.CKFile.Parsing;
using System.Globalization;
using System.Text;

namespace SyberiaDatamine.CKFile.Objects;

public sealed class CkBehaviorGraph : CkObject
{
	public int RawLength { get; init; }
	public bool HasStateChunk { get; init; }
	public IReadOnlyList<CkStateIdentifier> Identifiers { get; init; } = [];
	public IReadOnlyList<int> CandidateObjectIndices { get; init; } = [];
	public IReadOnlyDictionary<uint, int[]> PayloadsByIdentifier { get; init; } = new Dictionary<uint, int[]>();

	public override string Summary
		=> HasStateChunk
			? $"Behavior graph  {Identifiers.Count} identifier(s), {CandidateObjectIndices.Count} object-index candidate(s)"
			: $"Behavior graph  {RawLength:N0} bytes (no state chunk)";

	public string ToDiagnosticText(Func<int, string?>? objectLabelResolver = null)
	{
		var sb = new StringBuilder();
		var display = string.IsNullOrWhiteSpace(Name) ? $"0x{CkId:X8}" : Name;
		sb.AppendLine($"{display} — {ClassName} (ClassID {ClassId})");
		sb.AppendLine($"Object table index: {ObjectIndex.ToString(CultureInfo.InvariantCulture)}");
		sb.AppendLine($"CK_ID: 0x{CkId:X8}");
		sb.AppendLine($"Raw length: {RawLength.ToString("N0", CultureInfo.InvariantCulture)} bytes");
		sb.AppendLine($"State chunk: {(HasStateChunk ? "yes" : "no")}");

		if (Identifiers.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("Identifiers:");
			foreach (var id in Identifiers)
				sb.AppendLine($"  0x{id.Id:X8}  offset={id.Offset}  next={id.Next}  payload={id.PayloadDwordCount} dword(s)");
		}

		if (CandidateObjectIndices.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("Linked object-index candidates:");
			foreach (var idx in CandidateObjectIndices.Take(96))
			{
				var label = objectLabelResolver?.Invoke(idx);
				sb.AppendLine(string.IsNullOrWhiteSpace(label) ? $"  {idx}" : $"  {idx}  → {label}");
			}
			if (CandidateObjectIndices.Count > 96)
				sb.AppendLine($"  … ({CandidateObjectIndices.Count - 96} more)");
		}

		return sb.ToString();
	}
}
