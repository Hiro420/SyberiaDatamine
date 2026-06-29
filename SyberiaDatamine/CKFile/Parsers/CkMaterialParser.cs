using SyberiaDatamine.CKFile.Objects;
using SyberiaDatamine.CKFile.Parsing;
using SyberiaDatamine.Core;

namespace SyberiaDatamine.CKFile.Parsers;

public sealed class CkMaterialParser : ICkObjectParser<CkMaterial>
{
	public static readonly CkMaterialParser Instance = new();
	public IReadOnlyList<int> SupportedClassIds { get; } = [30];

	public CkParseResult<CkMaterial> Parse(CkBinaryReader reader, NemoObject header)
	{
		try { return DoParse(reader, header); }
		catch (Exception ex) { return CkParseResult<CkMaterial>.Fail($"Material parse error: {ex.Message}"); }
	}

	private static CkParseResult<CkMaterial> DoParse(CkBinaryReader reader, NemoObject header)
	{
		int totalBytes = (int)reader.BytesRemaining;
		var raw = reader.ReadBytes(totalBytes);
		var state = CkStateChunkReader.TryParse(raw);
		var candidates = new SortedSet<int>();
		uint[] rawPayload = [];
		int textureObjectIndex = -1;

		if (state != null && state.TryGetPayload(0x00001000, out var payload))
		{
			rawPayload = payload.Select(v => unchecked((uint)v)).ToArray();

			if (payload.Length > 5 && payload[5] >= 0)
			{
				textureObjectIndex = payload[5];
				candidates.Add(textureObjectIndex);
			}

			foreach (var v in payload)
			{
				if (v >= 0 && v < 200_000)
					candidates.Add(v);
			}
		}

		var result = new CkMaterial
		{
			ObjectIndex = header.ObjectIndex,
			CkId = header.CkId,
			ClassId = header.ClassId,
			Name = header.Name,
			BytesConsumed = totalBytes,
			RawLength = totalBytes,
			HasStateChunk = state != null,
			TextureObjectIndex = textureObjectIndex,
			CandidateObjectIndices = candidates.ToList(),
			RawPayload = rawPayload,
		};

		return CkParseResult<CkMaterial>.Ok(result, totalBytes);
	}
}
