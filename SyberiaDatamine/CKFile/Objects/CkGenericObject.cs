using SyberiaDatamine.CKFile.Parsing;
using System.Globalization;
using System.Text;

namespace SyberiaDatamine.CKFile.Objects;

public sealed class CkGenericObject : CkObject
{
	public int RawLength { get; init; }
	public bool HasStateChunk { get; init; }
	public int DataVersion { get; init; }
	public int ChunkVersion { get; init; }
	public int ChunkOptions { get; init; }
	public int StateClassId { get; init; }
	public int DwordCount { get; init; }
	public IReadOnlyList<CkStateIdentifier> Identifiers { get; init; } = [];

	public override string Summary
		=> HasStateChunk
			? $"{RawLength:N0} bytes, CKStateChunk class {StateClassId}, {Identifiers.Count} identifier(s)"
			: $"{RawLength:N0} bytes, no CKStateChunk header found";

	public string ToDiagnosticText(byte[]? raw = null, int maxPayloadDwords = 64)
	{
		var sb = new StringBuilder();
		var display = string.IsNullOrWhiteSpace(Name) ? $"0x{CkId:X8}" : Name;
		sb.AppendLine($"{display} — {ClassName} (ClassID {ClassId})");
		sb.AppendLine($"CK_ID: 0x{CkId:X8}");
		sb.AppendLine($"Raw length: {RawLength.ToString("N0", CultureInfo.InvariantCulture)} bytes");

		if (!HasStateChunk)
		{
			sb.AppendLine("State chunk: not found");
			if (raw != null && raw.Length > 0)
			{
				sb.AppendLine();
				sb.AppendLine("Raw bytes preview:");
				sb.AppendLine(HexPreview(raw, 256));
			}
			return sb.ToString();
		}

		sb.AppendLine($"State chunk: data v{DataVersion}, chunk v{ChunkVersion}, options 0x{ChunkOptions:X2}, dwords {DwordCount}");
		sb.AppendLine();
		sb.AppendLine("Identifiers:");
		foreach (var id in Identifiers)
			sb.AppendLine($"  0x{id.Id:X8}  offset={id.Offset}  next={id.Next}  payload={id.PayloadDwordCount} dword(s)");

		if (raw != null && raw.Length > 0)
		{
			sb.AppendLine();
			sb.AppendLine("Raw bytes preview:");
			sb.AppendLine(HexPreview(raw, 256));
		}

		return sb.ToString();
	}

	private static string HexPreview(byte[] bytes, int maxBytes)
	{
		var sb = new StringBuilder();
		int len = Math.Min(bytes.Length, maxBytes);
		for (int i = 0; i < len; i += 16)
		{
			var chunk = bytes.Skip(i).Take(Math.Min(16, len - i)).ToArray();
			sb.Append(i.ToString("X8", CultureInfo.InvariantCulture));
			sb.Append("  ");
			sb.Append(string.Join(" ", chunk.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))).PadRight(47));
			sb.Append("  ");
			sb.AppendLine(new string(chunk.Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray()));
		}
		if (bytes.Length > maxBytes)
			sb.AppendLine($"… ({bytes.Length - maxBytes:N0} more bytes)");
		return sb.ToString();
	}
}
