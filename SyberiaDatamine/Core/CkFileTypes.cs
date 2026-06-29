namespace SyberiaDatamine.Core;

public sealed class NemoObject
{
	public int ObjectIndex { get; set; }
	public uint CkId { get; set; }
	public int ClassId { get; set; }
	public int FileIndex { get; set; }
	public string Name { get; set; } = "";

	public string ClassName => NemoClassRegistry.GetName(ClassId);
	public string GroupName => NemoClassRegistry.GetGroupName(ClassId);
}

public sealed class NemoFileInfo
{
	public uint CkVersion { get; set; }
	public int FileVersion { get; set; }
	public uint FileWriteMode { get; set; }
	public uint ProductVersion { get; set; }
	public uint ProductBuild { get; set; }
	public int ObjectCount { get; set; }
	public int ManagerCount { get; set; }
	public uint MaxIDSaved { get; set; }
	public int DataPackSize { get; set; }
	public int DataUnPackSize { get; set; }
	public int Hdr1PackSize { get; set; }
	public int Hdr1UnPackSize { get; set; }
	public string? ParseError { get; set; }

	public List<NemoObject> Objects { get; set; } = new();

	public string ProductBuildStr
	{
		get
		{
			var major = (ProductBuild >> 24) & 0xFF;
			var minor = (ProductBuild >> 16) & 0xFF;
			var patch = ProductBuild & 0xFFFF;
			return $"{major}.{minor}.{patch:D4}";
		}
	}

	public string CkVersionStr
	{
		get
		{
			var day = (CkVersion >> 24) & 0xFF;
			var month = (CkVersion >> 16) & 0xFF;
			var year = CkVersion & 0xFFFF;
			return $"{day:D2}/{month:D2}/{year}";
		}
	}

	public string WriteModeStr => FileWriteMode switch
	{
		0 => "Uncompressed",
		1 => "Chunk-Compressed",
		8 => "Whole-Compressed",
		_ => $"0x{FileWriteMode:X}",
	};
}

public static class NemoClassRegistry
{
	private static readonly Dictionary<int, string> _names = new()
	{
		[1] = "Object",
		[2] = "ParameterIn",
		[3] = "ParameterOut",
		[4] = "ParameterOperation",
		[5] = "State",
		[6] = "BehaviorLink",
		[8] = "Behavior",
		[9] = "BehaviorIO",
		[10] = "Scene",
		[11] = "SceneObject",
		[12] = "RenderContext",
		[13] = "KinematicChain",
		[15] = "ObjectAnimation",
		[16] = "Animation",
		[18] = "KeyedAnimation",
		[19] = "BeObject",
		[20] = "Synchro",
		[21] = "Level",
		[22] = "Place",
		[23] = "Group",
		[24] = "Sound",
		[25] = "WaveSound",
		[26] = "MidiSound",
		[27] = "2dEntity",
		[28] = "Sprite",
		[29] = "SpriteText",
		[30] = "Material",
		[31] = "Texture",
		[32] = "Mesh",
		[33] = "3dEntity",
		[34] = "Camera",
		[35] = "TargetCamera",
		[36] = "CurvePoint",
		[37] = "Sprite3D",
		[38] = "Light",
		[39] = "TargetLight",
		[40] = "Character",
		[41] = "3dObject",
		[42] = "BodyPart",
		[43] = "Curve",
		[45] = "ParameterLocal",
		[46] = "Parameter",
		[47] = "RenderObject",
		[48] = "InterfaceObjectManager",
		[49] = "CriticalSection",
		[50] = "Grid",
		[51] = "Layer",
		[52] = "DataArray",
		[53] = "PatchMesh",
	};

	public static string GetName(int classId) =>
		_names.TryGetValue(classId, out var n) ? n : $"CID_{classId}";

	public static string GetGroupName(int classId) => classId switch
	{
		33 or 36 or 37 => "3D Entities",
		40 or 41 or 42 => "3D Objects",
		43 => "Curves",
		32 or 53 => "Meshes",
		30 => "Materials",
		31 => "Textures",
		8 or 6 or 9 => "Behaviors",
		38 or 39 => "Lights",
		34 or 35 => "Cameras",
		21 => "Levels",
		10 => "Scenes",
		22 => "Places",
		23 => "Groups",
		24 or 25 or 26 => "Sounds",
		28 or 27 or 29 => "Sprites",
		15 or 16 or 18 => "Animations",
		52 => "DataArrays",
		_ => "Other",
	};

	public static bool IsTexture(int classId) => classId == 31;

	public static bool IsGeometry(int classId) => classId is 32 or 33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 41 or 42 or 43 or 47 or 53;
}
