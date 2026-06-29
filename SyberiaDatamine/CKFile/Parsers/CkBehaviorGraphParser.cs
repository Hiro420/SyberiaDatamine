using SyberiaDatamine.CKFile.Objects;
using SyberiaDatamine.CKFile.Parsing;
using SyberiaDatamine.Core;

namespace SyberiaDatamine.CKFile.Parsers;

public sealed class CkBehaviorGraphParser : ICkObjectParser<CkBehaviorGraph>
{
	public static readonly CkBehaviorGraphParser Instance = new();

	public IReadOnlyList<int> SupportedClassIds { get; } = [2, 3, 4, 6, 8, 9, 45, 46];

	public CkParseResult<CkBehaviorGraph> Parse(CkBinaryReader reader, NemoObject header)
	{
		try { return DoParse(reader, header); }
		catch (Exception ex) { return CkParseResult<CkBehaviorGraph>.Fail($"Behavior parse error: {ex.Message}"); }
	}

	private static CkParseResult<CkBehaviorGraph> DoParse(CkBinaryReader reader, NemoObject header)
	{
		int totalBytes = (int)reader.BytesRemaining;
		var raw = reader.ReadBytes(totalBytes);
		var state = CkStateChunkReader.TryParse(raw);
		var identifiers = new List<CkStateIdentifier>();
		var payloads = new Dictionary<uint, int[]>();
		var candidates = new SortedSet<int>();

		if (state != null)
		{
			foreach (var ident in state.EnumerateIdentifiers())
			{
				identifiers.Add(ident);
				if (!state.TryGetPayload(ident.Id, out var payload))
					continue;

				payloads[ident.Id] = payload;

				foreach (var v in payload)
				{

					if (v >= 0 && v < 200_000 && v != header.ObjectIndex)
						candidates.Add(v);
				}
			}
		}

		var result = new CkBehaviorGraph
		{
			ObjectIndex = header.ObjectIndex,
			CkId = header.CkId,
			ClassId = header.ClassId,
			Name = header.Name,
			BytesConsumed = totalBytes,
			RawLength = totalBytes,
			HasStateChunk = state != null,
			Identifiers = identifiers,
			CandidateObjectIndices = candidates.ToList(),
			PayloadsByIdentifier = payloads,
		};

		return CkParseResult<CkBehaviorGraph>.Ok(result, totalBytes);
	}
}
