using SyberiaDatamine.CKFile.Objects;
using SyberiaDatamine.CKFile.Parsing;
using SyberiaDatamine.Core;

namespace SyberiaDatamine.CKFile.Parsers;

public static class CkGenericStateChunkParser
{
	public static CkGenericObject Parse(byte[] rawData, NemoObject header)
	{
		var state = rawData.Length > 0 ? CkStateChunkReader.TryParse(rawData) : null;
		var identifiers = state?.EnumerateIdentifiers().ToList() ?? [];

		return new CkGenericObject
		{
			ObjectIndex = header.ObjectIndex,
			CkId = header.CkId,
			ClassId = header.ClassId,
			Name = header.Name,
			BytesConsumed = rawData.Length,
			RawLength = rawData.Length,
			HasStateChunk = state != null,
			DataVersion = state?.DataVersion ?? 0,
			ChunkVersion = state?.ChunkVersion ?? 0,
			ChunkOptions = state?.ChunkOptions ?? 0,
			StateClassId = state?.ClassId ?? 0,
			DwordCount = state?.DwordCount ?? 0,
			Identifiers = identifiers,
		};
	}
}
