using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DatReaderWriter.DBObjs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Converters;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    /// <summary>
    /// Panel for browsing DAT objects and placing them in the scene.
    /// </summary>
    public partial class ObjectBrowserViewModel : ViewModelBase {
        private readonly TerrainEditingContext _context;
        private readonly IDatReaderWriter _dats;
        private readonly ObjectTagIndex _tagIndex = new();
        private readonly ThumbnailRenderService? _thumbnailService;
        private readonly ThumbnailCache _thumbnailCache;
        private bool _thumbnailsReady; // Deferred: don't request thumbnails until after startup
        private uint[] _allSetupIds = Array.Empty<uint>();
        private uint[] _allGfxObjIds = Array.Empty<uint>();
        private HashSet<uint> _buildingIds = new();
        private bool _buildingIdsLoaded;
        private HashSet<uint> _sceneryIds = new();
        private bool _sceneryIdsLoaded;

        // Lookup for updating items when thumbnails arrive from the render service
        private readonly Dictionary<uint, ObjectBrowserItem> _itemLookup = new();

        [ObservableProperty] private ObservableCollection<ObjectBrowserItem> _filteredItems = new();
        [ObservableProperty] private ObjectBrowserItem? _selectedObject;
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private string _status = "Search by name or hex ID";

        [ObservableProperty] private bool _showSetups = true;
        [ObservableProperty] private bool _showGfxObjs = true;
        [ObservableProperty] private bool _showBuildingsOnly;
        [ObservableProperty] private bool _showSceneryOnly;

        /// <summary>
        /// Gets the tag index for use by the view (e.g., tooltips).
        /// </summary>
        public ObjectTagIndex TagIndex => _tagIndex;

        /// <summary>
        /// Gets the editing context (exposed for View access to StaticObjectManager).
        /// </summary>
        public TerrainEditingContext Context => _context;

        public StaticObjectManager StaticObjectManager => _context.TerrainSystem.Scene._objectManager;

        public ObjectBrowserViewModel(TerrainEditingContext context, IDatReaderWriter dats,
            ThumbnailRenderService? thumbnailService = null, ThumbnailCache? thumbnailCache = null) {
            _context = context;
            _dats = dats;
            _thumbnailService = thumbnailService;
            _thumbnailCache = thumbnailCache ?? new ThumbnailCache();

            // Subscribe to thumbnail ready events from the render service
            if (_thumbnailService != null) {
                _thumbnailService.ThumbnailReady += OnThumbnailReady;
            }

            // Load keyword tag index for name-based search
            _tagIndex.LoadFromEmbeddedResource();
            ObjectIdToTagsConverter.TagIndex = _tagIndex;

            try {
                _allSetupIds = _dats.Dats.Portal.GetAllIdsOfType<Setup>().OrderBy(id => id).ToArray();
                _allGfxObjIds = _dats.Dats.Portal.GetAllIdsOfType<GfxObj>().OrderBy(id => id).ToArray();
                Console.WriteLine($"[ObjectBrowser] Loaded {_allSetupIds.Length} Setups, {_allGfxObjIds.Length} GfxObjs");
            }
            catch (Exception ex) {
                Console.WriteLine($"[ObjectBrowser] Error loading object IDs: {ex.Message}");
            }

            ApplyFilter();

            // Scan building and scenery IDs in background to avoid blocking startup
            Task.Run(LoadBuildingIds);
            Task.Run(LoadSceneryIds);

            // Defer thumbnail loading until the app has fully initialized.
            // This avoids competing with terrain/DAT loading during startup.
            _ = Task.Run(async () => {
                await Task.Delay(2000);
                _thumbnailsReady = true;
                Console.WriteLine($"[ObjectBrowser] Thumbnail loading ready, requesting for {FilteredItems.Count} items");
                Dispatcher.UIThread.Post(() => RequestThumbnails(FilteredItems));
            });
        }

        /// <summary>
        /// Called on the GL thread when a thumbnail has been rendered.
        /// Saves to disk cache and dispatches bitmap update to the UI thread.
        /// </summary>
        private void OnThumbnailReady(uint objectId, byte[] rgbaPixels, int frameCount) {
            var width = ThumbnailRenderService.ThumbnailSize * frameCount;
            var height = ThumbnailRenderService.ThumbnailSize;

            // Save to disk cache (fire-and-forget background thread)
            // Note: We always cache now, because ThumbnailCache handles versioning via filename dimensions
            _thumbnailCache.SaveAsync(objectId, rgbaPixels, width, height);

            // Create bitmap from pixels
            var bitmap = ThumbnailCache.CreateBitmapFromRgba(rgbaPixels, width, height);

            // Dispatch to UI thread to update the item
            Dispatcher.UIThread.Post(() => {
                if (_itemLookup.TryGetValue(objectId, out var item)) {
                    item.Thumbnail = bitmap;
                }
            });
        }

        private void LoadBuildingIds() {
            try {
                var buildingIds = new HashSet<uint>();

                // Try DatCollection-level enumeration first
                var allLbiIds = _dats.Dats.GetAllIdsOfType<LandBlockInfo>().ToArray();
                Console.WriteLine($"[ObjectBrowser] Found {allLbiIds.Length} LandBlockInfo entries in DAT");

                if (allLbiIds.Length == 0) {
                    // Fallback: try Cell database directly
                    allLbiIds = _dats.Dats.Cell.GetAllIdsOfType<LandBlockInfo>().ToArray();
                    Console.WriteLine($"[ObjectBrowser] Cell fallback: {allLbiIds.Length} LandBlockInfo entries");
                }

                if (allLbiIds.Length == 0) {
                    // Last resort: brute-force scan all possible landblock IDs
                    Console.WriteLine("[ObjectBrowser] Brute-force scanning landblocks for buildings...");
                    for (uint x = 0; x < 255; x++) {
                        for (uint y = 0; y < 255; y++) {
                            var infoId = (uint)(((x << 8) | y) << 16 | 0xFFFE);
                            if (_dats.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                                foreach (var building in lbi.Buildings) {
                                    buildingIds.Add(building.ModelId);
                                }
                            }
                        }
                    }
                }
                else {
                    foreach (var infoId in allLbiIds) {
                        if (_dats.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                            foreach (var building in lbi.Buildings) {
                                buildingIds.Add(building.ModelId);
                            }
                        }
                    }
                }

                _buildingIds = buildingIds;
                _buildingIdsLoaded = true;
                Console.WriteLine($"[ObjectBrowser] Found {_buildingIds.Count} unique building model IDs");
            }
            catch (Exception ex) {
                Console.WriteLine($"[ObjectBrowser] Error scanning building IDs: {ex}");
                _buildingIdsLoaded = true; // Mark loaded so UI doesn't stay stuck on "Loading..."
            }
        }

        private void LoadSceneryIds() {
            try {
                var sceneryIds = new HashSet<uint>();
                var region = _context.TerrainSystem.Region;

                // Collect all unique Scene IDs from the Region's terrain/scene type mappings
                var sceneIds = new HashSet<uint>();
                foreach (var terrainType in region.TerrainInfo.TerrainTypes) {
                    foreach (var sceneTypeIdx in terrainType.SceneTypes) {
                        if (sceneTypeIdx < region.SceneInfo.SceneTypes.Count) {
                            var sceneType = region.SceneInfo.SceneTypes[(int)sceneTypeIdx];
                            foreach (var sceneId in sceneType.Scenes) {
                                sceneIds.Add(sceneId);
                            }
                        }
                    }
                }

                // Load each Scene and collect its object model IDs
                foreach (var sceneId in sceneIds) {
                    if (_dats.TryGet<Scene>(sceneId, out var scene)) {
                        foreach (var obj in scene.Objects) {
                            if (obj.ObjectId != 0) {
                                sceneryIds.Add(obj.ObjectId);
                            }
                        }
                    }
                }

                _sceneryIds = sceneryIds;
                _sceneryIdsLoaded = true;
                Console.WriteLine($"[ObjectBrowser] Found {_sceneryIds.Count} unique scenery model IDs from {sceneIds.Count} scenes");
            }
            catch (Exception ex) {
                Console.WriteLine($"[ObjectBrowser] Error scanning scenery IDs: {ex}");
                _sceneryIdsLoaded = true;
            }
        }

        partial void OnSearchTextChanged(string value) {
            ApplyFilter();
        }

        partial void OnShowSetupsChanged(bool value) {
            ApplyFilter();
        }

        partial void OnShowGfxObjsChanged(bool value) {
            ApplyFilter();
        }

        partial void OnShowBuildingsOnlyChanged(bool value) {
            if (value) ShowSceneryOnly = false;
            ApplyFilter();
        }

        partial void OnShowSceneryOnlyChanged(bool value) {
            if (value) ShowBuildingsOnly = false;
            ApplyFilter();
        }

        /// <summary>
        /// Returns true if the search text looks like a hex ID search (starts with 0x or is all hex chars).
        /// </summary>
        private static bool IsHexSearch(string text, out string normalizedHex) {
            normalizedHex = text.TrimStart('0', 'x', 'X').ToUpperInvariant();
            return uint.TryParse(normalizedHex, System.Globalization.NumberStyles.HexNumber, null, out _);
        }

        /// <summary>
        /// Filters a set of IDs by either hex substring match or keyword search.
        /// </summary>
        private (IEnumerable<uint> setups, IEnumerable<uint> gfxObjs) ApplySearchFilter(
            IEnumerable<uint> setups, IEnumerable<uint> gfxObjs, out string? statusSuffix) {
            statusSuffix = null;

            if (string.IsNullOrWhiteSpace(SearchText)) return (setups, gfxObjs);

            if (IsHexSearch(SearchText, out var hexSearch)) {
                // Hex ID search (existing behavior)
                return (
                    setups.Where(id => id.ToString("X8").Contains(hexSearch)),
                    gfxObjs.Where(id => id.ToString("X8").Contains(hexSearch))
                );
            }

            // Keyword search via tag index
            if (!_tagIndex.IsLoaded) {
                statusSuffix = "(keyword index not loaded)";
                return (Array.Empty<uint>(), Array.Empty<uint>());
            }

            var matchedIds = _tagIndex.Search(SearchText);
            if (matchedIds.Count == 0) {
                statusSuffix = $"No results for \"{SearchText}\"";
                return (Array.Empty<uint>(), Array.Empty<uint>());
            }

            statusSuffix = $"Found {matchedIds.Count} matches for \"{SearchText}\"";
            return (
                setups.Where(id => matchedIds.Contains(id)),
                gfxObjs.Where(id => matchedIds.Contains(id))
            );
        }

        /// <summary>
        /// Creates ObjectBrowserItem instances from filtered setup and gfxobj ID arrays.
        /// Items start with placeholder thumbnails. Call RequestThumbnails() separately
        /// to load cached images and queue missing ones for rendering.
        /// </summary>
        private ObservableCollection<ObjectBrowserItem> BuildItems(uint[] setups, uint[] gfxObjs) {
            var items = new ObservableCollection<ObjectBrowserItem>();
            _itemLookup.Clear();

            foreach (var id in setups) {
                var tags = _tagIndex.IsLoaded ? _tagIndex.GetTagString(id) : null;
                var item = new ObjectBrowserItem(id, isSetup: true, tags);
                items.Add(item);
                _itemLookup[id] = item;
            }
            foreach (var id in gfxObjs) {
                var tags = _tagIndex.IsLoaded ? _tagIndex.GetTagString(id) : null;
                var item = new ObjectBrowserItem(id, isSetup: false, tags);
                items.Add(item);
                _itemLookup[id] = item;
            }

            // Only request thumbnails after initial startup has settled.
            // This avoids queuing render work during app initialization when
            // the DAT reader is still busy with background loading.
            if (_thumbnailsReady) {
                RequestThumbnails(items);
            }

            return items;
        }

        /// <summary>
        /// For each item without a thumbnail, try the disk cache first.
        /// If not cached, queue for rendering via the ThumbnailRenderService.
        /// Runs on a background thread to avoid blocking the UI thread during disk I/O.
        /// </summary>
        private void RequestThumbnails(IEnumerable<ObjectBrowserItem> items) {
            // Snapshot the list to avoid collection modification issues
            var itemsList = items.ToList();

            // Run on background thread
            Task.Run(async () => {
                int cached = 0, queued = 0, skipped = 0;
                const int frameCount = 16; // Request 16 frames for smoother rotation
                int width = ThumbnailRenderService.ThumbnailSize * frameCount;
                int height = ThumbnailRenderService.ThumbnailSize;

                foreach (var item in itemsList) {
                    if (item.Thumbnail != null) {
                        // Check if we need to upgrade to high-res
                        if (item.Thumbnail.Size.Width < width) {
                            // Already have a thumbnail but it's low-res, queue upgrade
                            _thumbnailService?.RequestThumbnail(item.Id, item.IsSetup, frameCount);
                        }
                        skipped++;
                        continue;
                    }

                    // Pass 1: Try to load fast single-frame static thumbnail first
                    var staticBitmap = await _thumbnailCache.TryLoadCachedAsync(item.Id, ThumbnailRenderService.ThumbnailSize, ThumbnailRenderService.ThumbnailSize);
                    if (staticBitmap != null) {
                        Dispatcher.UIThread.Post(() => item.Thumbnail = staticBitmap);
                        // Queue background upgrade to animated version
                        _thumbnailService?.RequestThumbnail(item.Id, item.IsSetup, frameCount);
                        cached++;
                        continue;
                    }

                    // Pass 2: Try to load full animated sprite sheet
                    var cachedBitmap = await _thumbnailCache.TryLoadCachedAsync(item.Id, width, height);
                    if (cachedBitmap != null) {
                        Dispatcher.UIThread.Post(() => item.Thumbnail = cachedBitmap);
                        cached++;
                        continue;
                    }

                    // Not in cache: Queue single frame for immediate feedback, then upgrade
                    _thumbnailService?.RequestThumbnail(item.Id, item.IsSetup, 1);
                    _thumbnailService?.RequestThumbnail(item.Id, item.IsSetup, frameCount);
                    queued++;
                }

                if (cached > 0 || queued > 0) {
                    Console.WriteLine($"[ObjectBrowser] RequestThumbnails: {itemsList.Count} items, {cached} from cache, {queued} queued for render, {skipped} already have thumbnails");
                }
            });
        }

        private void ApplyFilter() {
            // When buildings filter is active, show building IDs directly
            if (ShowBuildingsOnly) {
                if (!_buildingIdsLoaded) {
                    Status = "Loading building list...";
                    FilteredItems = new ObservableCollection<ObjectBrowserItem>();
                    return;
                }

                IEnumerable<uint> buildingSetups = _buildingIds
                    .Where(id => (id & 0xFF000000) == 0x02000000).OrderBy(id => id);
                IEnumerable<uint> buildingGfxObjs = _buildingIds
                    .Where(id => (id & 0xFF000000) != 0x02000000).OrderBy(id => id);

                var (filtSetups, filtGfx) = ApplySearchFilter(buildingSetups, buildingGfxObjs, out var suffix);
                var sr = filtSetups.Take(100).ToArray();
                var gr = filtGfx.Take(100).ToArray();
                FilteredItems = BuildItems(sr, gr);
                Status = suffix ?? $"{_buildingIds.Count} buildings total — showing {sr.Length + gr.Length}";
                return;
            }

            // Scenery filter mode
            if (ShowSceneryOnly) {
                if (!_sceneryIdsLoaded) {
                    Status = "Loading scenery list...";
                    FilteredItems = new ObservableCollection<ObjectBrowserItem>();
                    return;
                }

                IEnumerable<uint> scenerySetups = _sceneryIds
                    .Where(id => (id & 0xFF000000) == 0x02000000).OrderBy(id => id);
                IEnumerable<uint> sceneryGfxObjs = _sceneryIds
                    .Where(id => (id & 0xFF000000) != 0x02000000).OrderBy(id => id);

                var (filtSetups, filtGfx) = ApplySearchFilter(scenerySetups, sceneryGfxObjs, out var suffix);
                var scsr = filtSetups.Take(100).ToArray();
                var scgr = filtGfx.Take(100).ToArray();
                FilteredItems = BuildItems(scsr, scgr);
                Status = suffix ?? $"{_sceneryIds.Count} scenery total — showing {scsr.Length + scgr.Length}";
                return;
            }

            // Normal mode: show all Setups / GfxObjs
            IEnumerable<uint> setups = ShowSetups ? _allSetupIds : Array.Empty<uint>();
            IEnumerable<uint> gfxObjs = ShowGfxObjs ? _allGfxObjIds : Array.Empty<uint>();

            var (fSetups, fGfx) = ApplySearchFilter(setups, gfxObjs, out var statusSuffix);
            var setupResult = fSetups.Take(100).ToArray();
            var gfxResult = fGfx.Take(100).ToArray();
            FilteredItems = BuildItems(setupResult, gfxResult);
            Status = statusSuffix ?? $"Showing {setupResult.Length} Setups, {gfxResult.Length} GfxObjs";
        }

        /// <summary>
        /// Raised when an object is selected for placement, so the editor can switch to the Selector tool.
        /// </summary>
        public event EventHandler? PlacementRequested;

        [RelayCommand]
        private void SelectForPlacement(ObjectBrowserItem item) {
            SelectedObject = item; // Update selection for preview

            _context.ObjectSelection.IsPlacementMode = true;
            _context.ObjectSelection.PlacementPreview = new StaticObject {
                Id = item.Id,
                IsSetup = item.IsSetup,
                Origin = Vector3.Zero,
                Orientation = Quaternion.Identity,
                Scale = Vector3.One
            };

            Status = $"Placing 0x{item.Id:X8} - click terrain to place, Escape to cancel";
            Console.WriteLine($"[ObjectBrowser] Selected 0x{item.Id:X8} for placement (IsSetup={item.IsSetup})");

            PlacementRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        private void ClosePreview() {
            SelectedObject = null;
        }
    }
}
