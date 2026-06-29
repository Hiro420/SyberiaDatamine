using SyberiaDatamine.Core;
using System.Text;

namespace SyberiaDatamine.Parsers;

public sealed class CmoArchiveSource : IArchiveSource
{
	private readonly List<ArchiveEntry> _entries;

	public string ArchiveName { get; }
	public IReadOnlyList<ArchiveEntry> Entries => _entries;

	public CmoArchiveSource(
		NemoFileInfo info,
		string cmoName,
		Func<string, byte[]?> textureResolver,
		Func<NemoObject, byte[]?>? rawObjectLoader = null,
		Func<NemoObject, long>? objectSizeResolver = null)
	{
		ArchiveName = cmoName;
		_entries = BuildEntries(info, textureResolver, rawObjectLoader, objectSizeResolver);
	}

	private static List<ArchiveEntry> BuildEntries(
		NemoFileInfo info,
		Func<string, byte[]?> textureResolver,
		Func<NemoObject, byte[]?>? rawObjectLoader,
		Func<NemoObject, long>? objectSizeResolver)
	{
		var entries = new List<ArchiveEntry>(info.Objects.Count);

		foreach (var obj in info.Objects)
		{
			if (NemoClassRegistry.IsTexture(obj.ClassId))
			{

				var baseName = obj.Name;
				entries.Add(new ArchiveEntry
				{
					Name = string.IsNullOrEmpty(baseName) ? $"tex_0x{obj.CkId:X8}.tga" : baseName + ".tga",
					FileSize = 0,
					Kind = AssetKind.Texture2D,
					LoaderId = "tga",
					DataLoader = () =>
						textureResolver(baseName)
						?? textureResolver(baseName + ".tga")
						?? textureResolver(baseName + ".jpg"),
					RawDataLoader = () =>
						textureResolver(baseName)
						?? textureResolver(baseName + ".tga")
						?? textureResolver(baseName + ".jpg"),
				});
			}
			else
			{

				var capturedObj = obj;
				var displayName = string.IsNullOrEmpty(obj.Name)
					? $"{obj.ClassName}_0x{obj.CkId:X8}"
					: obj.Name;

				entries.Add(new ArchiveEntry
				{
					Name = displayName,
					FileSize = objectSizeResolver?.Invoke(capturedObj) ?? 0,
					Kind = AssetKind.CmoObject,
					LoaderId = "cmo_obj",
					NemoObj = capturedObj,
					DataLoader = () => BuildMetaBytes(capturedObj),
					RawDataLoader = () => rawObjectLoader?.Invoke(capturedObj),
				});
			}
		}

		return entries;
	}

	private static byte[] BuildMetaBytes(NemoObject obj)
	{
		var name = string.IsNullOrEmpty(obj.Name) ? "(unnamed)" : obj.Name;
		var sb = new StringBuilder();

		sb.AppendLine($"  {obj.GroupName.ToUpperInvariant()}  ──  {name}");
		sb.AppendLine();
		sb.AppendLine($"  Class      : {obj.ClassName}  (ClassID {obj.ClassId})");
		sb.AppendLine($"  Group      : {obj.GroupName}");
		sb.AppendLine($"  ObjectIdx  : {obj.ObjectIndex}");
		sb.AppendLine($"  CK_ID      : 0x{obj.CkId:X8}");
		sb.AppendLine($"  FileIndex  : {obj.FileIndex}");
		sb.AppendLine();

		switch (obj.ClassId)
		{
			case 32:
			case 53:
				sb.AppendLine($"  Mesh — exportable geometry (vertices, faces, normals, UVs)");
				sb.AppendLine($"  is embedded in the CMO data section.");
				break;
			case 33:
				sb.AppendLine($"  3D Entity — transform/link state, not a mesh container.");
				sb.AppendLine($"  If it references a mesh/bodypart/3D object, the viewer/exporter will resolve the visual target.");
				break;
			case 30:
				sb.AppendLine($"  Material — color, shininess and texture references");
				sb.AppendLine($"  are embedded in the CMO data section.");
				break;
			case 40:
				sb.AppendLine($"  Character — animated 3D character entity.");
				break;
			case 41:
				sb.AppendLine($"  3D Object — position, rotation, scale and mesh links");
				sb.AppendLine($"  stored in the CMO data section.");
				break;
			case 42:
				sb.AppendLine($"  Body part — skeletal/deformable vertex data.");
				sb.AppendLine($"  The viewer/exporter will pair it with its referenced mesh face list when possible.");
				break;
			case 43:
				sb.AppendLine($"  Curve — path/control-point data.");
				break;
			case 15:
			case 16:
			case 18:
				sb.AppendLine($"  Animation — keyframe or curve data in the CMO data section.");
				break;
			case 52:
				sb.AppendLine($"  DataArray — structured table/list (game data tables).");
				break;
			case 8:
				sb.AppendLine($"  Behavior graph — script/logic node.");
				sb.AppendLine($"  Export File writes a GraphViz .dot reference graph.");
				break;
		}

		return Encoding.UTF8.GetBytes(sb.ToString());
	}

	public void Dispose() { }
}
