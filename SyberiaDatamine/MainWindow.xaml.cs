using NAudio.Wave;
using SyberiaDatamine.CKFile;
using SyberiaDatamine.CKFile.Objects;
using SyberiaDatamine.CKFile.Parsers;
using SyberiaDatamine.Core;
using SyberiaDatamine.Loaders;
using SyberiaDatamine.Parsers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace SyberiaDatamine;

public partial class MainWindow : Window
{
	private readonly BulkCollection<FileTreeItem> _fileItems;
	private readonly BulkCollection<AssetItem> _assetItems;
	private readonly List<AssetItem> _allAssets;

	private readonly List<IArchiveSource> _archiveSources = new();
	private readonly Dictionary<string, ArchiveEntry> _entryMap = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, ArchiveEntry> _externalTextureCache = new(StringComparer.OrdinalIgnoreCase);
	private bool _hideUnknownFiles = false;

	private Point3D _meshCameraTarget = new(0, 0, 0);
	private double _meshCameraRadius = 5.0;
	private double _meshCameraYaw = 0.0;
	private double _meshCameraPitch = 0.3;
	private System.Windows.Point _meshDragStart;
	private bool _meshDragging;
	private Model3D? _currentMeshModel;

	private int _workspaceGeneration;

	private static readonly SemaphoreSlim _cmoLoadSem = new(3, 3);

	private CancellationTokenSource? _previewCts;

	private readonly DispatcherTimer _mediaTimer;
	private bool _isScrubbing;
	private CancellationTokenSource? _mediaCts;
	private Task? _currentMediaSetupTask;
	private AssetItem? _currentPreviewAsset;
	private long _videoSeqCounter;
	private long _activeVideoSeq;
	private readonly Dictionary<string, string> _mediaTempCache = new();
	private readonly object _mediaTempLock = new();
	private bool _isClosingCleanup;

	private IWavePlayer? _audioOutput;
	private WaveStream? _audioStream;
	private bool _isAudioActive;

	private readonly StaExecutor _sta = new("NAudio STA");
	private readonly SemaphoreSlim _videoGate = new(1, 1);
	private TimeSpan _videoDuration = TimeSpan.Zero;

	private const double VideoPreviewFallbackFrameRate = 30.0;
	private readonly Dictionary<string, VideoFrameCache> _videoFrameCache = new(StringComparer.OrdinalIgnoreCase);
	private VideoFrameCache? _currentVideoFrames;
	private readonly Stopwatch _videoFrameClock = new();
	private TimeSpan _videoFrameBasePosition = TimeSpan.Zero;
	private int _currentVideoFrameIndex = -1;
	private bool _isVideoFramePlaying;

	private bool _ffmpegBannerWired;
	private readonly Queue<string> _ffmpegLogTail = new();

	private sealed class VideoFrameCache
	{
		public required string DirectoryPath { get; init; }
		public required List<string> FramePaths { get; init; }
		public double FrameRate { get; init; } = VideoPreviewFallbackFrameRate;
		public TimeSpan Duration => FrameRate > 0 && FramePaths.Count > 0
			? TimeSpan.FromSeconds(FramePaths.Count / FrameRate)
			: TimeSpan.Zero;
	}

	private sealed class ExportPayload
	{
		public required byte[] Bytes { get; init; }
		public required string FileName { get; init; }
		public string Filter { get; init; } = "All files (*.*)|*.*";
		public string KindLabel { get; init; } = "asset";
		public Dictionary<string, byte[]> RelatedFiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
	}

	private sealed class ResolvedRenderable
	{
		public required CkMesh Mesh { get; init; }
		public required System.Numerics.Vector3[] Vertices { get; init; }
		public ImageSource? TextureImage { get; init; }
		public IReadOnlyList<ResolvedMaterialInfo> Materials { get; init; } = Array.Empty<ResolvedMaterialInfo>();
		public string SourceLabel { get; init; } = "";
		public bool HasEntityTransform { get; init; }
	}

	private sealed class ResolvedMaterialInfo
	{
		public required string Name { get; init; }
		public int ObjectIndex { get; init; }
		public string? TextureFileName { get; init; }
		public byte[]? TextureBytes { get; init; }
		public ImageSource? TextureImage { get; init; }
	}

	public MainWindow()
	{
		InitializeComponent();
		Loaded += MainWindow_Loaded;
		_fileItems = new BulkCollection<FileTreeItem>();
		_assetItems = new BulkCollection<AssetItem>();
		_allAssets = new List<AssetItem>();
		AssetLoaderRegistry.Initialize();

		_mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
		_mediaTimer.Tick += MediaTimer_Tick;

		SetupTreeView();
		AssetGrid.ItemsSource = _assetItems;

		TypeFilterCombo.Items.Add("All");
		TypeFilterCombo.Items.Add("Texture");
		TypeFilterCombo.Items.Add("Video");
		TypeFilterCombo.Items.Add("Audio");
		TypeFilterCombo.Items.Add("NMO/CMO");
		TypeFilterCombo.Items.Add("3D / CMO");
		TypeFilterCombo.Items.Add("Mesh");
		TypeFilterCombo.Items.Add("Material");
		TypeFilterCombo.Items.Add("Behavior");
		TypeFilterCombo.Items.Add("Sound");
		TypeFilterCombo.Items.Add("Animation");
		TypeFilterCombo.Items.Add("DataArray");
		TypeFilterCombo.Items.Add("BodyPart");
		TypeFilterCombo.Items.Add("3D Object");
		TypeFilterCombo.Items.Add("Archive");
		TypeFilterCombo.Items.Add("Binary");
		TypeFilterCombo.Items.Add("Unknown");
		TypeFilterCombo.SelectedIndex = 0;

		Closing += MainWindow_Closing;
	}

	private async void MainWindow_Closing(object? sender, CancelEventArgs e)
	{
		if (_isClosingCleanup)
			return;

		e.Cancel = true;
		_isClosingCleanup = true;

		try
		{
			await ShutdownMediaAsync();
		}
		catch (Exception ex)
		{
			CrashLog.Write("Media shutdown failed", ex);
		}
		finally
		{
			try { _sta.Dispose(); } catch { }
			Close();
		}
	}

	private void MainWindow_Loaded(object sender, RoutedEventArgs e)
	{
		WireFfmpegBanner();
	}

	private void SetupTreeView()
	{
		var template = new HierarchicalDataTemplate();
		var binding = new System.Windows.Data.Binding("Children");
		template.ItemsSource = binding;

		var textBlock = new FrameworkElementFactory(typeof(TextBlock));
		textBlock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
		template.VisualTree = textBlock;

		FileTreeView.ItemTemplate = template;
		FileTreeView.ItemsSource = _fileItems;
	}

	private void MenuItem_LoadFile(object sender, RoutedEventArgs e)
	{
		var dialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "Load File",
			Filter = "Game Files (*.syj;*.vxbg;*.*)|*.syj;*.vxbg;*.*|All Files (*.*)|*.*"
		};

		if (dialog.ShowDialog() == true)
		{
			LoadSingleFile(dialog.FileName);
		}
	}

	private void MenuItem_LoadFolder(object sender, RoutedEventArgs e)
	{
		var dialog = new OpenFolderDialog
		{
			Title = "Select a folder containing game files"
		};

		if (dialog.ShowDialog(this) && !string.IsNullOrEmpty(dialog.Folder))
		{
			LoadFolder(dialog.Folder);
		}
	}

	private void MenuItem_IndexTextureSources(object sender, RoutedEventArgs e)
	{
		var warning = "This operation recursively scans a folder for archives and indexes texture references.\n\n" +
			"It may be slow and expensive on large folders. Continue?";
		var choice = MessageBox.Show(
			warning,
			"Index Texture Sources",
			MessageBoxButton.YesNo,
			MessageBoxImage.Warning);

		if (choice != MessageBoxResult.Yes)
			return;

		var dialog = new OpenFolderDialog
		{
			Title = "Select a folder to scan for archives containing textures"
		};

		if (!dialog.ShowDialog(this) || string.IsNullOrEmpty(dialog.Folder))
			return;

		IndexExternalTextureSources(dialog.Folder);
	}

	private void MenuItem_Exit(object sender, RoutedEventArgs e)
	{
		Application.Current.Shutdown();
	}

	private void MenuItem_ExpandAll(object sender, RoutedEventArgs e)
	{
		ExpandAllTreeItems(FileTreeView.Items);
	}

	private void MenuItem_CollapseAll(object sender, RoutedEventArgs e)
	{
		CollapseAllTreeItems(FileTreeView.Items);
	}

	private void TypeFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		UpdateAssetListFilter();
	}

	private void MenuItem_ResetLayout(object sender, RoutedEventArgs e)
	{
		_currentPreviewAsset = null;
		_fileItems.Clear();
		_assetItems.Clear();
		_allAssets.Clear();
		foreach (var src in _archiveSources) src.Dispose();
		_archiveSources.Clear();
		_entryMap.Clear();
		_externalTextureCache.Clear();
		ImagePreview.Source = null;
		ImagePreview.Visibility = Visibility.Collapsed;
		HexPreviewText.Text = "";
		HexPreviewText.Visibility = Visibility.Collapsed;
		NemoPanel.Visibility = Visibility.Collapsed;
		NemoObjectTree.ItemsSource = null;
		NemoHeaderText.Text = "";
		PropertiesText.Text = "";
		DumpHexText.Text = "";
		PreviewMetadata.Text = "";
		SearchBox.Text = "";

		StopAndHideMedia();
		UpdateStatus("Layout reset");
	}

	private void AssetGrid_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{

		var row = FindVisualParent<DataGridRow>((DependencyObject)e.OriginalSource);
		if (row == null)
			return;

		if (!row.IsSelected)
		{
			AssetGrid.SelectedItems.Clear();
			row.IsSelected = true;
			AssetGrid.SelectedItem = row.Item;
		}

		row.Focus();
	}

	private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
	{
		while (child != null)
		{
			if (child is T parent)
				return parent;

			child = VisualTreeHelper.GetParent(child);
		}

		return null;
	}

	private void TypeFilterComboBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is not ComboBox comboBox)
			return;

		if (!comboBox.IsDropDownOpen)
		{
			comboBox.Focus();
			comboBox.IsDropDownOpen = true;
			e.Handled = true;
		}
	}

	private void MenuItem_ExportAll(object sender, RoutedEventArgs e)
	{
		ExportAssets(_allAssets);
	}

	private void MenuItem_ExportSelected(object sender, RoutedEventArgs e)
	{
		var selected = AssetGrid.SelectedItems.Cast<AssetItem>().ToList();
		ExportAssets(selected);
	}

	private void Context_ExportFile(object sender, RoutedEventArgs e)
	{
		if (AssetGrid.SelectedItem is not AssetItem asset)
			return;
		ExportSingleAsset(asset);
	}

	private void DumpExport_Click(object sender, RoutedEventArgs e)
	{
		var asset = AssetGrid.SelectedItem as AssetItem ?? _currentPreviewAsset;
		if (asset == null)
			return;
		ExportSingleAsset(asset);
	}

	private void DumpExportRaw_Click(object sender, RoutedEventArgs e)
	{
		var asset = AssetGrid.SelectedItem as AssetItem ?? _currentPreviewAsset;
		if (asset == null)
			return;

		var bytes = GetAssetRawBytes(asset);
		if (bytes == null || bytes.Length == 0)
		{
			UpdateStatus("Raw bytes are not available for this entry");
			return;
		}

		var ext = Path.GetExtension(asset.Name);
		var fallbackName = asset.Kind == AssetKind.CmoObject
			? Path.GetFileNameWithoutExtension(asset.Name) + ".raw"
			: asset.Name;

		var sfd = new Microsoft.Win32.SaveFileDialog
		{
			Title = "Export Raw Bytes",
			FileName = string.IsNullOrWhiteSpace(ext) ? fallbackName : asset.Name,
			Filter = "Raw binary (*.raw)|*.raw|All files (*.*)|*.*"
		};

		if (sfd.ShowDialog(this) != true)
			return;

		File.WriteAllBytes(sfd.FileName, bytes);
		UpdateStatus($"Raw export: {Path.GetFileName(sfd.FileName)} ({bytes.Length:N0} bytes)");
	}

	private void ExportAssets(IReadOnlyList<AssetItem> assets)
	{
		if (assets.Count == 0)
		{
			UpdateStatus("No assets to export");
			return;
		}

		var dialog = new OpenFolderDialog { Title = "Select export folder" };
		if (!dialog.ShowDialog(this) || string.IsNullOrEmpty(dialog.Folder))
			return;

		var outRoot = dialog.Folder;
		int exported = 0;

		foreach (var asset in assets)
		{
			try
			{
				var payload = BuildExportPayload(asset);
				if (payload == null || payload.Bytes.Length == 0)
					continue;

				var safeContainer = string.IsNullOrWhiteSpace(asset.Container) ? "" : SanitizePathSegment(asset.Container);
				var outDir = string.IsNullOrWhiteSpace(safeContainer) ? outRoot : Path.Combine(outRoot, safeContainer);
				Directory.CreateDirectory(outDir);
				var outPath = Path.Combine(outDir, payload.FileName);
				File.WriteAllBytes(outPath, payload.Bytes);
				WriteRelatedExportFiles(outDir, payload);
				exported++;
			}
			catch
			{

			}
		}

		UpdateStatus($"Exported {exported} asset(s)");
	}

	private void ExportSingleAsset(AssetItem asset)
	{
		var payload = BuildExportPayload(asset);
		if (payload == null || payload.Bytes.Length == 0)
		{
			UpdateStatus("Nothing to export");
			return;
		}

		var sfd = new Microsoft.Win32.SaveFileDialog
		{
			Title = $"Export {payload.KindLabel}",
			FileName = payload.FileName,
			Filter = payload.Filter
		};

		if (sfd.ShowDialog(this) != true)
			return;

		File.WriteAllBytes(sfd.FileName, payload.Bytes);
		WriteRelatedExportFiles(Path.GetDirectoryName(sfd.FileName) ?? Environment.CurrentDirectory, payload);
		var sidecarCount = payload.RelatedFiles.Count;
		UpdateStatus(sidecarCount > 0
			? $"Exported: {Path.GetFileName(sfd.FileName)} + {sidecarCount} sidecar file(s)"
			: $"Exported: {Path.GetFileName(sfd.FileName)}");
	}

	private static void WriteRelatedExportFiles(string directory, ExportPayload payload)
	{
		if (payload.RelatedFiles.Count == 0)
			return;

		Directory.CreateDirectory(directory);
		foreach (var kvp in payload.RelatedFiles)
		{
			var safeName = CkObjectExporter.SafeFileName(kvp.Key, "sidecar.bin");
			File.WriteAllBytes(Path.Combine(directory, safeName), kvp.Value);
		}
	}

	private ExportPayload? BuildExportPayload(AssetItem asset)
	{

		if (asset.Kind == AssetKind.CmoObject && asset.CmoObjectRef != null)
		{
			var obj = asset.CmoObjectRef;
			var raw = GetAssetRawBytes(asset) ?? Array.Empty<byte>();
			var stem = CkObjectExporter.SafeFileStem(asset.Name, $"ck_{obj.CkId:X8}");

			if (raw.Length > 0)
			{
				var renderable = ResolveRenderableForAsset(asset, raw);
				if (renderable != null)
					return BuildObjPayloadFromMesh(renderable.Mesh, renderable.Vertices, asset.Container, stem);
			}

			if (raw.Length > 0 && obj.ClassId == 52)
			{
				var dataArray = CkObjectDispatcher.ParseDataArray(raw, obj);
				if (dataArray != null)
				{
					return new ExportPayload
					{
						Bytes = Encoding.UTF8.GetBytes(dataArray.ToTabSeparatedText(int.MaxValue)),
						FileName = stem + ".tsv",
						Filter = "Tab-separated values (*.tsv)|*.tsv|All files (*.*)|*.*",
						KindLabel = "DataArray TSV"
					};
				}
			}

			if (raw.Length > 0 && CkBehaviorGraphParser.Instance.SupportedClassIds.Contains(obj.ClassId))
			{
				var graph = CkObjectDispatcher.ParseBehaviorGraph(raw, obj);
				if (graph != null)
				{
					return new ExportPayload
					{
						Bytes = Encoding.UTF8.GetBytes(BuildBehaviorDot(graph, asset.Container)),
						FileName = stem + ".dot",
						Filter = "GraphViz DOT (*.dot)|*.dot|All files (*.*)|*.*",
						KindLabel = "behavior graph"
					};
				}
			}

			if (raw.Length > 0 && obj.ClassId == 30)
			{
				var material = CkObjectDispatcher.ParseMaterial(raw, obj);
				if (material != null)
				{
					return new ExportPayload
					{
						Bytes = Encoding.UTF8.GetBytes(material.ToDiagnosticText()),
						FileName = stem + ".material.txt",
						Filter = "Material diagnostic (*.txt)|*.txt|All files (*.*)|*.*",
						KindLabel = "material diagnostic"
					};
				}
			}

			if (raw.Length > 0 && Ck3dEntityParser.Instance.SupportedClassIds.Contains(obj.ClassId))
			{
				var entity = CkObjectDispatcher.Parse3dEntity(raw, obj);
				if (entity != null)
				{
					return new ExportPayload
					{
						Bytes = Encoding.UTF8.GetBytes(entity.ToDiagnosticText()),
						FileName = stem + ".ckobj.txt",
						Filter = "CK object diagnostic (*.txt)|*.txt|All files (*.*)|*.*",
						KindLabel = "CK object diagnostic"
					};
				}
			}

			if (raw.Length > 0)
			{
				return new ExportPayload
				{
					Bytes = CkObjectExporter.GenericObjectTextBytes(obj, raw),
					FileName = stem + ".ckobj.txt",
					Filter = "CK object diagnostic (*.txt)|*.txt|All files (*.*)|*.*",
					KindLabel = "CK object diagnostic"
				};
			}

			if (_entryMap.TryGetValue(asset.FullPath, out var metaEntry))
			{
				var metaBytes = metaEntry.LoadData();
				if (metaBytes != null)
				{
					return new ExportPayload
					{
						Bytes = metaBytes,
						FileName = stem + ".ckobj.txt",
						Filter = "CK object diagnostic (*.txt)|*.txt|All files (*.*)|*.*",
						KindLabel = "CK object diagnostic"
					};
				}
			}
		}

		var bytes = GetAssetBytes(asset);
		if (bytes == null)
			return null;

		return new ExportPayload
		{
			Bytes = bytes,
			FileName = SanitizePathSegment(Path.GetFileName(asset.Name)),
			Filter = "All files (*.*)|*.*",
			KindLabel = "asset"
		};
	}

	private ExportPayload? BuildObjPayloadFromEntry(ArchiveEntry entry, string container, string exportStem)
	{
		if (entry.NemoObj == null)
			return null;

		var renderable = ResolveRenderableFromEntry(entry, container, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
		if (renderable == null)
			return null;

		var stem = CkObjectExporter.SafeFileStem(exportStem, $"ck_{entry.NemoObj.CkId:X8}");
		return BuildObjPayloadFromMesh(renderable.Mesh, renderable.Vertices, container, stem);
	}

	private ExportPayload? BuildObjPayloadFromMesh(CkMesh mesh, System.Numerics.Vector3[] vertices, string container, string exportStem)
	{
		if (mesh.Indices == null || mesh.Indices.Length < 3)
			return null;

		var stem = CkObjectExporter.SafeFileStem(exportStem, $"ck_{mesh.CkId:X8}");
		var materials = ResolveMaterialInfos(container, mesh);
		var materialNames = materials.Count > 0
			? materials.Select(m => m.Name).ToArray()
			: null;
		var mtlName = materials.Count > 0 ? stem + ".mtl" : null;

		var payload = new ExportPayload
		{
			Bytes = CkObjectExporter.MeshToObjBytes(mesh, vertices, stem, materialNames, mtlName),
			FileName = stem + ".obj",
			Filter = "Wavefront OBJ (*.obj)|*.obj|All files (*.*)|*.*",
			KindLabel = "OBJ mesh"
		};

		if (materials.Count > 0 && mtlName != null)
		{
			payload.RelatedFiles[mtlName] = Encoding.UTF8.GetBytes(
				CkObjectExporter.MaterialsToMtl(materials.Select(m => (m.Name, m.TextureFileName)).ToArray()));

			foreach (var material in materials)
			{
				if (!string.IsNullOrWhiteSpace(material.TextureFileName) && material.TextureBytes is { Length: > 0 })
					payload.RelatedFiles[material.TextureFileName!] = material.TextureBytes;
			}
		}

		return payload;
	}

	private System.Numerics.Vector3[]? ResolveMeshVertices(string container, CkMesh mesh)
	{
		if (mesh.Vertices != null)
			return mesh.Vertices;

		if (!mesh.NeedsBodypartVertices || mesh.Indices == null || mesh.Indices.Length == 0)
			return null;

		int maxVertIdx = mesh.Indices.Max();
		int expectedCount = mesh.VertexCount;
		System.Numerics.Vector3[]? fallback = null;

		var bpEntries = _archiveSources
			.Where(s => string.Equals(s.ArchiveName, container, StringComparison.OrdinalIgnoreCase))
			.SelectMany(s => s.Entries)
			.Where(e => e.NemoObj?.ClassId == 42)
			.ToList();

		foreach (var bpEntry in bpEntries)
		{
			var bpBytes = bpEntry.LoadRawData();
			if (bpBytes == null || bpBytes.Length == 0) continue;

			var vertices = CkObjectDispatcher.ExtractBodypartVertices(bpBytes);
			if (vertices == null) continue;

			if (expectedCount > 0 && vertices.Length == expectedCount)
				return vertices;

			if (vertices.Length > maxVertIdx && fallback == null)
				fallback = vertices;
		}

		return fallback;
	}

	private static ResolvedRenderable ApplyEntityTransform(ResolvedRenderable renderable, Ck3dEntity entity, string entityName)
	{
		if (!entity.HasTransform || entity.Transform3x4.Length < 12)
			return renderable;

		var m = entity.Transform3x4;
		var transformed = new System.Numerics.Vector3[renderable.Vertices.Length];
		for (int i = 0; i < transformed.Length; i++)
		{
			var v = renderable.Vertices[i];
			transformed[i] = new System.Numerics.Vector3(
				(v.X * m[0]) + (v.Y * m[3]) + (v.Z * m[6]) + m[9],
				(v.X * m[1]) + (v.Y * m[4]) + (v.Z * m[7]) + m[10],
				(v.X * m[2]) + (v.Y * m[5]) + (v.Z * m[8]) + m[11]);
		}

		var label = string.IsNullOrWhiteSpace(entityName)
			? renderable.SourceLabel
			: $"{entityName} → {renderable.SourceLabel}";

		return new ResolvedRenderable
		{
			Mesh = renderable.Mesh,
			Vertices = transformed,
			TextureImage = renderable.TextureImage,
			Materials = renderable.Materials,
			SourceLabel = label,
			HasEntityTransform = true
		};
	}

	private ResolvedRenderable? ResolveRenderableForAsset(AssetItem asset, byte[] rawData)
	{
		if (asset.CmoObjectRef == null)
			return null;

		if (_entryMap.TryGetValue(asset.FullPath, out var entry))
			return ResolveRenderableFromEntry(entry, asset.Container, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		var obj = asset.CmoObjectRef;
		if (obj.ClassId is 32 or 53)
		{
			var mesh = CkObjectDispatcher.ParseMesh(rawData, obj);
			var vertices = mesh != null ? ResolveMeshVertices(asset.Container, mesh) : null;
			if (mesh != null && vertices != null && mesh.Indices != null && mesh.Indices.Length >= 3)
				return new ResolvedRenderable { Mesh = mesh, Vertices = vertices, TextureImage = ResolveTextureImageForMesh(asset.Container, mesh), SourceLabel = asset.Name };
		}

		return null;
	}

	private ResolvedRenderable? ResolveRenderableFromEntry(
		ArchiveEntry entry,
		string container,
		HashSet<string> visited,
		System.Numerics.Vector3[]? forcedBodypartVertices = null)
	{
		if (entry.NemoObj == null)
			return null;

		var key = $"{container}|{entry.NemoObj.ObjectIndex}|{entry.NemoObj.CkId}";
		if (!visited.Add(key))
			return null;

		var raw = entry.LoadRawData();
		if (raw == null || raw.Length == 0)
			return null;

		var obj = entry.NemoObj;

		if (obj.ClassId is 32 or 53)
		{
			var mesh = CkObjectDispatcher.ParseMesh(raw, obj);
			if (mesh == null || mesh.Indices == null || mesh.Indices.Length < 3)
				return null;

			var vertices = forcedBodypartVertices ?? ResolveMeshVertices(container, mesh);
			if (vertices == null)
				return null;

			var materials = ResolveMaterialInfos(container, mesh);
			return new ResolvedRenderable
			{
				Mesh = mesh,
				Vertices = vertices,
				TextureImage = materials.FirstOrDefault(m => m.TextureImage != null)?.TextureImage,
				Materials = materials,
				SourceLabel = entry.Name
			};
		}

		if (obj.ClassId == 42)
		{

			var bodypartVertices = CkObjectDispatcher.ExtractBodypartVertices(raw);
			var entity = CkObjectDispatcher.Parse3dEntity(raw, obj);
			if (entity != null)
			{
				foreach (var meshEntry in FindReferencedEntries(container, entity, 32, 53))
				{
					var resolved = ResolveRenderableFromEntry(meshEntry.Entry, meshEntry.Container, visited, bodypartVertices);
					if (resolved != null)
						return resolved;
				}
			}

			if (bodypartVertices != null)
			{

				foreach (var meshEntry in EntriesInContainer(container).Where(e => e.NemoObj?.ClassId is 32 or 53))
				{
					var meshRaw = meshEntry.LoadRawData();
					if (meshRaw == null || meshRaw.Length == 0 || meshEntry.NemoObj == null) continue;
					var mesh = CkObjectDispatcher.ParseMesh(meshRaw, meshEntry.NemoObj);
					if (mesh == null || !mesh.NeedsBodypartVertices || mesh.Indices == null || mesh.Indices.Length < 3) continue;
					if (mesh.VertexCount > 0 && mesh.VertexCount != bodypartVertices.Length) continue;
					if (mesh.Indices.Max() >= bodypartVertices.Length) continue;

					var materials = ResolveMaterialInfos(container, mesh);
					return new ResolvedRenderable
					{
						Mesh = mesh,
						Vertices = bodypartVertices,
						TextureImage = materials.FirstOrDefault(m => m.TextureImage != null)?.TextureImage,
						Materials = materials,
						SourceLabel = $"{entry.Name} → {meshEntry.Name}"
					};
				}
			}
		}

		if (Ck3dEntityParser.Instance.SupportedClassIds.Contains(obj.ClassId))
		{
			var entity = CkObjectDispatcher.Parse3dEntity(raw, obj);
			if (entity == null)
				return null;

			if (obj.ClassId == 40 && !string.IsNullOrWhiteSpace(obj.Name))
			{
				var sameNameBodypart = EntriesInContainer(container)
					.FirstOrDefault(e => e.NemoObj?.ClassId == 42 && string.Equals(e.NemoObj.Name, obj.Name, StringComparison.OrdinalIgnoreCase));
				if (sameNameBodypart != null)
				{
					var resolved = ResolveRenderableFromEntry(sameNameBodypart, container, visited);
					if (resolved != null)
						return ApplyEntityTransform(resolved, entity, obj.Name);
				}

				var exactNameMesh = FindLikelyMeshByName(container, obj.Name, allowContains: false);
				if (exactNameMesh != null)
				{
					var resolved = ResolveRenderableFromEntry(exactNameMesh, container, visited);
					if (resolved != null)
						return ApplyEntityTransform(resolved, entity, obj.Name);
				}

				foreach (var target in FindReferencedEntries(container, entity, 42))
				{
					var resolved = ResolveRenderableFromEntry(target.Entry, target.Container, visited);
					if (resolved != null)
						return ApplyEntityTransform(resolved, entity, obj.Name);
				}
			}

			foreach (var target in FindReferencedEntries(container, entity, 32, 53, 42, 41, 33, 47))
			{
				var resolved = ResolveRenderableFromEntry(target.Entry, target.Container, visited);
				if (resolved != null)
					return ApplyEntityTransform(resolved, entity, obj.Name);
			}

			var nameMesh = FindLikelyMeshByName(container, obj.Name, allowContains: obj.ClassId != 40);
			if (nameMesh != null)
			{
				var resolved = ResolveRenderableFromEntry(nameMesh, container, visited);
				if (resolved != null)
					return ApplyEntityTransform(resolved, entity, obj.Name);
			}
		}

		return null;
	}

	private IEnumerable<ArchiveEntry> EntriesInContainer(string container)
		=> _archiveSources
			.Where(s => string.Equals(s.ArchiveName, container, StringComparison.OrdinalIgnoreCase))
			.SelectMany(s => s.Entries);

	private IEnumerable<(ArchiveEntry Entry, string Container)> FindReferencedEntries(string preferredContainer, Ck3dEntity entity, params int[] classIds)
	{
		var classSet = classIds.ToHashSet();
		var allEntries = _archiveSources
			.SelectMany(src => src.Entries.Select(e => (Entry: e, Container: src.ArchiveName)))
			.Where(x => x.Entry.NemoObj != null && (classSet.Count == 0 || classSet.Contains(x.Entry.NemoObj!.ClassId)))
			.ToList();

		var orderedEntries = allEntries
			.OrderBy(x => string.Equals(x.Container, preferredContainer, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
			.ToList();

		var objectIndices = entity.MeshCandidateObjectIndices
			.Concat(entity.ReferencedObjectIndices)
			.Where(i => i >= 0)
			.Distinct()
			.ToList();

		foreach (var idx in objectIndices)
			foreach (var hit in orderedEntries.Where(x => x.Entry.NemoObj!.ObjectIndex == idx))
				yield return hit;

		var ckIds = entity.MeshCandidateIds.Concat(entity.ReferencedIds).Distinct().ToList();
		foreach (var id in ckIds)
			foreach (var hit in orderedEntries.Where(x => x.Entry.NemoObj!.CkId == id))
				yield return hit;
	}

	private ArchiveEntry? FindLikelyMeshByName(string container, string? objectName, bool allowContains = true)
	{
		if (string.IsNullOrWhiteSpace(objectName))
			return null;

		var normalized = NormalizeNameForMatch(objectName);
		if (string.IsNullOrWhiteSpace(normalized))
			return null;

		var entries = EntriesInContainer(container)
			.Where(e => e.NemoObj?.ClassId is 32 or 53 or 42)
			.ToList();

		var exact = entries.FirstOrDefault(e => NormalizeNameForMatch(e.NemoObj!.Name) == normalized + "mesh")
			?? entries.FirstOrDefault(e => NormalizeNameForMatch(e.NemoObj!.Name) == normalized);

		if (exact != null || !allowContains)
			return exact;

		return entries.FirstOrDefault(e => NormalizeNameForMatch(e.NemoObj!.Name).Contains(normalized));
	}

	private static string NormalizeNameForMatch(string name)
	{
		var sb = new StringBuilder(name.Length);
		foreach (var ch in name)
			if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
		return sb.ToString().Replace("mesh", "", StringComparison.OrdinalIgnoreCase);
	}

	private List<ResolvedMaterialInfo> ResolveMaterialInfos(string container, CkMesh mesh)
	{
		var results = new List<ResolvedMaterialInfo>();
		if (mesh.MaterialObjectIndices.Length == 0)
			return results;

		for (int slot = 0; slot < mesh.MaterialObjectIndices.Length; slot++)
		{
			var matObjectIndex = mesh.MaterialObjectIndices[slot];
			if (matObjectIndex < 0)
			{
				results.Add(new ResolvedMaterialInfo
				{
					Name = $"default_slot_{slot}",
					ObjectIndex = -1
				});
				continue;
			}

			var materialEntry = EntriesInContainer(container)
				.FirstOrDefault(e => e.NemoObj?.ClassId == 30 && e.NemoObj.ObjectIndex == matObjectIndex);
			if (materialEntry?.NemoObj == null)
			{
				results.Add(new ResolvedMaterialInfo
				{
					Name = $"mat_{matObjectIndex}",
					ObjectIndex = matObjectIndex
				});
				continue;
			}

			var materialRaw = materialEntry.LoadRawData();
			var material = materialRaw != null ? CkObjectDispatcher.ParseMaterial(materialRaw, materialEntry.NemoObj) : null;
			ArchiveEntry? textureEntry = null;
			if (material != null && material.TextureObjectIndex >= 0)
			{
				textureEntry = EntriesInContainer(container)
					.FirstOrDefault(e => e.NemoObj?.ClassId == 31 && e.NemoObj.ObjectIndex == material.TextureObjectIndex);
			}

			byte[]? textureBytes = textureEntry?.LoadData();
			ImageSource? textureImage = null;
			if (textureBytes != null && textureBytes.Length > 0 && textureEntry != null)
			{
				var preview = AssetLoaderRegistry.LoadAssetFromData(textureBytes, textureEntry.Name);
				textureImage = preview.ImageData;
			}

			results.Add(new ResolvedMaterialInfo
			{
				Name = string.IsNullOrWhiteSpace(materialEntry.NemoObj.Name) ? $"mat_{matObjectIndex}" : materialEntry.NemoObj.Name,
				ObjectIndex = matObjectIndex,
				TextureFileName = textureEntry != null ? CkObjectExporter.SafeFileName(textureEntry.Name, $"texture_{matObjectIndex}.tga") : null,
				TextureBytes = textureBytes,
				TextureImage = textureImage
			});
		}

		return results;
	}

	private ImageSource? ResolveTextureImageForMesh(string container, CkMesh mesh)
		=> ResolveMaterialInfos(container, mesh).FirstOrDefault(m => m.TextureImage != null)?.TextureImage;

	private string BuildBehaviorDot(CkBehaviorGraph graph, string container)
	{
		string LabelForIndex(int idx)
		{
			var obj = EntriesInContainer(container).FirstOrDefault(e => e.NemoObj?.ObjectIndex == idx)?.NemoObj;
			return obj == null
				? $"idx {idx}"
				: $"{idx}: {obj.Name}\\n{obj.ClassName}";
		}

		string NodeId(int idx) => "n" + idx.ToString(System.Globalization.CultureInfo.InvariantCulture);
		var sb = new StringBuilder();
		sb.AppendLine("digraph VirtoolsBehavior {");
		sb.AppendLine("  rankdir=LR;");
		sb.AppendLine("  node [shape=box, fontname=\"Segoe UI\"];");
		sb.AppendLine($"  self [label=\"{EscapeDot(graph.Name)}\\n{graph.ClassName}\\nidx {graph.ObjectIndex}\", style=filled];");

		foreach (var idx in graph.CandidateObjectIndices.Take(160))
		{
			sb.AppendLine($"  {NodeId(idx)} [label=\"{EscapeDot(LabelForIndex(idx))}\"];");
			sb.AppendLine($"  self -> {NodeId(idx)};");
		}

		sb.AppendLine("}");
		return sb.ToString();
	}

	private static string EscapeDot(string text)
		=> text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

	private byte[]? GetAssetBytes(AssetItem asset)
	{
		if (_entryMap.TryGetValue(asset.FullPath, out var entry))
			return entry.LoadData();
		if (File.Exists(asset.FullPath))
			return File.ReadAllBytes(asset.FullPath);
		return null;
	}

	private byte[]? GetAssetRawBytes(AssetItem asset)
	{
		if (_entryMap.TryGetValue(asset.FullPath, out var entry))
			return entry.LoadRawData();
		if (File.Exists(asset.FullPath))
			return File.ReadAllBytes(asset.FullPath);
		return null;
	}

	private static string SanitizePathSegment(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return "asset";
		foreach (var ch in Path.GetInvalidFileNameChars())
			name = name.Replace(ch, '_');
		return name;
	}

	private void MenuItem_ToggleHideUnknown(object sender, RoutedEventArgs e)
	{
		if (sender is MenuItem menuItem)
		{
			_hideUnknownFiles = menuItem.IsChecked;
			UpdateAssetListFilter();
		}
	}

	private void LoadSingleFile(string filePath)
	{
		try
		{
			ClearWorkspace();
			AddFileToWorkspace(filePath);
		}
		catch (Exception ex)
		{
			UpdateStatus($"Error loading file: {ex.Message}");
		}
	}

	private void ClearPreviewPanels()
	{
		TextureReferencePanel.Visibility = Visibility.Collapsed;
		TextureReferenceTitle.Text = string.Empty;
		TextureReferenceBody.Text = string.Empty;

		MediaPanel.Visibility = Visibility.Collapsed;
		VideoOverlay.Visibility = Visibility.Collapsed;
		VideoPreview.Source = null;

		ImagePreview.Source = null;
		ImagePreview.Visibility = Visibility.Collapsed;

		HexPreviewText.Text = string.Empty;
		HexPreviewText.Visibility = Visibility.Collapsed;

		NemoPanel.Visibility = Visibility.Collapsed;
		NemoObjectTree.ItemsSource = null;
		NemoHeaderText.Text = string.Empty;

		DumpHexText.Text = string.Empty;
		PreviewMetadata.Text = string.Empty;

		Clear3DViewport();
	}

	private void ShowTextureReferenceWarning(string textureName)
	{
		ClearPreviewPanels();

		TextureReferenceTitle.Text = $"Texture reference: {textureName}";
		TextureReferenceBody.Text =
			"This texture is an external reference inside the CMO file.\n" +
			"No matching file was found in the currently loaded workspace.\n\n" +
			"Load the VXBG archive that contains this texture and try again.";

		TextureReferencePanel.Visibility = Visibility.Visible;
	}

	private void ClearWorkspace()
	{
		_currentPreviewAsset = null;
		_fileItems.Clear();
		_assetItems.Clear();
		_allAssets.Clear();
		foreach (var src in _archiveSources) src.Dispose();
		_archiveSources.Clear();
		_entryMap.Clear();
		_externalTextureCache.Clear();
		_workspaceGeneration++;
		_previewCts?.Cancel();
		ClearPreviewPanels();
		PropertiesText.Text = "";
		StopAndHideMedia();
		GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
	}

	private void AddFileToWorkspace(string filePath)
	{
		var fileInfo = new FileInfo(filePath);
		var (kind, loaderId) = AssetLoaderRegistry.Classify(filePath);

		if (kind == AssetKind.Archive && loaderId == "vxbg")
		{
			LoadVxbgArchive(filePath, fileInfo);
			UpdateStatus($"Loaded VXBG archive: {fileInfo.Name}");
			return;
		}

		var asset = CreateAssetItem(fileInfo);
		_assetItems.Add(asset);
		_allAssets.Add(asset);

		var treeItem = CreateTreeItemFromFile(fileInfo);
		_fileItems.Add(treeItem);

		if (asset.Kind == AssetKind.NemoFile)
			_ = AddCmoSubAssetsAsync(filePath, fileInfo.Name, treeItem);

		UpdateStatus($"Loaded file: {fileInfo.Name}");
	}

	private void LoadVxbgArchive(string archivePath, FileInfo archiveInfo)
	{
		try
		{
			var src = new VxbgArchiveSource(archivePath);
			_archiveSources.Add(src);

			var archiveItem = new FileTreeItem
			{
				Name = archiveInfo.Name,
				FullPath = archivePath,
				IsDirectory = true
			};
			_fileItems.Add(archiveItem);

			foreach (var entry in src.Entries)
			{
				var virtualPath = $"{archiveInfo.Name}/{entry.Name}";
				var (entryKind, entryLoaderId) = AssetLoaderRegistry.Classify(entry.Name);
				entry.Kind = entryKind;
				entry.LoaderId = entryLoaderId;
				_entryMap[virtualPath] = entry;

				var assetItem = new AssetItem
				{
					Name = entry.Name,
					FullPath = virtualPath,
					Container = archiveInfo.Name,
					FileSize = entry.FileSize,
					Kind = entryKind,
					LoaderId = entryLoaderId,
					Modified = archiveInfo.LastWriteTime
				};
				_allAssets.Add(assetItem);
				_assetItems.Add(assetItem);

				archiveItem.Children.Add(new FileTreeItem
				{
					Name = entry.Name,
					FullPath = virtualPath,
					IsDirectory = false,
					Kind = entryKind,
					LoaderId = entryLoaderId,
				});
			}

			UpdateStatus($"Loaded VXBG archive: {archiveInfo.Name} ({src.Entries.Count} files)");
		}
		catch (Exception ex)
		{
			UpdateStatus($"Error loading VXBG archive: {ex.Message}");
			MessageBox.Show($"Failed to load VXBG archive:\n\n{ex.Message}", "Archive Error", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private async Task AddCmoSubAssetsAsync(string cmoFilePath, string cmoFileName, FileTreeItem? parentTree)
	{
		int gen = _workspaceGeneration;

		var entryMapSnapshot = _entryMap;
		var extCacheSnapshot = _externalTextureCache;
		var selfPrefix = cmoFileName + "/";

		CmoArchiveSource? src = null;
		List<(AssetItem asset, ArchiveEntry entry, string vpath, FileTreeItem node)>? items = null;
		NemoFileInfo? parsedInfo = null;

		await _cmoLoadSem.WaitAsync();
		try
		{
			(src, items, parsedInfo) = await Task.Run<(CmoArchiveSource?, List<(AssetItem, ArchiveEntry, string, FileTreeItem)>?, NemoFileInfo?)>(() =>
			{
				var info = CkFileParser.TryParseHeaderOnly(cmoFilePath);
				if (info == null || info.ParseError != null || info.Objects.Count == 0)
					return (null, null, info);

				var extractor = new CkObjectDataExtractor(cmoFilePath, info);

				Func<string, byte[]?> resolver = texName =>
				{
					var baseName = Path.GetFileNameWithoutExtension(texName);

					if (extCacheSnapshot.TryGetValue(baseName, out var cached)) return cached.LoadData();
					if (extCacheSnapshot.TryGetValue(baseName + ".tga", out cached)) return cached.LoadData();
					if (extCacheSnapshot.TryGetValue(baseName + ".jpg", out cached)) return cached.LoadData();

					foreach (var kvp in entryMapSnapshot)
					{
						if (kvp.Key.StartsWith(selfPrefix, StringComparison.OrdinalIgnoreCase)) continue;
						if (kvp.Value.FileSize == 0) continue;
						if (Path.GetFileNameWithoutExtension(kvp.Key)
								.Equals(baseName, StringComparison.OrdinalIgnoreCase))
							return kvp.Value.LoadData();
					}
					return null;
				};

				var cmoSrc = new CmoArchiveSource(
					info, cmoFileName, resolver,
					rawObjectLoader: obj => extractor.TryGetRawObjectBytes(obj),
					objectSizeResolver: obj => extractor.EstimateObjectSize(obj));

				var list = new List<(AssetItem, ArchiveEntry, string, FileTreeItem)>(cmoSrc.Entries.Count);
				foreach (var entry in cmoSrc.Entries)
				{
					var vpath = $"{cmoFileName}/{entry.Name}";
					var asset = new AssetItem
					{
						Name = entry.Name,
						FullPath = vpath,
						Container = cmoFileName,
						FileSize = entry.FileSize,
						Kind = entry.Kind,
						LoaderId = entry.LoaderId,
						CmoSourcePath = cmoFilePath,

					};

					if (entry.Kind == AssetKind.CmoObject)
						asset.CmoObjectRef = entry.NemoObj
							?? info.Objects.FirstOrDefault(o =>
								(string.IsNullOrEmpty(o.Name) ? $"{o.ClassName}_0x{o.CkId:X8}" : o.Name)
								.Equals(entry.Name, StringComparison.Ordinal));

					var node = new FileTreeItem
					{
						Name = entry.Name,
						FullPath = vpath,
						IsDirectory = false,
						Kind = entry.Kind,
						LoaderId = entry.LoaderId,
					};
					list.Add((asset, entry, vpath, node));
				}

				return (cmoSrc, list, info);
			});
		}
		catch (Exception ex)
		{
			if (_workspaceGeneration == gen)
				UpdateStatus($"CMO expand error ({cmoFileName}): {ex.Message}");
			return;
		}
		finally
		{
			_cmoLoadSem.Release();
		}

		if (src == null || items == null || _workspaceGeneration != gen)
			return;

		if (parsedInfo != null)
		{
			var parentAsset = _allAssets.FirstOrDefault(a =>
				a.Kind == AssetKind.NemoFile && a.FullPath == cmoFilePath);
			if (parentAsset != null)
				parentAsset.CachedNemoInfo = parsedInfo;
		}

		_archiveSources.Add(src);

		foreach (var (asset, entry, vpath, _) in items)
		{
			_entryMap[vpath] = entry;
			_allAssets.Add(asset);
		}

		if (parentTree != null)
			parentTree.Children.AddRange(items.Select(x => x.node));

		UpdateAssetListFilter();

		UpdateStatus($"Expanded {cmoFileName}: {items.Count} object(s)");
	}

	private void LoadFolder(string folderPath)
	{
		try
		{
			ClearWorkspace();
			AddFolderToWorkspace(folderPath);
		}
		catch (Exception ex)
		{
			UpdateStatus($"Error loading folder: {ex.Message}");
		}
	}

	private void AddFolderToWorkspace(string folderPath)
	{
		var dirInfo = new DirectoryInfo(folderPath);
		var treeItem = CreateTreeItemFromDirectory(dirInfo);
		_fileItems.Add(treeItem);

		PopulateAssetList(dirInfo);
		UpdateStatus($"Loaded folder: {dirInfo.Name} ({_allAssets.Count} assets)");
	}

	private void PopulateAssetList(DirectoryInfo directory)
	{
		try
		{
			var files = directory.GetFiles();
			foreach (var file in files)
			{
				var (kind, loaderId) = AssetLoaderRegistry.Classify(file.FullName);

				if (kind == AssetKind.Archive && loaderId == "vxbg")
				{
					try
					{
						var src = new VxbgArchiveSource(file.FullName);
						_archiveSources.Add(src);
						foreach (var entry in src.Entries)
						{
							var virtualPath = $"{file.Name}/{entry.Name}";
							var (eKind, eLid) = AssetLoaderRegistry.Classify(entry.Name);
							entry.Kind = eKind;
							entry.LoaderId = eLid;
							_entryMap[virtualPath] = entry;

							var assetItem = new AssetItem
							{
								Name = entry.Name,
								FullPath = virtualPath,
								Container = file.Name,
								FileSize = entry.FileSize,
								Kind = eKind,
								LoaderId = eLid,
								Modified = file.LastWriteTime
							};
							_assetItems.Add(assetItem);
							_allAssets.Add(assetItem);
						}
					}
					catch
					{
						var asset = CreateAssetItem(file);
						_assetItems.Add(asset);
						_allAssets.Add(asset);
					}
				}
				else
				{
					var asset = CreateAssetItem(file);
					_assetItems.Add(asset);
					_allAssets.Add(asset);
					if (asset.Kind == AssetKind.NemoFile)
						_ = AddCmoSubAssetsAsync(file.FullName, file.Name, FindTreeItemByPath(file.FullName));
				}
			}

			var subDirs = directory.GetDirectories();
			foreach (var subDir in subDirs.Where(d => !d.Name.StartsWith(".")))
			{
				PopulateAssetList(subDir);
			}
		}
		catch (Exception ex)
		{
			UpdateStatus($"Error reading directory: {ex.Message}");
		}
	}

	private void Window_DragOver(object sender, DragEventArgs e)
	{
		e.Effects = DragDropEffects.Copy;
		e.Handled = true;
	}

	private void Window_Drop(object sender, DragEventArgs e)
	{
		if (e.Data.GetDataPresent(DataFormats.FileDrop))
		{
			var files = (string[])e.Data.GetData(DataFormats.FileDrop);
			if (files.Length == 0)
				return;

			try
			{
				ClearWorkspace();
				foreach (var item in files)
				{
					if (Directory.Exists(item))
						AddFolderToWorkspace(item);
					else if (File.Exists(item))
						AddFileToWorkspace(item);
				}
				UpdateStatus($"Loaded {files.Length} item(s)");
			}
			catch (Exception ex)
			{
				UpdateStatus($"Error loading dropped items: {ex.Message}");
			}
		}
	}

	private FileTreeItem CreateTreeItemFromDirectory(DirectoryInfo directory)
	{
		var item = new FileTreeItem
		{
			Name = directory.Name,
			FullPath = directory.FullName,
			IsDirectory = true
		};

		try
		{
			var files = directory.GetFiles();
			var dirs = directory.GetDirectories();

			foreach (var file in files)
			{
				item.Children.Add(CreateTreeItemFromFile(file));
			}

			foreach (var dir in dirs.Where(d => !d.Name.StartsWith(".")))
			{
				item.Children.Add(CreateTreeItemFromDirectory(dir));
			}
		}
		catch (Exception ex)
		{
			UpdateStatus($"Error reading directory: {ex.Message}");
		}

		return item;
	}

	private FileTreeItem CreateTreeItemFromFile(FileInfo file)
	{
		var (kind, loaderId) = AssetLoaderRegistry.Classify(file.FullName);
		return new FileTreeItem
		{
			Name = file.Name,
			FullPath = file.FullName,
			IsDirectory = false,
			Kind = kind,
			LoaderId = loaderId
		};
	}

	private void TreeViewItem_Selected(object sender, RoutedEventArgs e)
	{
		try
		{
			if (FileTreeView.SelectedItem is FileTreeItem fileItem && !fileItem.IsDirectory)
			{

				var asset = _allAssets.FirstOrDefault(a => a.FullPath == fileItem.FullPath);
				if (asset != null)
				{
					if (_assetItems.Contains(asset))
						AssetGrid.SelectedItem = asset;
					else
						DisplayAssetPreview(asset);
				}
			}
		}
		catch (Exception ex)
		{
			UpdateStatus($"Error selecting item: {ex.Message}");
		}
	}

	private void ExpandAllTreeItems(ItemCollection items)
	{
		foreach (var item in items)
		{
			if (item is FileTreeItem)
			{
				var treeViewItem = FileTreeView.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
				if (treeViewItem != null)
				{
					treeViewItem.IsExpanded = true;
					ExpandAllTreeItems(treeViewItem.Items);
				}
			}
		}
	}

	private void CollapseAllTreeItems(ItemCollection items)
	{
		foreach (var item in items)
		{
			if (item is FileTreeItem)
			{
				var treeViewItem = FileTreeView.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
				if (treeViewItem != null)
				{
					treeViewItem.IsExpanded = false;
					CollapseAllTreeItems(treeViewItem.Items);
				}
			}
		}
	}

	private void AssetGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (AssetGrid.SelectedItem is AssetItem asset)
		{
			DisplayAssetPreview(asset);
		}
	}

	private AssetItem CreateAssetItem(FileInfo file)
	{
		var (kind, loaderId) = AssetLoaderRegistry.Classify(file.FullName);
		return new AssetItem
		{
			Name = file.Name,
			FullPath = file.FullName,
			Kind = kind,
			LoaderId = loaderId,
			FileSize = file.Length,
			Modified = file.LastWriteTime
		};
	}

	private async void DisplayAssetPreview(AssetItem asset)
	{
		_currentPreviewAsset = asset;
		_previewCts?.Cancel();
		_previewCts = new CancellationTokenSource();
		var ct = _previewCts.Token;

		await StopAndHideMediaAsync();
		if (ct.IsCancellationRequested || asset != _currentPreviewAsset) return;
		ClearPreviewPanels();
		PropertiesText.Text = "";

		try
		{
			if (_entryMap.ContainsKey(asset.FullPath))
			{
				await DisplayArchiveFilePreviewAsync(asset, ct);
				return;
			}

			if (!File.Exists(asset.FullPath))
			{
				PropertiesText.Text = $"File no longer exists: {asset.FullPath}";
				UpdateStatus("File is missing");
				return;
			}

			var fileInfo = new FileInfo(asset.FullPath);
			var ext = fileInfo.Extension;
			string properties = $"Name: {fileInfo.Name}\n";
			properties += $"Type: {asset.TypeName} ({asset.Kind})\n";
			if (!string.IsNullOrWhiteSpace(ext))
				properties += $"Extension: {ext}\n";
			properties += $"Size: {FormatFileSize(fileInfo.Length)} ({fileInfo.Length} bytes)\n";
			if (!string.IsNullOrWhiteSpace(asset.Container))
				properties += $"Container: {asset.Container}\n";
			if (!string.IsNullOrWhiteSpace(asset.LoaderId))
				properties += $"Loader: {asset.LoaderId}\n";
			properties += $"Modified: {fileInfo.LastWriteTime}\n";
			properties += $"Full Path: {fileInfo.FullName}\n";

			PropertiesText.Text = properties;
			PreviewMetadata.Text = $"  {fileInfo.Name}";

			if (asset.Kind == AssetKind.NemoFile && asset.CachedNemoInfo != null)
			{
				ApplyPreviewData(new AssetPreviewData { NemoInfo = asset.CachedNemoInfo }, fileInfo.Name);
				UpdateStatus($"Loaded: {fileInfo.Name}");
				return;
			}

			var capturedPath = asset.FullPath;
			var previewData = await Task.Run(() => AssetLoaderRegistry.LoadAsset(capturedPath), ct);
			if (ct.IsCancellationRequested || asset != _currentPreviewAsset) return;

			ApplyPreviewData(previewData, fileInfo.Name);
			QueueMediaSetup(asset, asset.FullPath, null);
			UpdateStatus($"Loaded: {fileInfo.Name}");
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			if (!ct.IsCancellationRequested)
			{
				PropertiesText.Text = $"Error loading file: {ex.Message}";
				UpdateStatus($"Error: {ex.Message}");
			}
		}
	}

	private async Task DisplayArchiveFilePreviewAsync(AssetItem asset, CancellationToken ct)
	{
		try
		{
			_entryMap.TryGetValue(asset.FullPath, out var entry);

			var data = entry?.LoadData() ?? Array.Empty<byte>();
			var fileName = asset.Name;

			var ext = Path.GetExtension(fileName);
			string properties = $"Name: {fileName}\n";
			properties += $"Type: {asset.TypeName} ({asset.Kind})\n";
			if (!string.IsNullOrWhiteSpace(ext))
				properties += $"Extension: {ext}\n";
			properties += $"Size: {FormatFileSize(data.Length)} ({data.Length} bytes)\n";
			properties += $"Container: {asset.Container}\n";
			if (!string.IsNullOrWhiteSpace(asset.LoaderId))
				properties += $"Loader: {asset.LoaderId}\n";
			properties += $"Virtual Path: {asset.FullPath}\n";

			PropertiesText.Text = properties;
			PreviewMetadata.Text = $"  {fileName} (from archive)";

			if (asset.Kind == AssetKind.Texture2D && data.Length == 0)
			{
				ShowTextureReferenceWarning(asset.Name);
				PreviewMetadata.Text = $"[unresolved]  {asset.Name}";
				return;
			}

			if (asset.Kind == AssetKind.CmoObject)
			{
				var metaText = data.Length > 0
					? Encoding.UTF8.GetString(data)
					: "(no metadata)";

				NemoHeaderText.Text = metaText + "\n\nAnalyzing…";
				NemoObjectTree.ItemsSource = null;
				NemoPanel.Visibility = Visibility.Visible;
				DumpHexText.Text = "Loading raw bytes…";
				PreviewMetadata.Text = $"CMO Object — {asset.Name}  [{asset.TypeName}]";

				var capturedEntry = entry;
				var rawData = await Task.Run(
					() => capturedEntry?.LoadRawData() ?? Array.Empty<byte>(), ct);

				if (ct.IsCancellationRequested || asset != _currentPreviewAsset)
					return;

				if (asset.CmoObjectRef != null && rawData.Length > 0)
				{
					var summary = CkObjectDispatcher.GetSummary(rawData, asset.CmoObjectRef);
					metaText += "\n\n" + summary;

					if (asset.CmoObjectRef.ClassId == 52)
					{
						var dataArray = CkObjectDispatcher.ParseDataArray(rawData, asset.CmoObjectRef);
						if (dataArray != null)
							metaText += "\n\n" + dataArray.ToTabSeparatedText(60);
					}

					var renderable = await Task.Run(() => ResolveRenderableForAsset(asset, rawData), ct);
					if (renderable != null && !ct.IsCancellationRequested && asset == _currentPreviewAsset)
					{
						ShowMesh3D(renderable.Mesh, renderable.Vertices, renderable.TextureImage, renderable.Materials);
						if (!string.IsNullOrWhiteSpace(renderable.SourceLabel))
							metaText += $"\n\nRenderable resolved from: {renderable.SourceLabel}";
					}
					else if (asset.CmoObjectRef != null && Ck3dEntityParser.Instance.SupportedClassIds.Contains(asset.CmoObjectRef.ClassId))
					{
						metaText += "\n\nNo renderable mesh was resolved for this object." +
							"\nThis can be normal for locator nodes, sound emitters, behavior/script targets, cameras, lights, render callbacks, or parent skeleton nodes." +
							"\nIf this object should be visible, send me its object name plus the diagnostic refs below and I can add a specific resolver rule.";
					}

					if (asset.CmoObjectRef != null && CkBehaviorGraphParser.Instance.SupportedClassIds.Contains(asset.CmoObjectRef.ClassId))
					{
						var behavior = CkObjectDispatcher.ParseBehaviorGraph(rawData, asset.CmoObjectRef);
						if (behavior != null)
						{
							metaText += "\n\n" + behavior.ToDiagnosticText(idx =>
							{
								var target = EntriesInContainer(asset.Container).FirstOrDefault(e => e.NemoObj?.ObjectIndex == idx)?.NemoObj;
								return target == null ? null : $"{target.Name} ({target.ClassName})";
							});
						}
					}

				}

				NemoHeaderText.Text = metaText;
				DumpHexText.Text = rawData.Length > 0
					? $"Raw Object ({Math.Min(rawData.Length, 512)} / {rawData.Length:N0} bytes):\n\n" +
					  FormatHexPreview(rawData.Take(512).ToArray())
					: "Raw object bytes are unavailable for this entry.";
				return;
			}

			var assetPreviewData = AssetLoaderRegistry.LoadAssetFromData(data, fileName);
			ApplyPreviewData(assetPreviewData, fileName);
			QueueMediaSetup(asset, fileName, data);
			UpdateStatus($"Loaded: {fileName}");
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			PropertiesText.Text = $"Error loading file: {ex.Message}";
			UpdateStatus($"Error: {ex.Message}");
		}
	}

	private void ApplyPreviewData(AssetPreviewData previewData, string displayName)
	{
		ClearPreviewPanels();

		if (previewData == null)
		{
			PropertiesText.Text += "\nError: preview data is null.\n";
			return;
		}

		if (previewData.ImageData != null)
		{
			ImagePreview.Source = previewData.ImageData;
			ImagePreview.Visibility = Visibility.Visible;

			PreviewMetadata.Text = !string.IsNullOrWhiteSpace(previewData.MetadataText)
				? previewData.MetadataText
				: $"{displayName}  {previewData.ImageData.PixelWidth}×{previewData.ImageData.PixelHeight}  {previewData.ImageData.Format}";

			PropertiesText.Text += $"\nTexture: {previewData.ImageData.PixelWidth}×{previewData.ImageData.PixelHeight}\n";
			PropertiesText.Text += $"DPI: {previewData.ImageData.DpiX}×{previewData.ImageData.DpiY}\n";
			PropertiesText.Text += $"Format: {previewData.ImageData.Format}\n";

			var previewBytes = previewData.GetHexPreview();
			if (previewBytes != null && previewBytes.Length > 0)
				DumpHexText.Text = $"Raw Data Preview ({previewBytes.Length} bytes):\n\n{FormatHexPreview(previewBytes)}";

			return;
		}

		if (previewData.NemoInfo != null)
		{
			BuildNemoTree(previewData.NemoInfo, displayName);

			var raw = previewData.GetHexPreview();
			if (raw != null && raw.Length > 0)
				DumpHexText.Text = $"Header Preview ({raw.Length} bytes):\n\n{FormatHexPreview(raw)}";

			return;
		}

		if (!string.IsNullOrWhiteSpace(previewData.ErrorMessage))
			PropertiesText.Text += $"\nError: {previewData.ErrorMessage}\n";

		if (!string.IsNullOrWhiteSpace(previewData.MetadataText))
			PreviewMetadata.Text = previewData.MetadataText;

		var bytes = previewData.GetHexPreview();
		if (bytes != null && bytes.Length > 0)
		{
			HexPreviewText.Text = FormatHexPreview(bytes);
			HexPreviewText.Visibility = Visibility.Visible;
			if (string.IsNullOrWhiteSpace(PreviewMetadata.Text))
				PreviewMetadata.Text = $"{displayName}  Hex ({bytes.Length} B)";
			DumpHexText.Text = $"Raw Data Preview ({bytes.Length} bytes):\n\n{HexPreviewText.Text}";
		}
		else if (string.IsNullOrWhiteSpace(PreviewMetadata.Text))
		{
			PreviewMetadata.Text = $"{displayName}";
		}
	}

	private void BuildNemoTree(NemoFileInfo info, string displayName)
	{
		var hdr = new System.Text.StringBuilder();
		hdr.AppendLine($"File     {displayName}");
		hdr.AppendLine($"Version  {info.FileVersion}   Mode: {info.WriteModeStr}");
		hdr.AppendLine($"Build    Virtools {info.ProductBuildStr}   CK: {info.CkVersionStr}");
		hdr.AppendLine($"Objects  {info.ObjectCount}   Managers: {info.ManagerCount}   MaxID: {info.MaxIDSaved}");
		hdr.Append($"Data     {info.DataPackSize:N0} B packed → {info.DataUnPackSize:N0} B");
		NemoHeaderText.Text = hdr.ToString();

		PreviewMetadata.Text = $"{displayName}   {info.ObjectCount} objects   Virtools {info.ProductBuildStr}";

		var groups = info.Objects
			.GroupBy(o => o.GroupName)
			.OrderBy(g => g.Key)
			.Select(g => new NemoTreeGroup
			{
				GroupName = g.Key,
				CountLabel = $"({g.Count()})",
				Objects = g.Select(o => new NemoTreeObject
				{
					DisplayName = string.IsNullOrEmpty(o.Name) ? "(unnamed)" : o.Name,
					ClassName = o.ClassName,
				}).ToList()
			})
			.ToList();

		NemoObjectTree.ItemsSource = groups;
		NemoPanel.Visibility = Visibility.Visible;

		foreach (var group in groups)
			group.IsExpanded = group.Objects.Count <= 20;
	}

	private string FormatHexPreview(byte[] bytes)
	{
		var lines = new List<string>();
		lines.Add("Offset    Hex Data                                        ASCII");
		lines.Add("--------  ------------------------------------------------  ----------------");

		for (int i = 0; i < bytes.Length; i += 16)
		{
			var chunk = bytes.Skip(i).Take(16).ToArray();
			var hex = string.Join(" ", chunk.Select(b => b.ToString("X2")));
			var ascii = System.Text.Encoding.ASCII.GetString(chunk)
				.Select(c => char.IsControl(c) ? '.' : c)
				.Aggregate("", (a, c) => a + c);
			lines.Add($"{i:X8}  {hex,-48}  {ascii}");
		}
		return string.Join("\n", lines);
	}

	private void QueueMediaSetup(AssetItem asset, string filePathOrName, byte[]? archiveData)
	{
		if (asset.Kind != AssetKind.Audio && asset.Kind != AssetKind.Video)
			return;

		_mediaCts?.Cancel();

		var prevTask = _currentMediaSetupTask;
		_mediaCts = new CancellationTokenSource();
		var ct = _mediaCts.Token;

		_currentMediaSetupTask = RunQueuedSetupAsync(prevTask, asset, filePathOrName, archiveData, ct);
	}

	private async Task RunQueuedSetupAsync(Task? prevTask, AssetItem asset, string filePathOrName, byte[]? archiveData, CancellationToken ct)
	{
		if (prevTask != null)
		{
			try { await prevTask.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
			catch (Exception ex) { CrashLog.Write("Previous media setup failed", ex); }
		}

		if (ct.IsCancellationRequested || asset != _currentPreviewAsset)
			return;

		try
		{
			await SetupMediaPlaybackAsync(asset, filePathOrName, archiveData, ct).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			CrashLog.Write("Media setup failed", ex);
			await Dispatcher.InvokeAsync(() =>
			{
				PropertiesText.Text += $"\nPlayback not available: {ex.Message}\n";
				var detail = ex.InnerException?.Message;
				if (!string.IsNullOrWhiteSpace(detail))
					PropertiesText.Text += $"Detail: {detail}\n";

				string[] tail;
				lock (_ffmpegLogTail) { tail = _ffmpegLogTail.ToArray(); }
				if (tail.Length > 0)
				{
					PropertiesText.Text += "\nFFmpeg log (tail):\n";
					foreach (var line in tail.TakeLast(20))
						PropertiesText.Text += "  " + line + "\n";
				}

				ShowVideoError("Playback unavailable");
			}, DispatcherPriority.Background);
		}
	}

	private async Task ShowPlaybackUnavailableAsync(string message)
	{
		await Dispatcher.InvokeAsync(() =>
		{
			PropertiesText.Text += $"\nPlayback not available: {message}\n";
			ShowVideoError("Playback unavailable");
		}, DispatcherPriority.Background);
	}

	private async Task SetupMediaPlaybackAsync(AssetItem asset, string filePathOrName, byte[]? archiveData, CancellationToken ct)
	{
		if (asset.Kind != AssetKind.Audio && asset.Kind != AssetKind.Video)
			return;

		await Dispatcher.InvokeAsync(() =>
		{
			_mediaTimer.Stop();
			MediaPositionSlider.IsEnabled = false;
			MediaPositionSlider.Maximum = 1;
			MediaPositionSlider.Value = 0;
			MediaTimeText.Text = "00:00 / 00:00";
			MediaPanel.Visibility = Visibility.Visible;
		}, DispatcherPriority.Send, ct);

		StopAudioBackend();
		await CloseVideoBackendAsync(ct).ConfigureAwait(false);

		ct.ThrowIfCancellationRequested();
		if (asset != _currentPreviewAsset)
			return;

		if (asset.Kind == AssetKind.Audio)
		{
			_isAudioActive = true;

			await SetupAudioBackendAsync(asset, filePathOrName, archiveData, ct).ConfigureAwait(false);

			await Dispatcher.InvokeAsync(() =>
			{
				VideoPreview.Visibility = Visibility.Collapsed;
				MediaVolumeSlider.IsEnabled = true;
				MediaPositionSlider.IsEnabled = _audioStream != null;
				MediaPositionSlider.Maximum = _audioStream?.TotalTime.TotalSeconds ?? 1;
				UpdateMediaTimeText();
			}, DispatcherPriority.Background, ct);
			return;
		}

		_isAudioActive = false;
		await Dispatcher.InvokeAsync(() => VideoPreview.Visibility = Visibility.Visible, DispatcherPriority.Send, ct);

		await EnsureFfmpegReadyForVideoAsync(ct).ConfigureAwait(false);

		string pathOnDisk;
		if (archiveData != null)
		{
			pathOnDisk = await GetOrCreateTempMediaAsync(asset.FullPath, filePathOrName, archiveData, ct).ConfigureAwait(false);
		}
		else
		{
			pathOnDisk = Path.GetFullPath(filePathOrName);
			if (!File.Exists(pathOnDisk))
			{
				await ShowPlaybackUnavailableAsync($"Video file not found: {pathOnDisk}").ConfigureAwait(false);
				return;
			}
		}

		ct.ThrowIfCancellationRequested();
		if (asset != _currentPreviewAsset)
			return;

		await OpenVideoBackendAsync(asset.FullPath, pathOnDisk, ct).ConfigureAwait(false);
	}

	private async Task<VideoFrameCache> GetOrCreateVideoFrameCacheAsync(string cacheKey, string sourcePath, CancellationToken ct)
	{
		var frameKey = cacheKey + "|ffmpeg-frame-preview-v8-original-fps";
		lock (_mediaTempLock)
		{
			if (_videoFrameCache.TryGetValue(frameKey, out var existing) &&
				Directory.Exists(existing.DirectoryPath) &&
				existing.FramePaths.Count > 0 &&
				existing.FramePaths.All(File.Exists))
			{
				return existing;
			}
		}

		var ffmpegExe = ResolveFfmpegExe();
		if (string.IsNullOrWhiteSpace(ffmpegExe) || !File.Exists(ffmpegExe))
			throw new FileNotFoundException("ffmpeg.exe was not found after FFmpeg setup completed.", ffmpegExe);

		string root = Path.Combine(Path.GetTempPath(), "SyberiaDatamine", "preview_frames");
		Directory.CreateDirectory(root);
		string frameDir = Path.Combine(root, Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(frameDir);

		try
		{
			var frameRate = await ProbeVideoFrameRateAsync(ffmpegExe, sourcePath, ct).ConfigureAwait(false);

			await Dispatcher.InvokeAsync(() => ShowVideoLoading($"Extracting video frames… {frameRate:0.###} fps"), DispatcherPriority.Background, ct);
			await RunFfmpegFrameExtractAsync(ffmpegExe, sourcePath, frameDir, ct).ConfigureAwait(false);

			var frames = Directory.EnumerateFiles(frameDir, "frame_*.jpg")
				.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
				.ToList();

			if (frames.Count == 0)
			{
				await Dispatcher.InvokeAsync(() => ShowVideoLoading("Retrying frame extraction…"), DispatcherPriority.Background, ct);
				await RunFfmpegSingleFrameExtractAsync(ffmpegExe, sourcePath, frameDir, ct).ConfigureAwait(false);
				frames = Directory.EnumerateFiles(frameDir, "frame_*.jpg")
					.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
					.ToList();
			}

			if (frames.Count == 0)
				throw new InvalidDataException("FFmpeg completed but did not produce preview frames.");

			var cache = new VideoFrameCache
			{
				DirectoryPath = frameDir,
				FramePaths = frames,
				FrameRate = frameRate
			};

			lock (_mediaTempLock)
			{
				_videoFrameCache[frameKey] = cache;
			}

			return cache;
		}
		catch
		{
			try { if (Directory.Exists(frameDir)) Directory.Delete(frameDir, recursive: true); } catch { }
			throw;
		}
	}

	private async Task RunFfmpegFrameExtractAsync(string ffmpegExe, string inputPath, string frameDir, CancellationToken ct)
	{
		var outputPattern = Path.Combine(frameDir, "frame_%06d.jpg");

		var vf = "scale='min(720,iw)':-2";

		var psi = new ProcessStartInfo(ffmpegExe)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
		};

		void Add(string arg) => psi.ArgumentList.Add(arg);
		Add("-hide_banner");
		Add("-nostdin");
		Add("-y");
		Add("-loglevel");
		Add("warning");
		Add("-threads");
		Add("1");
		Add("-fflags");
		Add("+genpts");
		Add("-i");
		Add(inputPath);
		Add("-map");
		Add("0:v:0");
		Add("-an");
		Add("-sn");
		Add("-vf");
		Add(vf);
		Add("-vsync");
		Add("0");
		Add("-q:v");
		Add("5");
		Add(outputPattern);

		await RunFfmpegProcessAsync(psi, ct).ConfigureAwait(false);
	}

	private async Task RunFfmpegSingleFrameExtractAsync(string ffmpegExe, string inputPath, string frameDir, CancellationToken ct)
	{
		var outputPath = Path.Combine(frameDir, "frame_000001.jpg");
		var psi = new ProcessStartInfo(ffmpegExe)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
		};

		void Add(string arg) => psi.ArgumentList.Add(arg);
		Add("-hide_banner");
		Add("-nostdin");
		Add("-y");
		Add("-loglevel");
		Add("warning");
		Add("-threads");
		Add("1");
		Add("-i");
		Add(inputPath);
		Add("-map");
		Add("0:v:0");
		Add("-an");
		Add("-frames:v");
		Add("1");
		Add("-q:v");
		Add("5");
		Add(outputPath);

		await RunFfmpegProcessAsync(psi, ct).ConfigureAwait(false);
	}

	private async Task RunFfmpegProcessAsync(ProcessStartInfo psi, CancellationToken ct)
	{
		AppendFfmpegLogTail("ffmpeg " + string.Join(" ", psi.ArgumentList.Select(QuoteArgForLog)));

		using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
		try
		{
			if (!process.Start())
				throw new InvalidOperationException("Failed to start ffmpeg.exe.");

			var stderrTask = process.StandardError.ReadToEndAsync();
			var stdoutTask = process.StandardOutput.ReadToEndAsync();

			try
			{
				await process.WaitForExitAsync(ct).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
				throw;
			}

			var stderr = await stderrTask.ConfigureAwait(false);
			var stdout = await stdoutTask.ConfigureAwait(false);
			AppendFfmpegLogTail(stderr);
			AppendFfmpegLogTail(stdout);

			if (process.ExitCode != 0)
			{
				var detail = ShortenLog(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr, 2400);
				throw new InvalidOperationException($"ffmpeg.exe exited with code {process.ExitCode}. {detail}");
			}
		}
		catch
		{
			try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
			throw;
		}
	}

	private async Task<string> PrepareVideoPlaybackFileAsync(string cacheKey, string sourcePath, byte[]? originalBytes, CancellationToken ct)
	{
		if (!File.Exists(sourcePath))
			return sourcePath;

		var container = DetectVideoContainer(sourcePath, originalBytes);
		if (container != VideoContainer.Bink)
			return sourcePath;

		await Dispatcher.InvokeAsync(() => ShowVideoLoading("Converting Bink video…"), DispatcherPriority.Background, ct);
		return await GetOrCreateTranscodedVideoAsync(cacheKey, sourcePath, ct).ConfigureAwait(false);
	}

	private async Task<string> GetOrCreateTranscodedVideoAsync(string cacheKey, string sourcePath, CancellationToken ct)
	{
		var transcodeKey = cacheKey + "|native-mp4-preview";
		lock (_mediaTempLock)
		{
			if (_mediaTempCache.TryGetValue(transcodeKey, out var existingPath) && File.Exists(existingPath) && new FileInfo(existingPath).Length > 0)
				return existingPath;
		}

		var ffmpegExe = ResolveFfmpegExe();
		if (string.IsNullOrWhiteSpace(ffmpegExe) || !File.Exists(ffmpegExe))
			throw new FileNotFoundException("ffmpeg.exe was not found after FFmpeg setup completed.", ffmpegExe);

		string dir = Path.Combine(Path.GetTempPath(), "SyberiaDatamine", "preview");
		Directory.CreateDirectory(dir);

		string outPath = Path.Combine(dir, $"{Guid.NewGuid():N}.mp4");
		string writingPath = outPath + ".writing.mp4";

		try { if (File.Exists(writingPath)) File.Delete(writingPath); } catch { }

		var attempts = new[]
		{
			new TranscodeAttempt("H.264 + audio", UseFallbackEncoder: false, IncludeAudio: true),
			new TranscodeAttempt("H.264 video-only", UseFallbackEncoder: false, IncludeAudio: false),
			new TranscodeAttempt("MPEG-4 video-only", UseFallbackEncoder: true, IncludeAudio: false),
		};

		Exception? lastError = null;
		foreach (var attempt in attempts)
		{
			ct.ThrowIfCancellationRequested();
			try { if (File.Exists(writingPath)) File.Delete(writingPath); } catch { }

			await Dispatcher.InvokeAsync(
				() => ShowVideoLoading($"Converting Bink video… {attempt.Label}"),
				DispatcherPriority.Background,
				ct);

			try
			{
				await RunFfmpegTranscodeAsync(ffmpegExe, sourcePath, writingPath, attempt.UseFallbackEncoder, attempt.IncludeAudio, ct).ConfigureAwait(false);
				lastError = null;
				break;
			}
			catch (Exception ex) when (!ct.IsCancellationRequested)
			{
				lastError = ex;
				CrashLog.Write($"Bink transcode failed: {attempt.Label}", ex);
			}
		}

		if (lastError != null)
			throw new InvalidOperationException("Bink-to-MP4 conversion failed. See the FFmpeg log tail below for details.", lastError);

		ct.ThrowIfCancellationRequested();

		if (!File.Exists(writingPath) || new FileInfo(writingPath).Length == 0)
			throw new InvalidDataException("FFmpeg conversion completed but produced an empty preview file.");

		File.Move(writingPath, outPath, overwrite: false);

		lock (_mediaTempLock)
		{
			_mediaTempCache[transcodeKey] = outPath;
		}

		return outPath;
	}

	private readonly record struct TranscodeAttempt(string Label, bool UseFallbackEncoder, bool IncludeAudio);

	private async Task RunFfmpegTranscodeAsync(string ffmpegExe, string inputPath, string outputPath, bool useFallbackEncoder, bool includeAudio, CancellationToken ct)
	{
		var psi = new ProcessStartInfo(ffmpegExe)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
		};

		void Add(string arg) => psi.ArgumentList.Add(arg);
		Add("-hide_banner");
		Add("-nostdin");
		Add("-y");
		Add("-loglevel");
		Add("warning");
		Add("-threads");
		Add("1");
		Add("-fflags");
		Add("+genpts");
		Add("-i");
		Add(inputPath);
		Add("-map");
		Add("0:v:0");
		if (includeAudio)
		{
			Add("-map");
			Add("0:a?");
		}
		else
		{
			Add("-an");
		}
		Add("-vf");
		Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");

		if (useFallbackEncoder)
		{
			Add("-c:v");
			Add("mpeg4");
			Add("-q:v");
			Add("4");
		}
		else
		{
			Add("-c:v");
			Add("libx264");
			Add("-preset");
			Add("veryfast");
			Add("-crf");
			Add("20");
		}

		Add("-pix_fmt");
		Add("yuv420p");
		if (includeAudio)
		{
			Add("-c:a");
			Add("aac");
			Add("-b:a");
			Add("128k");
		}
		Add("-movflags");
		Add("+faststart");
		Add("-avoid_negative_ts");
		Add("make_zero");
		Add(outputPath);

		AppendFfmpegLogTail("ffmpeg " + string.Join(" ", psi.ArgumentList.Select(QuoteArgForLog)));

		using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
		try
		{
			if (!process.Start())
				throw new InvalidOperationException("Failed to start ffmpeg.exe.");

			var stderrTask = process.StandardError.ReadToEndAsync();
			var stdoutTask = process.StandardOutput.ReadToEndAsync();

			try
			{
				await process.WaitForExitAsync(ct).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
				throw;
			}

			var stderr = await stderrTask.ConfigureAwait(false);
			var stdout = await stdoutTask.ConfigureAwait(false);
			AppendFfmpegLogTail(stderr);
			AppendFfmpegLogTail(stdout);

			if (process.ExitCode != 0)
			{
				var detail = ShortenLog(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr, 2400);
				throw new InvalidOperationException($"ffmpeg.exe exited with code {process.ExitCode}. {detail}");
			}
		}
		catch
		{
			try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
			throw;
		}
	}

	private static string QuoteArgForLog(string arg)
	{
		if (string.IsNullOrEmpty(arg)) return "\"\"";
		return arg.Any(char.IsWhiteSpace) ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
	}

	private static string? ResolveFfmpegExe()
	{
		var dir = FfmpegBootstrapper.ResolvedFfmpegDir;
		if (!string.IsNullOrWhiteSpace(dir))
		{
			var exe = Path.Combine(dir, "ffmpeg.exe");
			if (File.Exists(exe))
				return exe;
		}
		return null;
	}

	private static string? ResolveFfprobeExe(string ffmpegExe)
	{
		try
		{
			var dir = Path.GetDirectoryName(ffmpegExe);
			if (!string.IsNullOrWhiteSpace(dir))
			{
				var ffprobe = Path.Combine(dir, "ffprobe.exe");
				if (File.Exists(ffprobe))
					return ffprobe;
			}
		}
		catch
		{
		}

		return null;
	}

	private async Task<double> ProbeVideoFrameRateAsync(string ffmpegExe, string inputPath, CancellationToken ct)
	{
		var ffprobeExe = ResolveFfprobeExe(ffmpegExe);
		if (string.IsNullOrWhiteSpace(ffprobeExe) || !File.Exists(ffprobeExe))
			return VideoPreviewFallbackFrameRate;

		var psi = new ProcessStartInfo(ffprobeExe)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
		};

		void Add(string arg) => psi.ArgumentList.Add(arg);
		Add("-v");
		Add("error");
		Add("-select_streams");
		Add("v:0");
		Add("-show_entries");
		Add("stream=avg_frame_rate,r_frame_rate");
		Add("-of");
		Add("default=noprint_wrappers=1");
		Add(inputPath);

		AppendFfmpegLogTail("ffprobe " + string.Join(" ", psi.ArgumentList.Select(QuoteArgForLog)));

		try
		{
			using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
			if (!process.Start())
				return VideoPreviewFallbackFrameRate;

			var stdoutTask = process.StandardOutput.ReadToEndAsync();
			var stderrTask = process.StandardError.ReadToEndAsync();

			try
			{
				await process.WaitForExitAsync(ct).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
				throw;
			}

			var stdout = await stdoutTask.ConfigureAwait(false);
			var stderr = await stderrTask.ConfigureAwait(false);
			AppendFfmpegLogTail(stderr);

			if (process.ExitCode != 0)
				return VideoPreviewFallbackFrameRate;

			var probed = ParseFfprobeFrameRate(stdout);
			return IsUsableVideoFrameRate(probed) ? probed : VideoPreviewFallbackFrameRate;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			CrashLog.Write("Video frame-rate probe failed", ex);
			return VideoPreviewFallbackFrameRate;
		}
	}

	private static double ParseFfprobeFrameRate(string output)
	{
		var values = output.Replace("\r\n", "\n").Replace('\r', '\n')
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(line => line.Contains('=') ? line[(line.IndexOf('=') + 1)..] : line)
			.Select(ParseFrameRateValue)
			.Where(IsUsableVideoFrameRate)
			.ToList();

		return values.FirstOrDefault(VideoPreviewFallbackFrameRate);
	}

	private static double ParseFrameRateValue(string value)
	{
		value = value.Trim();
		if (string.IsNullOrWhiteSpace(value) || value == "0/0")
			return 0;

		var slash = value.IndexOf('/');
		if (slash > 0)
		{
			if (double.TryParse(value[..slash], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num) &&
				double.TryParse(value[(slash + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var den) &&
				den != 0)
			{
				return num / den;
			}
		}

		return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fps)
			? fps
			: 0;
	}

	private static bool IsUsableVideoFrameRate(double fps)
	{
		return !double.IsNaN(fps) && !double.IsInfinity(fps) && fps >= 1.0 && fps <= 240.0;
	}

	private void AppendFfmpegLogTail(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return;

		lock (_ffmpegLogTail)
		{
			foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
			{
				if (string.IsNullOrWhiteSpace(line))
					continue;
				if (_ffmpegLogTail.Count > 80)
					_ffmpegLogTail.Dequeue();
				_ffmpegLogTail.Enqueue(line.Trim());
			}
		}
	}

	private static string ShortenLog(string? text, int maxChars)
	{
		if (string.IsNullOrWhiteSpace(text))
			return string.Empty;
		text = text.Trim();
		return text.Length <= maxChars ? text : text[^maxChars..];
	}

	private enum VideoContainer
	{
		Unknown,
		Bink,
		Other
	}

	private static VideoContainer DetectVideoContainer(string path, byte[]? data)
	{
		if (LooksLikeBink(data))
			return VideoContainer.Bink;

		try
		{
			if (File.Exists(path))
			{
				Span<byte> header = stackalloc byte[16];
				using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				var read = fs.Read(header);
				if (LooksLikeBink(header[..read]))
					return VideoContainer.Bink;
			}
		}
		catch
		{
		}

		var ext = Path.GetExtension(path);
		if (ext.Equals(".bik", StringComparison.OrdinalIgnoreCase) ||
			ext.Equals(".bk2", StringComparison.OrdinalIgnoreCase) ||
			ext.Equals(".bik2", StringComparison.OrdinalIgnoreCase))
			return VideoContainer.Bink;

		return VideoContainer.Other;
	}

	private static string GetPreferredMediaExtension(string fileName, byte[]? data)
	{
		if (LooksLikeBink(data))
		{
			if (data!.Length >= 3 && data[0] == (byte)'K' && data[1] == (byte)'B' && data[2] == (byte)'2')
				return ".bk2";
			return ".bik";
		}

		var ext = Path.GetExtension(fileName);
		return string.IsNullOrWhiteSpace(ext) ? ".bin" : ext;
	}

	private static bool LooksLikeBink(byte[]? data)
	{
		if (data == null || data.Length < 3)
			return false;
		return (data[0] == (byte)'B' && data[1] == (byte)'I' && data[2] == (byte)'K') ||
			   (data[0] == (byte)'K' && data[1] == (byte)'B' && data[2] == (byte)'2');
	}

	private static bool LooksLikeBink(ReadOnlySpan<byte> data)
	{
		if (data.Length < 3)
			return false;
		return (data[0] == (byte)'B' && data[1] == (byte)'I' && data[2] == (byte)'K') ||
			   (data[0] == (byte)'K' && data[1] == (byte)'B' && data[2] == (byte)'2');
	}

	private async Task SetupAudioBackendAsync(AssetItem asset, string filePathOrName, byte[]? archiveData, CancellationToken ct)
	{
		var ext = Path.GetExtension(filePathOrName).ToLowerInvariant();

		WaveStream reader;

		if (archiveData != null)
		{
			var ms = new MemoryStream(archiveData, writable: false);

			if (ext == ".wav")
				reader = new WaveFileReader(ms);
			else if (ext == ".mp3")
				reader = new Mp3FileReader(ms);
			else
			{

				var tmp = GetOrCreateTempMediaSync(asset.FullPath, filePathOrName, archiveData);
				reader = await _sta.InvokeAsync(() => (WaveStream)new AudioFileReader(tmp), ct);
			}
		}
		else
		{
			var fullPath = Path.GetFullPath(filePathOrName);
			if (!File.Exists(fullPath))
			{
				await ShowPlaybackUnavailableAsync($"Audio file not found: {fullPath}").ConfigureAwait(false);
				return;
			}

			if (ext == ".wav")
			{
				reader = new WaveFileReader(fullPath);
			}
			else if (ext == ".mp3")
			{
				reader = await _sta.InvokeAsync(() => (WaveStream)new Mp3FileReader(fullPath), ct);
			}
			else
			{
				reader = await _sta.InvokeAsync(() => (WaveStream)new AudioFileReader(fullPath), ct);
			}
		}

		if (reader.TotalTime == TimeSpan.Zero)
		{
			try
			{
				reader.Dispose();

				if (archiveData != null)
				{
					var tmp = GetOrCreateTempMediaSync(asset.FullPath, filePathOrName, archiveData);
					reader = await _sta.InvokeAsync(() => (WaveStream)new AudioFileReader(tmp), ct);
				}
				else
				{
					var fullPath = Path.GetFullPath(filePathOrName);
					reader = await _sta.InvokeAsync(() => (WaveStream)new AudioFileReader(fullPath), ct);
				}
			}
			catch
			{
			}
		}

		StopAudioBackend();
		_audioStream = reader;
		_audioOutput = new WaveOutEvent
		{
			DesiredLatency = 200,
			NumberOfBuffers = 4
		};
		_audioOutput.Init(_audioStream);
		_audioOutput.Volume = (float)MediaVolumeSlider.Value;
	}

	private async Task CloseVideoBackendAsync(CancellationToken ct = default)
	{
		try
		{
			await _videoGate.WaitAsync(ct).ConfigureAwait(false);
			try
			{
				await Dispatcher.InvokeAsync(() => CloseVideoOnUi(), DispatcherPriority.Send, ct);
			}
			finally
			{
				_videoGate.Release();
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			CrashLog.Write("CloseVideoBackendAsync failed", ex);
		}
	}

	private void CloseVideoOnUi()
	{
		_mediaTimer.Stop();
		_isVideoFramePlaying = false;
		_videoFrameClock.Reset();
		_videoFrameBasePosition = TimeSpan.Zero;
		_currentVideoFrameIndex = -1;
		_currentVideoFrames = null;
		_videoDuration = TimeSpan.Zero;
		TryClearMediaSource(VideoPreview);
		try { VideoPreview.Tag = null; } catch { }
	}

	private async Task OpenVideoBackendAsync(string cacheKey, string pathOnDisk, CancellationToken ct)
	{
		if (!File.Exists(pathOnDisk))
		{
			await ShowPlaybackUnavailableAsync($"Video file not found: {pathOnDisk}").ConfigureAwait(false);
			return;
		}

		await _videoGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			ct.ThrowIfCancellationRequested();

			var seq = Interlocked.Increment(ref _videoSeqCounter);
			_activeVideoSeq = seq;

			await Dispatcher.InvokeAsync(() =>
			{
				VideoPreview.Tag = seq;
				VideoPreview.Visibility = Visibility.Visible;
				TryClearMediaSource(VideoPreview);
				MediaVolumeSlider.IsEnabled = false;
				ShowVideoLoading("Extracting video frames…");
			}, DispatcherPriority.Send, ct);

			var cache = await GetOrCreateVideoFrameCacheAsync(cacheKey, pathOnDisk, ct).ConfigureAwait(false);

			ct.ThrowIfCancellationRequested();
			await Dispatcher.InvokeAsync(() => OpenVideoFramesOnUi(seq, cache), DispatcherPriority.Send, ct);
		}
		finally
		{
			_videoGate.Release();
		}
	}

	private void OpenVideoFramesOnUi(long seq, VideoFrameCache cache)
	{
		if (seq != _activeVideoSeq)
			return;

		if (cache.FramePaths.Count == 0)
		{
			ShowVideoError("No frames decoded");
			MediaPositionSlider.IsEnabled = false;
			return;
		}

		_currentVideoFrames = cache;
		_currentVideoFrameIndex = -1;
		_videoFrameBasePosition = TimeSpan.Zero;
		_videoFrameClock.Restart();
		_isVideoFramePlaying = true;
		_videoDuration = cache.Duration;
		_mediaTimer.Interval = TimeSpan.FromMilliseconds(
			Math.Clamp(1000.0 / Math.Max(1.0, cache.FrameRate), 4.0, 100.0));

		MediaPositionSlider.IsEnabled = _videoDuration > TimeSpan.Zero;
		MediaPositionSlider.Maximum = Math.Max(1, _videoDuration.TotalSeconds);
		MediaPositionSlider.Value = 0;
		MediaVolumeSlider.IsEnabled = false;
		SetVideoFrameFromPosition(TimeSpan.Zero);
		UpdateMediaTimeText();
		HideVideoOverlay();
		_mediaTimer.Start();
	}

	private void StopAudioBackend()
	{
		try
		{
			_audioOutput?.Stop();
		}
		catch { }

		try
		{
			_audioOutput?.Dispose();
		}
		catch { }

		try
		{
			_audioStream?.Dispose();
		}
		catch { }

		_audioOutput = null;
		_audioStream = null;
	}

	private async Task<string> GetOrCreateTempMediaAsync(string cacheKey, string fileName, byte[] data, CancellationToken ct)
	{
		lock (_mediaTempLock)
		{
			if (_mediaTempCache.TryGetValue(cacheKey, out var existingPath) && File.Exists(existingPath))
				return existingPath;
		}

		var ext = GetPreferredMediaExtension(fileName, data);

		string dir = Path.Combine(Path.GetTempPath(), "SyberiaDatamine", "preview");
		Directory.CreateDirectory(dir);

		string path = Path.Combine(dir, $"{Guid.NewGuid():N}{ext}");
		string writingPath = path + ".writing";

		await Task.Run(() =>
		{
			ct.ThrowIfCancellationRequested();
			File.WriteAllBytes(writingPath, data);
			ct.ThrowIfCancellationRequested();
			File.Move(writingPath, path, overwrite: false);
		}, ct).ConfigureAwait(false);

		lock (_mediaTempLock)
		{
			_mediaTempCache[cacheKey] = path;
		}
		return path;
	}

	private string GetOrCreateTempMediaSync(string cacheKey, string fileName, byte[] data)
	{
		lock (_mediaTempLock)
		{
			if (_mediaTempCache.TryGetValue(cacheKey, out var existingPath) && File.Exists(existingPath))
				return existingPath;
		}

		var ext = GetPreferredMediaExtension(fileName, data);

		string dir = Path.Combine(Path.GetTempPath(), "SyberiaDatamine", "preview");
		Directory.CreateDirectory(dir);

		string path = Path.Combine(dir, $"{Guid.NewGuid():N}{ext}");
		string writingPath = path + ".writing";
		File.WriteAllBytes(writingPath, data);
		File.Move(writingPath, path, overwrite: false);

		lock (_mediaTempLock)
		{
			_mediaTempCache[cacheKey] = path;
		}
		return path;
	}

	private void InvalidateVideoEvents()
	{
		_activeVideoSeq = Interlocked.Increment(ref _videoSeqCounter);
		try
		{
			if (Dispatcher.CheckAccess())
				VideoPreview.Tag = null;
			else
				Dispatcher.BeginInvoke(new Action(() => VideoPreview.Tag = null), DispatcherPriority.Send);
		}
		catch { }
	}

	private void StopAndHideMedia()
	{
		_ = StopAndHideMediaAsync();
	}

	private async Task StopAndHideMediaAsync()
	{
		_mediaCts?.Cancel();
		InvalidateVideoEvents();
		_mediaTimer.Stop();
		StopAudioBackend();
		await CloseVideoBackendAsync().ConfigureAwait(false);

		await Dispatcher.InvokeAsync(() =>
		{
			MediaPositionSlider.IsEnabled = false;
			MediaPositionSlider.Maximum = 1;
			MediaPositionSlider.Value = 0;
			MediaTimeText.Text = "0:00 / 0:00";
			MediaPanel.Visibility = Visibility.Collapsed;
		}, DispatcherPriority.Background);
	}

	private async Task ShutdownMediaAsync()
	{
		_previewCts?.Cancel();
		_mediaCts?.Cancel();
		InvalidateVideoEvents();
		_mediaTimer.Stop();
		StopAudioBackend();

		await CloseVideoBackendAsync().ConfigureAwait(false);

		var setupTask = _currentMediaSetupTask;
		if (setupTask != null)
		{
			try { await Task.WhenAny(setupTask, Task.Delay(1000)).ConfigureAwait(false); }
			catch { }
		}

		List<string> paths;
		List<string> frameDirs;
		lock (_mediaTempLock)
		{
			paths = _mediaTempCache.Values.ToList();
			frameDirs = _videoFrameCache.Values.Select(v => v.DirectoryPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			_mediaTempCache.Clear();
			_videoFrameCache.Clear();
		}

		foreach (var path in paths)
		{
			try { if (File.Exists(path)) File.Delete(path); } catch { }
		}

		foreach (var dir in frameDirs)
		{
			try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
		}
	}

	private void CleanupAllMediaTempFiles()
	{
		_ = ShutdownMediaAsync();
	}

	private static void TryClearMediaSource(System.Windows.Controls.Image element)
	{
		try
		{
			element.Source = null;
		}
		catch
		{
		}
	}

	private static ImageSource LoadFrozenBitmap(string path)
	{
		var bitmap = new BitmapImage();
		bitmap.BeginInit();
		bitmap.CacheOption = BitmapCacheOption.OnLoad;
		bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
		bitmap.UriSource = new Uri(path, UriKind.Absolute);
		bitmap.EndInit();
		bitmap.Freeze();
		return bitmap;
	}

	private async Task EnsureFfmpegReadyForVideoAsync(CancellationToken ct)
	{
		if (FfmpegBootstrapper.IsReady)
			return;

		WireFfmpegBanner();
		ShowFfmpegBanner("Setting up FFmpeg…", 0, "Downloading and extracting FFmpeg binaries for video playback");

		await FfmpegBootstrapper.EnsureStartedAsync(ct).ConfigureAwait(false);
	}

	private void WireFfmpegBanner()
	{
		if (_ffmpegBannerWired) return;
		_ffmpegBannerWired = true;

		FfmpegBootstrapper.ProgressChanged += p =>
		{
			Dispatcher.Invoke(() =>
			{
				var value = p.Progress ?? 0;
				var stage = string.IsNullOrWhiteSpace(p.Stage) ? "Setting up FFmpeg…" : p.Stage;
				var detail = p.Detail ?? string.Empty;
				ShowFfmpegBanner(stage, value, detail);
			});
		};

		if (FfmpegBootstrapper.IsReady)
			HideFfmpegBanner();
	}

	private void ShowFfmpegBanner(string title, double progress, string detail)
	{
		FfmpegBanner.Visibility = Visibility.Visible;
		FfmpegBannerText.Text = title;
		FfmpegBannerDetail.Text = detail;
		FfmpegBannerProgress.Value = Math.Clamp(progress, 0, 1);
		FfmpegBannerRetry.Visibility = Visibility.Visible;

		if (FfmpegBootstrapper.IsReady)
			HideFfmpegBanner();
	}

	private void HideFfmpegBanner()
	{
		FfmpegBanner.Visibility = Visibility.Collapsed;
		FfmpegBannerText.Text = string.Empty;
		FfmpegBannerDetail.Text = string.Empty;
		FfmpegBannerProgress.Value = 0;
	}

	private async void FfmpegBannerRetry_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			ShowFfmpegBanner("Retrying…", 0, "");
			await FfmpegBootstrapper.EnsureStartedAsync();
		}
		catch (Exception ex)
		{
			ShowFfmpegBanner("FFmpeg setup failed", 0, ex.Message);
		}
	}

	private TimeSpan GetCurrentVideoFramePosition()
	{
		if (_currentVideoFrames == null)
			return TimeSpan.Zero;

		var pos = _videoFrameBasePosition;
		if (_isVideoFramePlaying)
			pos += _videoFrameClock.Elapsed;

		if (_videoDuration > TimeSpan.Zero && pos >= _videoDuration)
		{
			pos = _videoDuration;
			_isVideoFramePlaying = false;
			_videoFrameClock.Reset();
		}

		return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
	}

	private void SetVideoFrameFromPosition(TimeSpan position)
	{
		var cache = _currentVideoFrames;
		if (cache == null || cache.FramePaths.Count == 0)
			return;

		if (position < TimeSpan.Zero)
			position = TimeSpan.Zero;
		if (_videoDuration > TimeSpan.Zero && position > _videoDuration)
			position = _videoDuration;

		var index = (int)Math.Floor(position.TotalSeconds * cache.FrameRate);
		if (index >= cache.FramePaths.Count)
			index = cache.FramePaths.Count - 1;
		if (index < 0)
			index = 0;

		if (index == _currentVideoFrameIndex && VideoPreview.Source != null)
			return;

		_currentVideoFrameIndex = index;
		try
		{
			VideoPreview.Source = LoadFrozenBitmap(cache.FramePaths[index]);
		}
		catch (Exception ex)
		{
			CrashLog.Write("Loading decoded video frame failed", ex);
			ShowVideoError("Frame load failed");
		}
	}

	private void UpdateVideoFramePlayback()
	{
		var pos = GetCurrentVideoFramePosition();
		SetVideoFrameFromPosition(pos);
		MediaPositionSlider.Maximum = Math.Max(1, _videoDuration.TotalSeconds);
		MediaPositionSlider.Value = Math.Clamp(pos.TotalSeconds, 0, MediaPositionSlider.Maximum);
		UpdateMediaTimeText();

		if (!_isVideoFramePlaying)
			_mediaTimer.Stop();
	}

	private void MediaTimer_Tick(object? sender, EventArgs e)
	{
		if (_isScrubbing)
			return;

		try
		{
			if (_isAudioActive)
			{
				if (_audioStream == null) return;
				MediaPositionSlider.Maximum = Math.Max(1, _audioStream.TotalTime.TotalSeconds);
				MediaPositionSlider.Value = Math.Clamp(_audioStream.CurrentTime.TotalSeconds, 0, MediaPositionSlider.Maximum);
				UpdateMediaTimeText();
			}
			else
			{
				if (VideoPreview.Tag is not long s || s != _activeVideoSeq) return;
				UpdateVideoFramePlayback();
			}
		}
		catch (Exception ex)
		{
			_mediaTimer.Stop();
			CrashLog.Write("Media timer failed", ex);
		}
	}

	private void UpdateMediaTimeText()
	{
		try
		{
			TimeSpan pos;
			TimeSpan dur;

			if (_isAudioActive && _audioStream != null)
			{
				pos = _audioStream.CurrentTime;
				dur = _audioStream.TotalTime;
			}
			else
			{
				pos = VideoPreview.Tag is long s && s == _activeVideoSeq ? GetCurrentVideoFramePosition() : TimeSpan.Zero;
				dur = _videoDuration;
			}
			MediaTimeText.Text = $"{FormatTime(pos)} / {FormatTime(dur)}";
		}
		catch
		{
			MediaTimeText.Text = "00:00 / 00:00";
		}
	}

	private static string FormatTime(TimeSpan t)
	{
		if (t < TimeSpan.Zero)
			t = TimeSpan.Zero;

		return t.TotalHours >= 1
			? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
			: $"{t.Minutes:00}:{t.Seconds:00}";
	}

	private void MediaPlay_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (_isAudioActive)
			{
				_audioOutput?.Play();
				_mediaTimer.Start();
				return;
			}

			if (VideoPreview.Tag is long s && s == _activeVideoSeq && _currentVideoFrames != null)
			{
				var pos = GetCurrentVideoFramePosition();
				if (_videoDuration > TimeSpan.Zero && pos >= _videoDuration)
					pos = TimeSpan.Zero;

				_videoFrameBasePosition = pos;
				_videoFrameClock.Restart();
				_isVideoFramePlaying = true;
				_mediaTimer.Start();
			}
		}
		catch (Exception ex)
		{
			CrashLog.Write("Media play failed", ex);
		}
	}

	private void MediaPause_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (_isAudioActive)
			{
				_audioOutput?.Pause();
			}
			else if (VideoPreview.Tag is long s && s == _activeVideoSeq)
			{
				_videoFrameBasePosition = GetCurrentVideoFramePosition();
				_videoFrameClock.Reset();
				_isVideoFramePlaying = false;
			}
		}
		catch (Exception ex)
		{
			CrashLog.Write("Media pause failed", ex);
		}
		finally
		{
			_mediaTimer.Stop();
			UpdateMediaTimeText();
		}
	}

	private void MediaStop_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (_isAudioActive)
			{
				_audioOutput?.Stop();
				if (_audioStream != null) _audioStream.CurrentTime = TimeSpan.Zero;
			}
			else if (VideoPreview.Tag is long s && s == _activeVideoSeq)
			{
				_videoFrameBasePosition = TimeSpan.Zero;
				_videoFrameClock.Reset();
				_isVideoFramePlaying = false;
				SetVideoFrameFromPosition(TimeSpan.Zero);
			}
			MediaPositionSlider.Value = 0;
			UpdateMediaTimeText();
		}
		catch (Exception ex)
		{
			CrashLog.Write("Media stop failed", ex);
		}
		finally
		{
			_mediaTimer.Stop();
		}
	}

	private void MediaPositionSlider_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		_isScrubbing = true;
	}

	private void MediaPositionSlider_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		_isScrubbing = false;
		SeekToSliderPosition();
	}

	private void MediaPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_isScrubbing)
			UpdateMediaTimeText();
	}

	private void SeekToSliderPosition()
	{
		try
		{
			var seconds = MediaPositionSlider.Value;
			if (_isAudioActive)
			{
				if (_audioStream == null) return;
				_audioStream.CurrentTime = TimeSpan.FromSeconds(seconds);
			}
			else if (VideoPreview.Tag is long s && s == _activeVideoSeq)
			{
				var target = TimeSpan.FromSeconds(seconds);
				if (_videoDuration > TimeSpan.Zero && target > _videoDuration)
					target = _videoDuration;

				_videoFrameBasePosition = target;
				if (_isVideoFramePlaying)
					_videoFrameClock.Restart();
				else
					_videoFrameClock.Reset();

				SetVideoFrameFromPosition(target);
			}
			UpdateMediaTimeText();
		}
		catch (Exception ex)
		{
			CrashLog.Write("Media seek failed", ex);
		}
	}

	private void MediaVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		var vol = MediaVolumeSlider.Value;
		if (_audioOutput != null) _audioOutput.Volume = (float)vol;
	}

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		UpdateAssetListFilter();
	}

	private void UpdateAssetListFilter()
	{
		if (_allAssets == null || _assetItems == null)
			return;

		var searchText = SearchBox.Text.ToLowerInvariant();
		var typeFilter = TypeFilterCombo?.SelectedItem as string ?? "All";

		var filtered = new List<AssetItem>(_allAssets.Count);
		foreach (var asset in _allAssets)
		{
			if (asset.HideInAssetGrid)
				continue;

			if (!string.IsNullOrWhiteSpace(searchText) && !asset.Name.ToLowerInvariant().Contains(searchText))
				continue;

			if (_hideUnknownFiles && asset.Kind == AssetKind.Unknown)
				continue;

			if (typeFilter != "All")
			{
				var matched = typeFilter switch
				{
					"Texture" => asset.Kind == AssetKind.Texture2D,
					"Video" => asset.Kind == AssetKind.Video,
					"Audio" => asset.Kind == AssetKind.Audio,
					"NMO/CMO" => asset.Kind == AssetKind.NemoFile,
					"3D / CMO" => asset.Kind == AssetKind.CmoObject,
					"Mesh" => asset.Kind == AssetKind.CmoObject && asset.CmoObjectRef?.ClassId is 32 or 53,
					"Material" => asset.Kind == AssetKind.CmoObject && asset.CmoObjectRef?.ClassId == 30,
					"Behavior" => asset.Kind == AssetKind.CmoObject && asset.CmoObjectRef?.ClassId == 8,
					"Sound" => asset.Kind == AssetKind.CmoObject && asset.CmoObjectRef?.ClassId is 24 or 25 or 26,
					"Animation" => asset.Kind == AssetKind.CmoObject && asset.CmoObjectRef?.ClassId is 15 or 16 or 18,
					"DataArray" => asset.Kind == AssetKind.CmoObject && asset.CmoObjectRef?.ClassId == 52,
					"BodyPart" => asset.Kind == AssetKind.CmoObject && asset.CmoObjectRef?.ClassId == 42,
					"3D Object" => asset.Kind == AssetKind.CmoObject && asset.CmoObjectRef?.ClassId is 40 or 41,
					"Archive" => asset.Kind == AssetKind.Archive,
					"Binary" => asset.Kind == AssetKind.Binary,
					"Unknown" => asset.Kind == AssetKind.Unknown,
					_ => true
				};
				if (!matched) continue;
			}

			filtered.Add(asset);
		}

		_assetItems.ReplaceAll(filtered);

		UpdateStatus($"Showing {_assetItems.Count} of {_allAssets.Count} assets");
	}

	private FileTreeItem? FindTreeItemByPath(string fullPath)
	{
		foreach (var root in _fileItems)
		{
			var found = FindTreeItemByPathRecursive(root, fullPath);
			if (found != null)
				return found;
		}
		return null;
	}

	private static FileTreeItem? FindTreeItemByPathRecursive(FileTreeItem node, string fullPath)
	{
		if (node.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
			return node;

		foreach (var child in node.Children)
		{
			var found = FindTreeItemByPathRecursive(child, fullPath);
			if (found != null)
				return found;
		}

		return null;
	}

	private void IndexExternalTextureSources(string folderPath)
	{
		int archiveCount = 0;
		int textureEntries = 0;

		try
		{
			foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
			{
				try
				{
					var (kind, loaderId) = AssetLoaderRegistry.Classify(file);
					if (kind != AssetKind.Archive || !string.Equals(loaderId, "vxbg", StringComparison.OrdinalIgnoreCase))
						continue;

					var src = new VxbgArchiveSource(file);
					_archiveSources.Add(src);
					archiveCount++;

					foreach (var entry in src.Entries)
					{
						var (entryKind, _) = AssetLoaderRegistry.Classify(entry.Name);
						if (entryKind != AssetKind.Texture2D)
							continue;

						textureEntries++;
						var fullName = entry.Name;
						var baseName = Path.GetFileNameWithoutExtension(fullName);

						if (!_externalTextureCache.ContainsKey(fullName))
							_externalTextureCache[fullName] = entry;
						if (!_externalTextureCache.ContainsKey(baseName))
							_externalTextureCache[baseName] = entry;
						if (!_externalTextureCache.ContainsKey(baseName + ".tga"))
							_externalTextureCache[baseName + ".tga"] = entry;
						if (!_externalTextureCache.ContainsKey(baseName + ".jpg"))
							_externalTextureCache[baseName + ".jpg"] = entry;
					}
				}
				catch
				{

				}
			}

			UpdateStatus($"Indexed {textureEntries:N0} textures from {archiveCount:N0} archive(s)");
		}
		catch (Exception ex)
		{
			UpdateStatus($"Texture source indexing failed: {ex.Message}");
		}
	}

	private string FormatFileSize(long bytes)
	{
		string[] sizes = { "B", "KB", "MB", "GB" };
		double len = bytes;
		int order = 0;
		while (len >= 1024 && order < sizes.Length - 1)
		{
			order++;
			len = len / 1024;
		}
		return $"{len:0.##} {sizes[order]}";
	}

	private void UpdateStatus(string message)
	{
		StatusText.Text = message;
	}

	private void ShowVideoLoading(string message = "Loading video…")
	{
		MediaPanel.Visibility = Visibility.Visible;
		VideoOverlay.Visibility = Visibility.Visible;
		VideoOverlayText.Text = message;
		VideoOverlayProgress.IsIndeterminate = true;
	}

	private void ShowVideoError(string message)
	{
		MediaPanel.Visibility = Visibility.Visible;
		VideoOverlay.Visibility = Visibility.Visible;
		VideoOverlayText.Text = message;
		VideoOverlayProgress.IsIndeterminate = false;
	}

	private void HideVideoOverlay()
	{
		VideoOverlay.Visibility = Visibility.Collapsed;
	}

	private void ShowMesh3D(
		CkMesh mesh,
		System.Numerics.Vector3[]? externalVertices = null,
		ImageSource? textureImage = null,
		IReadOnlyList<ResolvedMaterialInfo>? materials = null)
	{
		var vertices = externalVertices ?? mesh.Vertices;
		var sourceIndices = mesh.Indices;

		if (vertices == null || sourceIndices == null || vertices.Length == 0 || sourceIndices.Length < 3)
			return;

		if (_currentMeshModel != null)
		{
			MeshModelGroup.Children.Remove(_currentMeshModel);
			_currentMeshModel = null;
		}

		for (int i = MeshModelGroup.Children.Count - 1; i >= 0; i--)
		{
			if (MeshModelGroup.Children[i] is Light)
				MeshModelGroup.Children.RemoveAt(i);
		}

		MeshModelGroup.Children.Add(new AmbientLight(
			Color.FromRgb(90, 90, 90)));

		MeshModelGroup.Children.Add(new DirectionalLight(
			Color.FromRgb(240, 240, 240),
			new Vector3D(-0.45, -0.65, -1.0)));

		MeshModelGroup.Children.Add(new DirectionalLight(
			Color.FromRgb(120, 120, 120),
			new Vector3D(0.6, 0.35, 0.8)));

		var geom = new MeshGeometry3D();

		var positions = new Point3DCollection(vertices.Length);
		foreach (var v in vertices)
		{

			positions.Add(new Point3D(v.X, v.Z, -v.Y));
		}

		geom.Positions = positions;

		var indices = new Int32Collection(sourceIndices.Length);
		var indicesByMaterialSlot = new Dictionary<int, Int32Collection>();

		for (int i = 0, faceIndex = 0; i + 2 < sourceIndices.Length; i += 3, faceIndex++)
		{
			int i0 = sourceIndices[i];
			int i1 = sourceIndices[i + 1];
			int i2 = sourceIndices[i + 2];

			if (i0 >= 0 && i0 < vertices.Length &&
				i1 >= 0 && i1 < vertices.Length &&
				i2 >= 0 && i2 < vertices.Length)
			{
				indices.Add(i0);
				indices.Add(i2);
				indices.Add(i1);

				int materialSlot = mesh.FaceMaterialIndices != null && faceIndex < mesh.FaceMaterialIndices.Length
					? mesh.FaceMaterialIndices[faceIndex]
					: -1;
				if (!indicesByMaterialSlot.TryGetValue(materialSlot, out var slotIndices))
				{
					slotIndices = new Int32Collection();
					indicesByMaterialSlot[materialSlot] = slotIndices;
				}
				slotIndices.Add(i0);
				slotIndices.Add(i2);
				slotIndices.Add(i1);
			}
		}

		if (indices.Count < 3)
			return;

		geom.TriangleIndices = indices;

		bool hasUVs = mesh.UVs != null && mesh.UVs.Length == vertices.Length;

		if (hasUVs)
		{
			var uvs = new PointCollection(mesh.UVs!.Length);

			foreach (var uv in mesh.UVs)
			{

				uvs.Add(new System.Windows.Point(uv.U, 1.0 - uv.V));
			}

			geom.TextureCoordinates = uvs;
		}

		if (mesh.Normals != null && mesh.Normals.Length == vertices.Length)
		{
			var normals = new Vector3DCollection(mesh.Normals.Length);

			foreach (var n in mesh.Normals)
			{

				var nn = new Vector3D(n.X, n.Z, -n.Y);

				if (nn.LengthSquared > 1e-12)
					nn.Normalize();
				else
					nn = new Vector3D(0, 0, 1);

				normals.Add(nn);
			}

			geom.Normals = normals;
		}
		else
		{
			geom.Normals = BuildSmoothNormalsForMesh(positions, indices);
		}

		MaterialGroup BuildFrontMaterial(ImageSource? image)
		{
			var group = new MaterialGroup();
			if (image != null && hasUVs)
			{
				var brush = new ImageBrush(image)
				{
					Stretch = Stretch.Fill
				};
				group.Children.Add(new DiffuseMaterial(brush));

				group.Children.Add(new EmissiveMaterial(
					new SolidColorBrush(Color.FromArgb(0x40, 0x88, 0xCC, 0xFF))));
			}
			else
			{
				group.Children.Add(new DiffuseMaterial(
					new SolidColorBrush(Color.FromRgb(0x88, 0xCC, 0xFF))));
			}

			group.Children.Add(new EmissiveMaterial(
				new SolidColorBrush(Color.FromRgb(0x20, 0x2A, 0x34))));

			group.Children.Add(new SpecularMaterial(
				new SolidColorBrush(Color.FromRgb(0xDD, 0xEE, 0xFF)), 35));
			return group;
		}

		ImageSource? TextureForSlot(int slot)
		{
			if (materials != null && slot >= 0 && slot < materials.Count)
				return materials[slot].TextureImage ?? textureImage;
			return textureImage;
		}

		var modelRoot = new Model3DGroup();
		var splitByMaterial = materials != null && materials.Count > 0 && mesh.FaceMaterialIndices != null && indicesByMaterialSlot.Count > 1;
		if (splitByMaterial)
		{
			foreach (var kvp in indicesByMaterialSlot.OrderBy(k => k.Key))
			{
				if (kvp.Value.Count < 3)
					continue;

				var slotGeom = new MeshGeometry3D
				{
					Positions = positions,
					TriangleIndices = kvp.Value,
					Normals = geom.Normals,
					TextureCoordinates = geom.TextureCoordinates
				};
				var slotTexture = TextureForSlot(kvp.Key);
				modelRoot.Children.Add(new GeometryModel3D(slotGeom, BuildFrontMaterial(slotTexture))
				{
					BackMaterial = BuildFrontMaterial(slotTexture)
				});
			}
		}
		else
		{
			modelRoot.Children.Add(new GeometryModel3D(geom, BuildFrontMaterial(textureImage))
			{
				BackMaterial = BuildFrontMaterial(textureImage)
			});
		}

		MeshModelGroup.Children.Add(modelRoot);
		_currentMeshModel = modelRoot;

		var minX = double.MaxValue;
		var minY = double.MaxValue;
		var minZ = double.MaxValue;

		var maxX = double.MinValue;
		var maxY = double.MinValue;
		var maxZ = double.MinValue;

		foreach (var p in positions)
		{
			if (p.X < minX) minX = p.X;
			if (p.X > maxX) maxX = p.X;

			if (p.Y < minY) minY = p.Y;
			if (p.Y > maxY) maxY = p.Y;

			if (p.Z < minZ) minZ = p.Z;
			if (p.Z > maxZ) maxZ = p.Z;
		}

		var cx = (minX + maxX) / 2.0;
		var cy = (minY + maxY) / 2.0;
		var cz = (minZ + maxZ) / 2.0;

		var dx = maxX - minX;
		var dy = maxY - minY;
		var dz = maxZ - minZ;

		var radius = Math.Max(Math.Max(dx, dy), dz) * 0.75;
		if (radius < 0.001)
			radius = 1.0;

		_meshCameraTarget = new Point3D(cx, cy, cz);
		_meshCameraRadius = radius * 2.2;
		_meshCameraYaw = 0.0;
		_meshCameraPitch = 0.0;

		var maxDim = Math.Max(Math.Max(dx, dy), dz);
		var flatThreshold = Math.Max(0.0001, maxDim * 0.03);
		var isFlatX = dx <= flatThreshold && maxDim > flatThreshold;
		var isFlatY = dy <= flatThreshold && maxDim > flatThreshold;
		var isFlatZ = dz <= flatThreshold && maxDim > flatThreshold;

		if (isFlatY)
		{

			_meshCameraYaw = 0.0;
			_meshCameraPitch = 1.15;
		}
		else if (isFlatX)
		{
			_meshCameraYaw = Math.PI / 2.0;
			_meshCameraPitch = 0.15;
		}
		else if (isFlatZ)
		{
			_meshCameraYaw = 0.0;
			_meshCameraPitch = 0.15;
		}

		if (isFlatX || isFlatY || isFlatZ || vertices.Length <= 8)
		{
			var overlayMaterial = new MaterialGroup();
			overlayMaterial.Children.Add(new DiffuseMaterial(
				new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xB0, 0x30))));
			overlayMaterial.Children.Add(new EmissiveMaterial(
				new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xB0, 0x30))));

			modelRoot.Children.Add(new GeometryModel3D(geom, overlayMaterial)
			{
				BackMaterial = overlayMaterial
			});
		}

		UpdateMeshCamera();

		var flatNote = isFlatX || isFlatY || isFlatZ ? "   flat/billboard view" : "";
		MeshInfoText.Text =
			$"{mesh.Name}   {vertices.Length} verts  {mesh.FaceCount} tris   " +
			$"bounds [{minX:F2},{minY:F2},{minZ:F2}] – [{maxX:F2},{maxY:F2},{maxZ:F2}]" + flatNote;

		MeshViewportBorder.Visibility = Visibility.Visible;

		static Vector3DCollection BuildSmoothNormalsForMesh(
			Point3DCollection positions,
			Int32Collection indices)
		{
			var accumulated = new Vector3D[positions.Count];

			for (int i = 0; i + 2 < indices.Count; i += 3)
			{
				int i0 = indices[i];
				int i1 = indices[i + 1];
				int i2 = indices[i + 2];

				if (i0 < 0 || i1 < 0 || i2 < 0 ||
					i0 >= positions.Count ||
					i1 >= positions.Count ||
					i2 >= positions.Count)
					continue;

				var p0 = positions[i0];
				var p1 = positions[i1];
				var p2 = positions[i2];

				var edge1 = p1 - p0;
				var edge2 = p2 - p0;

				var normal = Vector3D.CrossProduct(edge1, edge2);

				if (normal.LengthSquared > 1e-12)
					normal.Normalize();
				else
					continue;

				accumulated[i0] += normal;
				accumulated[i1] += normal;
				accumulated[i2] += normal;
			}

			var result = new Vector3DCollection(positions.Count);

			foreach (var n0 in accumulated)
			{
				var n = n0;

				if (n.LengthSquared > 1e-12)
					n.Normalize();
				else
					n = new Vector3D(0, 0, 1);

				result.Add(n);
			}

			return result;
		}
	}

	private void Clear3DViewport()
	{
		if (_currentMeshModel != null)
		{
			MeshModelGroup.Children.Remove(_currentMeshModel);
			_currentMeshModel = null;
		}
		MeshViewportBorder.Visibility = Visibility.Collapsed;
	}

	private void UpdateMeshCamera()
	{

		double x = _meshCameraTarget.X + _meshCameraRadius * Math.Cos(_meshCameraPitch) * Math.Sin(_meshCameraYaw);
		double y = _meshCameraTarget.Y + _meshCameraRadius * Math.Sin(_meshCameraPitch);
		double z = _meshCameraTarget.Z + _meshCameraRadius * Math.Cos(_meshCameraPitch) * Math.Cos(_meshCameraYaw);

		MeshCamera.Position = new Point3D(x, y, z);
		var lookDir = new Vector3D(
			_meshCameraTarget.X - x,
			_meshCameraTarget.Y - y,
			_meshCameraTarget.Z - z);
		MeshCamera.LookDirection = lookDir;

		var up = new Vector3D(0, 1, 0);
		if (lookDir.LengthSquared > 1e-12)
		{
			var n = lookDir;
			n.Normalize();
			if (Math.Abs(Vector3D.DotProduct(n, up)) > 0.96)
				up = new Vector3D(0, 0, -1);
		}
		MeshCamera.UpDirection = up;
	}

	private void MeshViewport_MouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.LeftButton == MouseButtonState.Pressed)
		{
			_meshDragStart = e.GetPosition(MeshViewport3D);
			_meshDragging = true;
			MeshViewport3D.CaptureMouse();
		}
	}

	private void MeshViewport_MouseMove(object sender, MouseEventArgs e)
	{
		if (!_meshDragging) return;

		var pos = e.GetPosition(MeshViewport3D);
		var dx = pos.X - _meshDragStart.X;
		var dy = pos.Y - _meshDragStart.Y;
		_meshDragStart = pos;

		_meshCameraYaw -= dx * 0.01;
		_meshCameraPitch = Math.Clamp(_meshCameraPitch + dy * 0.01, -1.4, 1.4);
		UpdateMeshCamera();
	}

	private void MeshViewport_MouseUp(object sender, MouseButtonEventArgs e)
	{
		_meshDragging = false;
		MeshViewport3D.ReleaseMouseCapture();
	}

	private void MeshViewport_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		_meshCameraRadius *= e.Delta > 0 ? 0.85 : 1.18;
		_meshCameraRadius = Math.Clamp(_meshCameraRadius, 0.01, 100_000);
		UpdateMeshCamera();
	}

	private static Vector3DCollection BuildSmoothNormals(
		Point3DCollection positions,
		Int32Collection indices)
	{
		var accum = new Vector3D[positions.Count];

		for (int i = 0; i + 2 < indices.Count; i += 3)
		{
			int i0 = indices[i];
			int i1 = indices[i + 1];
			int i2 = indices[i + 2];

			if (i0 < 0 || i1 < 0 || i2 < 0 ||
				i0 >= positions.Count || i1 >= positions.Count || i2 >= positions.Count)
				continue;

			var p0 = positions[i0];
			var p1 = positions[i1];
			var p2 = positions[i2];

			var a = p1 - p0;
			var b = p2 - p0;

			var n = Vector3D.CrossProduct(a, b);
			if (n.LengthSquared > 1e-12)
				n.Normalize();

			accum[i0] += n;
			accum[i1] += n;
			accum[i2] += n;
		}

		var normals = new Vector3DCollection(positions.Count);
		foreach (var n0 in accum)
		{
			var n = n0;
			if (n.LengthSquared < 1e-12)
				n = new Vector3D(0, 0, 1);
			else
				n.Normalize();

			normals.Add(n);
		}

		return normals;
	}

	private void EnsureMeshLights()
	{

		for (int i = MeshModelGroup.Children.Count - 1; i >= 0; i--)
		{
			if (MeshModelGroup.Children[i] is Light)
				MeshModelGroup.Children.RemoveAt(i);
		}

		MeshModelGroup.Children.Add(new AmbientLight(
			System.Windows.Media.Color.FromRgb(80, 80, 80)));

		MeshModelGroup.Children.Add(new DirectionalLight(
			System.Windows.Media.Color.FromRgb(230, 230, 230),
			new Vector3D(-0.4, -0.7, -1.0)));

		MeshModelGroup.Children.Add(new DirectionalLight(
			System.Windows.Media.Color.FromRgb(120, 120, 120),
			new Vector3D(0.6, 0.3, 0.8)));
	}
}

public class FileTreeItem
{
	public string Name { get; set; } = "";
	public string FullPath { get; set; } = "";
	public bool IsDirectory { get; set; }
	public AssetKind Kind { get; set; } = AssetKind.Unknown;
	public string LoaderId { get; set; } = "";
	public BulkCollection<FileTreeItem> Children { get; set; } = new();
}

public class NemoTreeGroup
{
	public string GroupName { get; set; } = "";
	public string CountLabel { get; set; } = "";
	public bool IsExpanded { get; set; } = true;
	public List<NemoTreeObject> Objects { get; set; } = new();
}

public class NemoTreeObject
{
	public string DisplayName { get; set; } = "";
	public string ClassName { get; set; } = "";
}

public sealed class BulkCollection<T> : ObservableCollection<T>
{

	public void AddRange(IEnumerable<T> range)
	{
		foreach (var item in range)
			Items.Add(item);
		OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
			System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
	}

	public void ReplaceAll(IEnumerable<T> range)
	{
		Items.Clear();
		foreach (var item in range)
			Items.Add(item);
		OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
			System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
	}
}

