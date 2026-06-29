using SyberiaDatamine.CKFile.Objects;
using SyberiaDatamine.CKFile.Parsing;
using SyberiaDatamine.Core;
using System.Numerics;

namespace SyberiaDatamine.CKFile.Parsers;

public sealed class CkMeshParser : ICkObjectParser<CkMesh>
{
	public static readonly CkMeshParser Instance = new();

	public IReadOnlyList<int> SupportedClassIds { get; } = [32, 53];

	public CkParseResult<CkMesh> Parse(CkBinaryReader reader, NemoObject header)
	{
		try { return DoParse(reader, header); }
		catch (Exception ex)
		{ return CkParseResult<CkMesh>.Fail($"Mesh parse error: {ex.Message}"); }
	}

	private static CkParseResult<CkMesh> DoParse(CkBinaryReader reader, NemoObject header)
	{
		int totalBytes = (int)reader.BytesRemaining;
		var raw = reader.ReadBytes(totalBytes);

		var state = CkStateChunkReader.TryParse(raw);
		if (state == null)
			return CkParseResult<CkMesh>.Fail("Not a valid CKStateChunk");

		int[]? indices = null;
		int[]? faceMaterials = null;
		int faceCount = 0;

		if (state.TryGetPayload(CkStateChunkReader.CK_STATESAVE_MESHFACES, out var facePayload))
		{
			var faceBytes = CkStateChunkReader.PayloadToBytes(facePayload);
			faceCount = faceBytes.Length >= 4 ? ReadU32(faceBytes, 0) : 0;
			if (faceCount < 0 || faceCount > 4_000_000)
				return CkParseResult<CkMesh>.Fail($"Invalid face count: {faceCount}");

			indices = new int[faceCount * 3];
			faceMaterials = new int[faceCount];
			for (int i = 0; i < faceCount; i++)
			{
				int off = 4 + i * 8;
				if (off + 8 > faceBytes.Length)
				{
					Array.Resize(ref indices, i * 3);
					if (faceMaterials != null) Array.Resize(ref faceMaterials, i);
					faceCount = i;
					break;
				}

				indices[i * 3 + 0] = ReadU16(faceBytes, off);
				indices[i * 3 + 1] = ReadU16(faceBytes, off + 2);
				indices[i * 3 + 2] = ReadU16(faceBytes, off + 4);
				faceMaterials[i] = ReadU16(faceBytes, off + 6);
			}
		}

		Vector3[]? vertices = null;
		Vector3[]? normals = null;
		(float U, float V)[]? uvs = null;
		int vertexCount = 0;
		int vertexFlags = 0;
		int vertexUnknown = 0;

		if (state.TryGetPayload(CkStateChunkReader.CK_STATESAVE_MESHVERTICES, out var vertexPayload)
			&& vertexPayload.Length >= 3)
		{
			vertexCount = vertexPayload[0];
			vertexFlags = vertexPayload[1];
			vertexUnknown = vertexPayload[2];

			if (vertexCount > 0 && vertexCount <= 2_000_000)
			{
				int p = 3;
				if (p + vertexCount * 3 <= vertexPayload.Length)
				{
					vertices = new Vector3[vertexCount];
					for (int i = 0; i < vertexCount; i++)
					{
						float x = IntBitsToFloat(vertexPayload[p++]);
						float z = IntBitsToFloat(vertexPayload[p++]);
						float y = IntBitsToFloat(vertexPayload[p++]);
						vertices[i] = new Vector3(x, y, z);
					}

					if (p + vertexCount * 3 <= vertexPayload.Length)
					{
						normals = new Vector3[vertexCount];
						for (int i = 0; i < vertexCount; i++)
						{
							float x = IntBitsToFloat(vertexPayload[p++]);
							float z = IntBitsToFloat(vertexPayload[p++]);
							float y = IntBitsToFloat(vertexPayload[p++]);
							normals[i] = new Vector3(x, y, z);
						}
					}

					if (p + vertexCount * 2 <= vertexPayload.Length)
					{
						uvs = new (float U, float V)[vertexCount];
						for (int i = 0; i < vertexCount; i++)
						{
							float u = IntBitsToFloat(vertexPayload[p++]);
							float v = IntBitsToFloat(vertexPayload[p++]);
							uvs[i] = (u, v);
						}
					}
				}
			}
		}

		var materialObjectIndices = new List<int>();
		if (state.TryGetPayload(CkStateChunkReader.CK_STATESAVE_MESHMATERIALS, out var matPayload)
			&& matPayload.Length > 0)
		{
			int count = matPayload[0];
			if (count > 0 && count < 65536)
			{
				for (int i = 0; i < count; i++)
				{
					int pMat = 1 + i * 2;
					if (pMat >= matPayload.Length) break;
					int objectIndex = matPayload[pMat];
					materialObjectIndices.Add(objectIndex);
				}
			}
		}

		bool needsBodypart = vertices == null && indices != null && faceCount > 0;
		bool succeeded = (vertices != null && indices != null && vertexCount > 0 && faceCount > 0)
						 || needsBodypart;

		return CkParseResult<CkMesh>.Ok(new CkMesh
		{
			ObjectIndex = header.ObjectIndex,
			CkId = header.CkId,
			ClassId = header.ClassId,
			Name = header.Name,
			VertexCount = vertexCount,
			FaceCount = faceCount,
			Vertices = vertices,
			Indices = indices,
			FaceMaterialIndices = faceMaterials,
			MaterialObjectIndices = materialObjectIndices.ToArray(),
			Normals = normals,
			UVs = uvs,
			ParseSucceeded = succeeded,
			NeedsBodypartVertices = needsBodypart,
			ParseNote = succeeded ? "" : BuildNote(vertices, indices, vertexCount, vertexFlags, vertexUnknown),
			BytesConsumed = totalBytes,
		}, totalBytes);
	}

	public static Vector3[]? ExtractBodypartVertices(byte[] raw)
	{
		var state = CkStateChunkReader.TryParse(raw);
		if (state == null || !state.TryGetPayload(0x00200000, out var payload))
			return null;

		var chunk = CkStateChunkReader.PayloadToBytes(payload);
		int p = 0;

		p += 16 * 4;
		if (p + 4 > chunk.Length) return null;

		int childCount = ReadU32(chunk, p); p += 4;
		if (childCount < 0 || childCount > 100_000) return null;

		p += childCount * 4;
		p += childCount * (4 + 16 * 4);

		if (p + 4 > chunk.Length) return null;
		int vertexCount = ReadU32(chunk, p); p += 4;
		if (vertexCount <= 0 || vertexCount > 2_000_000) return null;

		var verts = new Vector3[vertexCount];
		for (int i = 0; i < vertexCount; i++)
		{
			if (p + 16 > chunk.Length) return null;

			int weightCount = ReadU32(chunk, p); p += 4;
			if (weightCount < 0 || weightCount > 1024) return null;

			float x = ReadFloat(chunk, p);
			float z = ReadFloat(chunk, p + 4);
			float y = ReadFloat(chunk, p + 8);
			p += 12;

			p += weightCount * 8;
			if (p > chunk.Length) return null;

			verts[i] = new Vector3(x, y, z);
		}

		return verts;
	}

	public static int ReadU32(byte[] b, int off)
		=> (int)(uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

	private static int ReadU16(byte[] b, int off)
		=> b[off] | (b[off + 1] << 8);

	private static float ReadFloat(byte[] b, int off)
		=> BitConverter.ToSingle(b, off);

	private static float IntBitsToFloat(int v)
		=> BitConverter.Int32BitsToSingle(v);

	private static string BuildNote(Vector3[]? verts, int[]? idx, int vertexCount, int vertexFlags, int vertexUnknown)
	{
		if (idx == null) return "MESHFACES identifier 0x10000 not found";
		if (verts == null) return vertexCount > 0
			? $"vertex payload present but positions are stored externally/body-mode (count={vertexCount}, flags=0x{vertexFlags:X}, aux=0x{vertexUnknown:X})"
			: "MESHVERTICES identifier 0x20000 not found or empty";
		return "vertices or faces empty";
	}
}
