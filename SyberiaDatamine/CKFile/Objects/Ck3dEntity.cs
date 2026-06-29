using System.Globalization;
using System.Numerics;
using System.Text;

namespace SyberiaDatamine.CKFile.Objects;

public sealed class Ck3dEntity : CkObject
{
	public bool HasStateChunk { get; init; }
	public bool HasTransform { get; init; }
	public float[] Transform3x4 { get; init; } = [];
	public Vector3? Position { get; init; }
	public IReadOnlyList<uint> ReferencedIds { get; init; } = [];
	public IReadOnlyList<uint> MeshCandidateIds { get; init; } = [];
	public IReadOnlyList<int> ReferencedObjectIndices { get; init; } = [];
	public IReadOnlyList<int> MeshCandidateObjectIndices { get; init; } = [];
	public int RawLength { get; init; }
	public int IdentifierCount { get; init; }

	public override string Summary
	{
		get
		{
			var refs = ReferencedIds.Count > 0
				? $", refs: {string.Join(", ", ReferencedIds.Take(8).Select(id => $"0x{id:X8}"))}"
				: ReferencedObjectIndices.Count > 0
					? $", obj refs: {string.Join(", ", ReferencedObjectIndices.Take(8))}"
					: "";
			var pos = Position.HasValue
				? $", pos: {Position.Value.X:G5}, {Position.Value.Y:G5}, {Position.Value.Z:G5}"
				: "";
			return HasStateChunk
				? $"3D Entity  {RawLength:N0} bytes, {IdentifierCount} identifier(s){pos}{refs}"
				: $"3D Entity  {RawLength:N0} bytes (no state chunk)";
		}
	}

	public string ToDiagnosticText()
	{
		var sb = new StringBuilder();
		var display = string.IsNullOrWhiteSpace(Name) ? $"0x{CkId:X8}" : Name;
		sb.AppendLine($"{display} — {ClassName} (ClassID {ClassId})");
		sb.AppendLine($"CK_ID: 0x{CkId:X8}");
		sb.AppendLine($"Raw length: {RawLength.ToString("N0", CultureInfo.InvariantCulture)} bytes");
		sb.AppendLine($"State chunk: {(HasStateChunk ? "yes" : "no")}");

		if (HasTransform && Transform3x4.Length >= 12)
		{
			sb.AppendLine();
			sb.AppendLine("Transform 3×4:");
			for (int r = 0; r < 3; r++)
			{
				sb.Append("  ");
				for (int c = 0; c < 4; c++)
					sb.Append(Transform3x4[r * 4 + c].ToString("G7", CultureInfo.InvariantCulture).PadLeft(13));
				sb.AppendLine();
			}
		}

		if (Position.HasValue)
		{
			var p = Position.Value;
			sb.AppendLine($"Position: {p.X.ToString("G7", CultureInfo.InvariantCulture)}, {p.Y.ToString("G7", CultureInfo.InvariantCulture)}, {p.Z.ToString("G7", CultureInfo.InvariantCulture)}");
		}

		if (ReferencedIds.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("Referenced CK_ID candidates:");
			foreach (var id in ReferencedIds.Take(64))
				sb.AppendLine($"  0x{id:X8}");
			if (ReferencedIds.Count > 64)
				sb.AppendLine($"  … ({ReferencedIds.Count - 64} more)");
		}

		if (ReferencedObjectIndices.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("Referenced object-index candidates:");
			foreach (var idx in ReferencedObjectIndices.Take(96))
				sb.AppendLine($"  {idx}");
			if (ReferencedObjectIndices.Count > 96)
				sb.AppendLine($"  … ({ReferencedObjectIndices.Count - 96} more)");
		}

		return sb.ToString();
	}
}
