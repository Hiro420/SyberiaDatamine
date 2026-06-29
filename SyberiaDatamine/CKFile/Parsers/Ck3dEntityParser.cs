using SyberiaDatamine.CKFile.Objects;
using SyberiaDatamine.CKFile.Parsing;
using SyberiaDatamine.Core;
using System.Numerics;

namespace SyberiaDatamine.CKFile.Parsers;

public sealed class Ck3dEntityParser : ICkObjectParser<Ck3dEntity>
{
	public static readonly Ck3dEntityParser Instance = new();

	public IReadOnlyList<int> SupportedClassIds { get; } = [33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 47];

	public CkParseResult<Ck3dEntity> Parse(CkBinaryReader reader, NemoObject header)
	{
		try { return DoParse(reader, header); }
		catch (Exception ex) { return CkParseResult<Ck3dEntity>.Fail($"3D entity parse error: {ex.Message}"); }
	}

	private static CkParseResult<Ck3dEntity> DoParse(CkBinaryReader reader, NemoObject header)
	{
		int totalBytes = (int)reader.BytesRemaining;
		var raw = reader.ReadBytes(totalBytes);
		var state = CkStateChunkReader.TryParse(raw);
		var refs = new SortedSet<uint>();
		var meshCandidates = new SortedSet<uint>();
		var objectIndexRefs = new SortedSet<int>();
		var meshObjectIndexCandidates = new SortedSet<int>();
		float[] transform = [];
		Vector3? position = null;

		if (state != null)
		{
			foreach (var id in state.EnumerateIdentifiers())
			{
				if (!state.TryGetPayload(id.Id, out var payload))
					continue;

				if (id.Id == 0x00100000 && payload.Length >= 14)
				{
					transform = new float[12];
					bool plausible = true;
					for (int i = 0; i < 12; i++)
					{
						float f = BitConverter.Int32BitsToSingle(payload[i + 2]);
						if (float.IsNaN(f) || float.IsInfinity(f)) plausible = false;
						transform[i] = f;
					}

					if (plausible)
						position = new Vector3(transform[9], transform[10], transform[11]);
				}

				if (id.Id == 0x00000800 && payload.Length >= 2)
					AddCountedObjectIndexes(payload, meshObjectIndexCandidates);

				if (id.Id == 0x00004000 && payload.Length > 0)
					AddObjectIndexes(payload, meshObjectIndexCandidates);

				if (id.Id == 0x00400000 || id.Id == 0xFFC00000)
					AddObjectIndexes(payload, meshObjectIndexCandidates);

				foreach (var v in payload)
				{
					uint u = unchecked((uint)v);

					if (u >= 0x20 && u <= 0xFFFF)
						refs.Add(u);

					if (v >= 0 && v < 200_000)
						objectIndexRefs.Add(v);
				}
			}
		}

		foreach (var id in meshCandidates)
			refs.Add(id);

		var result = new Ck3dEntity
		{
			ObjectIndex = header.ObjectIndex,
			CkId = header.CkId,
			ClassId = header.ClassId,
			Name = header.Name,
			BytesConsumed = totalBytes,
			RawLength = totalBytes,
			HasStateChunk = state != null,
			HasTransform = transform.Length == 12,
			Transform3x4 = transform,
			Position = position,
			ReferencedIds = refs.ToList(),
			MeshCandidateIds = meshCandidates.ToList(),
			ReferencedObjectIndices = objectIndexRefs.ToList(),
			MeshCandidateObjectIndices = meshObjectIndexCandidates.ToList(),
			IdentifierCount = state?.EnumerateIdentifiers().Count() ?? 0,
		};

		return CkParseResult<Ck3dEntity>.Ok(result, totalBytes);
	}

	private static void AddCountedObjectIndexes(IReadOnlyList<int> payload, ISet<int> dst)
	{
		if (payload.Count == 0) return;
		int count = payload[0];
		if (count <= 0 || count > 4096)
		{
			AddObjectIndexes(payload, dst);
			return;
		}

		for (int i = 0; i < count && i + 1 < payload.Count; i++)
		{
			int idx = payload[i + 1];
			if (idx >= 0 && idx < 200_000)
				dst.Add(idx);
		}
	}

	private static void AddObjectIndexes(IEnumerable<int> payload, ISet<int> dst)
	{
		foreach (var idx in payload)
			if (idx >= 0 && idx < 200_000)
				dst.Add(idx);
	}
}
