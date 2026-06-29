using System.Numerics;

namespace SyberiaDatamine.CKFile.Objects;

public readonly record struct CkFace(int V0, int V1, int V2);

public sealed class CkMesh : CkObject
{

	public int VertexCount { get; init; }

	public int FaceCount { get; init; }

	public Vector3[]? Vertices { get; init; }

	public int[]? Indices { get; init; }

	public int[]? FaceMaterialIndices { get; init; }

	public int[] MaterialObjectIndices { get; init; } = [];

	public Vector3[]? Normals { get; init; }

	public (float U, float V)[]? UVs { get; init; }

	public bool ParseSucceeded { get; init; }

	public bool NeedsBodypartVertices { get; init; }

	public string ParseNote { get; init; } = string.Empty;

	public uint ReferencedMeshCkId { get; init; }

	public bool IsEntity { get; init; }

	public override string Summary
		=> ParseSucceeded && !NeedsBodypartVertices
			? $"Mesh  {VertexCount} verts  {FaceCount} tris" + (MaterialObjectIndices.Count(i => i >= 0) > 0 ? $"  {MaterialObjectIndices.Count(i => i >= 0)} material(s)" : "")
			: ParseSucceeded && NeedsBodypartVertices
				? $"Mesh  {FaceCount} tris  (bodypart vertices)" + (MaterialObjectIndices.Count(i => i >= 0) > 0 ? $"  {MaterialObjectIndices.Count(i => i >= 0)} material(s)" : "")
				: IsEntity && ReferencedMeshCkId != 0
					? $"3D Entity  (mesh ref → CkId 0x{ReferencedMeshCkId:X8})"
					: IsEntity
						? "3D Entity  (no mesh assigned)"
						: $"Mesh  (parse failed: {ParseNote})";
}
