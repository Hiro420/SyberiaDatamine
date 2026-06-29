using SyberiaDatamine.CKFile.Objects;
using SyberiaDatamine.CKFile.Parsing;
using SyberiaDatamine.Core;

namespace SyberiaDatamine.CKFile.Parsers;

public sealed class CkDataArrayParser : ICkObjectParser<CkDataArray>
{

	public static readonly CkDataArrayParser Instance = new();

	public IReadOnlyList<int> SupportedClassIds { get; } = [52];

	public CkParseResult<CkDataArray> Parse(CkBinaryReader reader, NemoObject header)
	{
		try { return DoParse(reader, header); }
		catch (Exception ex)
		{ return CkParseResult<CkDataArray>.Fail($"DataArray parse error: {ex.Message}"); }
	}

	private static CkParseResult<CkDataArray> DoParse(CkBinaryReader reader, NemoObject header)
	{
		int totalBytes = (int)reader.BytesRemaining;
		var raw = reader.ReadBytes(totalBytes);

		var chunk = CkStateChunkReader.TryParse(raw);
		if (chunk == null)
			return CkParseResult<CkDataArray>.Fail("Not a valid CKStateChunk");

		var columns = new List<DataArrayColumn>();

		if (!chunk.SeekIdentifier(CkStateChunkReader.CK_STATESAVE_DATAARRAYFORMAT))
			return CkParseResult<CkDataArray>.Fail("DATAARRAYFORMAT identifier not found");

		int colCount = chunk.ReadInt32();
		if (colCount < 0 || colCount > 4096)
			return CkParseResult<CkDataArray>.Fail($"Invalid column count: {colCount}");

		for (int c = 0; c < colCount && chunk.HasMore; c++)
		{

			string colName = chunk.ReadString();
			int typeCode = chunk.ReadInt32();

			var colType = typeCode switch
			{
				1 => DataArrayColumnType.Int,
				2 => DataArrayColumnType.Float,
				3 => DataArrayColumnType.String,
				4 => DataArrayColumnType.Object,
				5 => DataArrayColumnType.Parameter,
				_ => DataArrayColumnType.Unknown,
			};

			if (typeCode == 5)
				chunk.Skip(2);

			if (string.IsNullOrEmpty(colName))
				colName = $"col_{c}";

			columns.Add(new DataArrayColumn { Name = colName, Type = colType });
		}

		var rows = new List<object?[]>();

		if (chunk.SeekIdentifier(CkStateChunkReader.CK_STATESAVE_DATAARRAYDATA))
		{
			int rowCount = chunk.ReadInt32();
			if (rowCount < 0 || rowCount > 1_000_000)
				rowCount = 0;

			const int maxRows = 50_000;
			int safeRows = Math.Min(rowCount, maxRows);

			for (int r = 0; r < safeRows && chunk.HasMore; r++)
			{
				var row = new object?[columns.Count];
				for (int c = 0; c < columns.Count && chunk.HasMore; c++)
				{
					row[c] = columns[c].Type switch
					{
						DataArrayColumnType.Float => (object?)chunk.ReadFloat(),
						DataArrayColumnType.String => chunk.ReadString(),
						DataArrayColumnType.Int => chunk.ReadInt32(),
						DataArrayColumnType.Object => (uint)chunk.ReadInt32(),
						DataArrayColumnType.Parameter => SkipSubChunkCell(chunk),
						_ => chunk.ReadInt32(),
					};
				}
				rows.Add(row);
			}
		}

		return CkParseResult<CkDataArray>.Ok(new CkDataArray
		{
			ObjectIndex = header.ObjectIndex,
			CkId = header.CkId,
			ClassId = header.ClassId,
			Name = header.Name,
			Columns = columns,
			Rows = rows,
			BytesConsumed = totalBytes,
		}, totalBytes);
	}

	private static object? SkipSubChunkCell(CkStateChunkReader chunk)
	{
		if (!chunk.HasMore) return null;
		int size = chunk.ReadInt32();
		chunk.Skip(size);
		return null;
	}
}
