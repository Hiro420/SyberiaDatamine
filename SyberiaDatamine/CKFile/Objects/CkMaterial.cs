using System.Globalization;
using System.Text;

namespace SyberiaDatamine.CKFile.Objects;

public sealed class CkMaterial : CkObject
{
	public int RawLength { get; init; }
	public bool HasStateChunk { get; init; }
	public int TextureObjectIndex { get; init; } = -1;
	public IReadOnlyList<int> CandidateObjectIndices { get; init; } = [];
	public IReadOnlyList<uint> RawPayload { get; init; } = [];

	public override string Summary
		=> TextureObjectIndex >= 0
			? $"Material  texture object index {TextureObjectIndex}"
			: HasStateChunk
				? $"Material  {CandidateObjectIndices.Count} object-reference candidate(s)"
				: $"Material  {RawLength:N0} bytes (no state chunk)";

	public string ToDiagnosticText()
	{
		var sb = new StringBuilder();
		var display = string.IsNullOrWhiteSpace(Name) ? $"0x{CkId:X8}" : Name;
		sb.AppendLine($"{display} — {ClassName} (ClassID {ClassId})");
		sb.AppendLine($"Object table index: {ObjectIndexText()}");
		sb.AppendLine($"CK_ID: 0x{CkId:X8}");
		sb.AppendLine($"Raw length: {RawLength.ToString("N0", CultureInfo.InvariantCulture)} bytes");
		sb.AppendLine($"State chunk: {(HasStateChunk ? "yes" : "no")}");
		sb.AppendLine($"Texture object index: {(TextureObjectIndex >= 0 ? TextureObjectIndex.ToString(CultureInfo.InvariantCulture) : "(none detected)")}");

		if (CandidateObjectIndices.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("Object-index candidates:");
			foreach (var idx in CandidateObjectIndices.Take(64))
				sb.AppendLine($"  {idx}");
			if (CandidateObjectIndices.Count > 64)
				sb.AppendLine($"  … ({CandidateObjectIndices.Count - 64} more)");
		}

		if (RawPayload.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("Material payload DWORDs:");
			for (int i = 0; i < RawPayload.Count; i++)
				sb.AppendLine($"  [{i,2}] 0x{RawPayload[i]:X8}");
		}

		return sb.ToString();
	}

	private string ObjectIndexText() => ObjectIndex >= 0 ? ObjectIndex.ToString(CultureInfo.InvariantCulture) : "(unknown)";
}
