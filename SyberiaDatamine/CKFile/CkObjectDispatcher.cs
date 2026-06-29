using SyberiaDatamine.CKFile.Objects;
using SyberiaDatamine.CKFile.Parsers;
using SyberiaDatamine.CKFile.Parsing;
using SyberiaDatamine.Core;

namespace SyberiaDatamine.CKFile;

public static class CkObjectDispatcher
{
	private delegate string SummaryDelegate(CkBinaryReader r, NemoObject h);

	private static readonly Dictionary<int, SummaryDelegate> _summaryParsers;

	static CkObjectDispatcher()
	{
		_summaryParsers = new Dictionary<int, SummaryDelegate>();
		RegisterSummary(CkDataArrayParser.Instance, (r, h) =>
		{
			var res = CkDataArrayParser.Instance.Parse(r, h);
			return res.Success ? res.Value!.Summary : $"(parse error: {res.Error})";
		});
		RegisterSummary(CkMeshParser.Instance, (r, h) =>
		{
			var res = CkMeshParser.Instance.Parse(r, h);
			return res.Success ? res.Value!.Summary : $"(parse error: {res.Error})";
		});
		RegisterSummary(Ck3dEntityParser.Instance, (r, h) =>
		{
			var res = Ck3dEntityParser.Instance.Parse(r, h);
			return res.Success ? res.Value!.Summary : $"(parse error: {res.Error})";
		});
		RegisterSummary(CkMaterialParser.Instance, (r, h) =>
		{
			var res = CkMaterialParser.Instance.Parse(r, h);
			return res.Success ? res.Value!.Summary : $"(parse error: {res.Error})";
		});
		RegisterSummary(CkBehaviorGraphParser.Instance, (r, h) =>
		{
			var res = CkBehaviorGraphParser.Instance.Parse(r, h);
			return res.Success ? res.Value!.Summary : $"(parse error: {res.Error})";
		});
	}

	private static void RegisterSummary<T>(ICkObjectParser<T> parser, SummaryDelegate fn)
		where T : CkObject
	{
		foreach (var id in parser.SupportedClassIds)
			_summaryParsers[id] = fn;
	}

	public static string GetSummary(byte[] rawData, NemoObject header)
	{
		if (rawData.Length == 0)
			return "(no raw data available)";

		if (!_summaryParsers.TryGetValue(header.ClassId, out var fn))
		{
			var generic = CkGenericStateChunkParser.Parse(rawData, header);
			return generic.Summary;
		}

		using var reader = new CkBinaryReader(rawData);
		try { return fn(reader, header); }
		catch (Exception ex) { return $"(dispatcher error: {ex.Message})"; }
	}

	public static CkDataArray? ParseDataArray(byte[] rawData, NemoObject header)
	{
		if (header.ClassId != 52 || rawData.Length == 0)
			return null;

		using var reader = new CkBinaryReader(rawData);
		var result = CkDataArrayParser.Instance.Parse(reader, header);
		return result.Success ? result.Value : null;
	}

	public static CkMesh? ParseMesh(byte[] rawData, NemoObject header)
	{
		if (!CkMeshParser.Instance.SupportedClassIds.Contains(header.ClassId) || rawData.Length == 0)
			return null;

		using var reader = new CkBinaryReader(rawData);
		var result = CkMeshParser.Instance.Parse(reader, header);
		return result.Success ? result.Value : null;
	}

	public static System.Numerics.Vector3[]? ExtractBodypartVertices(byte[] rawData)
		=> rawData.Length > 0 ? CkMeshParser.ExtractBodypartVertices(rawData) : null;

	public static Ck3dEntity? Parse3dEntity(byte[] rawData, NemoObject header)
	{
		if (!Ck3dEntityParser.Instance.SupportedClassIds.Contains(header.ClassId) || rawData.Length == 0)
			return null;

		using var reader = new CkBinaryReader(rawData);
		var result = Ck3dEntityParser.Instance.Parse(reader, header);
		return result.Success ? result.Value : null;
	}

	public static CkMaterial? ParseMaterial(byte[] rawData, NemoObject header)
	{
		if (header.ClassId != 30 || rawData.Length == 0)
			return null;

		using var reader = new CkBinaryReader(rawData);
		var result = CkMaterialParser.Instance.Parse(reader, header);
		return result.Success ? result.Value : null;
	}

	public static CkBehaviorGraph? ParseBehaviorGraph(byte[] rawData, NemoObject header)
	{
		if (!CkBehaviorGraphParser.Instance.SupportedClassIds.Contains(header.ClassId) || rawData.Length == 0)
			return null;

		using var reader = new CkBinaryReader(rawData);
		var result = CkBehaviorGraphParser.Instance.Parse(reader, header);
		return result.Success ? result.Value : null;
	}

	public static CkGenericObject ParseGeneric(byte[] rawData, NemoObject header)
		=> CkGenericStateChunkParser.Parse(rawData, header);

	public static bool HasParser(int classId)
		=> _summaryParsers.ContainsKey(classId);
}
