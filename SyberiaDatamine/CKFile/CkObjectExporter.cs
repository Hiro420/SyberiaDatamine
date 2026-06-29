using SyberiaDatamine.CKFile.Objects;
using SyberiaDatamine.CKFile.Parsers;
using SyberiaDatamine.Core;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace SyberiaDatamine.CKFile;

public static class CkObjectExporter
{
	public static byte[] MeshToObjBytes(CkMesh mesh, Vector3[] vertices, string objectName)
		=> Encoding.UTF8.GetBytes(MeshToObj(mesh, vertices, objectName));

	public static byte[] MeshToObjBytes(
		CkMesh mesh,
		Vector3[] vertices,
		string objectName,
		IReadOnlyList<string>? materialNames,
		string? materialLibraryFile)
		=> Encoding.UTF8.GetBytes(MeshToObj(mesh, vertices, objectName, materialNames, materialLibraryFile));

	public static string MeshToObj(CkMesh mesh, Vector3[] vertices, string objectName)
		=> MeshToObj(mesh, vertices, objectName, null, null);

	public static string MeshToObj(
		CkMesh mesh,
		Vector3[] vertices,
		string objectName,
		IReadOnlyList<string>? materialNames,
		string? materialLibraryFile)
	{
		var sb = new StringBuilder();
		var inv = CultureInfo.InvariantCulture;
		var safeName = SanitizeObjName(objectName);

		sb.AppendLine($"# Exported by SyberiaDatamine");
		sb.AppendLine($"# ObjectIndex {mesh.ObjectIndex}, CK_ID 0x{mesh.CkId:X8}, ClassID {mesh.ClassId}, {mesh.VertexCount} vertices, {mesh.FaceCount} faces");
		if (!string.IsNullOrWhiteSpace(materialLibraryFile))
			sb.AppendLine($"mtllib {materialLibraryFile}");
		sb.AppendLine($"o {safeName}");

		foreach (var v in vertices)
			sb.AppendLine($"v {v.X.ToString("G9", inv)} {v.Y.ToString("G9", inv)} {v.Z.ToString("G9", inv)}");

		if (mesh.UVs != null && mesh.UVs.Length == vertices.Length)
			foreach (var uv in mesh.UVs)
				sb.AppendLine($"vt {uv.U.ToString("G9", inv)} {(1.0f - uv.V).ToString("G9", inv)}");

		if (mesh.Normals != null && mesh.Normals.Length == vertices.Length)
			foreach (var n in mesh.Normals)
				sb.AppendLine($"vn {n.X.ToString("G9", inv)} {n.Y.ToString("G9", inv)} {n.Z.ToString("G9", inv)}");

		var indices = mesh.Indices ?? [];
		bool hasUv = mesh.UVs != null && mesh.UVs.Length == vertices.Length;
		bool hasNormals = mesh.Normals != null && mesh.Normals.Length == vertices.Length;
		string? currentMaterial = null;

		for (int i = 0, faceIndex = 0; i + 2 < indices.Length; i += 3, faceIndex++)
		{
			int a = indices[i];
			int b = indices[i + 1];
			int c = indices[i + 2];
			if (a < 0 || b < 0 || c < 0 || a >= vertices.Length || b >= vertices.Length || c >= vertices.Length)
				continue;

			if (materialNames != null && mesh.FaceMaterialIndices != null && faceIndex < mesh.FaceMaterialIndices.Length)
			{
				int slot = mesh.FaceMaterialIndices[faceIndex];
				if (slot >= 0 && slot < materialNames.Count)
				{
					var nextMaterial = SanitizeObjName(materialNames[slot]);
					if (!string.Equals(currentMaterial, nextMaterial, StringComparison.Ordinal))
					{
						sb.AppendLine($"usemtl {nextMaterial}");
						currentMaterial = nextMaterial;
					}
				}
			}

			sb.Append("f ");
			AppendObjIndex(sb, a + 1, hasUv, hasNormals); sb.Append(' ');
			AppendObjIndex(sb, b + 1, hasUv, hasNormals); sb.Append(' ');
			AppendObjIndex(sb, c + 1, hasUv, hasNormals); sb.AppendLine();
		}

		return sb.ToString();
	}

	public static string MaterialsToMtl(IReadOnlyList<(string Name, string? TextureFileName)> materials)
	{
		var sb = new StringBuilder();
		sb.AppendLine("# Exported by SyberiaDatamine");
		foreach (var material in materials)
		{
			var name = SanitizeObjName(material.Name);
			sb.AppendLine();
			sb.AppendLine($"newmtl {name}");
			sb.AppendLine("Ka 1.000000 1.000000 1.000000");
			sb.AppendLine("Kd 1.000000 1.000000 1.000000");
			sb.AppendLine("Ks 0.200000 0.200000 0.200000");
			sb.AppendLine("Ns 35.000000");
			if (!string.IsNullOrWhiteSpace(material.TextureFileName))
				sb.AppendLine($"map_Kd {material.TextureFileName}");
		}
		return sb.ToString();
	}

	public static byte[] GenericObjectTextBytes(NemoObject header, byte[] rawData)
	{
		var generic = CkGenericStateChunkParser.Parse(rawData, header);
		return Encoding.UTF8.GetBytes(generic.ToDiagnosticText(rawData));
	}

	public static string SafeFileStem(string name, string fallback)
	{
		var stem = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(name) ? fallback : name);
		foreach (var ch in Path.GetInvalidFileNameChars())
			stem = stem.Replace(ch, '_');
		return string.IsNullOrWhiteSpace(stem) ? fallback : stem;
	}

	public static string SafeFileName(string name, string fallback)
	{
		var safe = string.IsNullOrWhiteSpace(name) ? fallback : name;
		foreach (var ch in Path.GetInvalidFileNameChars())
			safe = safe.Replace(ch, '_');
		return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
	}

	private static void AppendObjIndex(StringBuilder sb, int idx, bool hasUv, bool hasNormals)
	{
		if (hasUv && hasNormals) sb.Append($"{idx}/{idx}/{idx}");
		else if (hasUv) sb.Append($"{idx}/{idx}");
		else if (hasNormals) sb.Append($"{idx}//{idx}");
		else sb.Append(idx);
	}

	private static string SanitizeObjName(string name)
	{
		if (string.IsNullOrWhiteSpace(name)) return "ck_object";
		var sb = new StringBuilder(name.Length);
		foreach (char ch in name)
			sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
		return sb.ToString();
	}
}
