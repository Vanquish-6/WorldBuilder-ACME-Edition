# ACME WorldBuilder

World building tool for Asheron's Call. Edit terrain, dungeons, spells, skills, creature visuals, ACE weenie scalars, and more — export directly to DAT files.

**User documentation:** step-by-step setup, which editor to use, export options, and MySQL-backed tools are covered in the [User Guide](docs/USER_GUIDE.md).

---

## Downloads

- **Latest (for testing)** — [Releases → Edge (pre-release)](https://github.com/Vanquish-6/WorldBuilder-ACME-Edition/releases) — automated build from the latest commit. Download **ACME-WorldBuilderInstall-*.exe** and run it.
- **Stable** — [Releases](https://github.com/Vanquish-6/WorldBuilder-ACME-Edition/releases) — pick a versioned release (e.g. v0.1.0) when available.

Requires **Windows 10/11**, **.NET 8.0** (installer can prompt to install it). The app can check for updates in-app once installed.

---

> **Beta software.** All features are under active development. Table-driven editors (Spell, Skill, Vital, Experience, CharGen, SpellSet, Layout), **Weenie Editor**, **Monster Creator**, and **Object Debug** are powerful but still evolving. Expect rough edges, and back up your DAT files (and ACE database when using DB write features) before exporting or saving.

> **First run note:** The initial launch will be slower than usual. ACME WorldBuilder builds several caches on first run (textures, thumbnails, terrain data) that persist across sessions. Subsequent launches will be significantly faster.

---

## Features

### Terrain Editing

- **Raise / Lower** — left-click to raise, shift+left-click to lower. Adjustable brush radius (0.5–200) and strength (1–50)
- **Set Height** — paint vertices to a target height (0–255)
- **Smooth** — blend height differences across vertices
- **Texture Brush** — paint terrain textures with a shader-based WYSIWYG preview inside the brush circle
- **Bucket Fill** — flood-fill terrain textures with live preview, constrained to visible landblocks
- **Texture Palette** — visual thumbnail palette that activates with terrain tools
- **Slope Overlay** — highlight unwalkable slopes with configurable threshold (default 45°)

### Roads

- **Point placement** — click individual vertices to set road flags
- **Line drawing** — draw road lines between points
- **Remove** — clear road flags from vertices

### Objects

- **Object Browser** — browse and search the full DAT object catalog (Setups and GfxObjs)
- **Search** — by hex ID or keyword, filter by buildings or scenery
- **Thumbnail previews** — rendered on the GPU, cached to disk across sessions
- **Placement** — click terrain to place, with terrain snap
- **Move / Rotate / Delete** — drag to move, drag to rotate, Delete key to remove
- **Multi-select** — Ctrl+Click or drag a marquee box (full bounding-box testing)
- **Multi-object rotate** — rotate a group of objects around their shared center
- **Copy / Paste** — Ctrl+C / Ctrl+V, supports multi-object paste with offset
- **Right-click context menu** — copy, paste, snap to terrain, delete
- **Properties panel** — edit position (X, Y, Z), rotation (Euler angles), view landcell, snap to terrain
- **Auto height adjustment** — objects stay on the ground when terrain changes beneath them
- **Terrain snap on rotate** — objects maintain ground contact when rotated on slopes
- **Selection highlights** — spheres scale proportionally to object size

### Buildings

- **Building blueprint system** — place buildings with interior cells
- **Move / Rotate** — correctly updates interior cell VisibleCells references
- **Interior toggle** — show or hide building interiors in the viewport

### Terrain Stamps (Clone & Paste)

- **Clone tool** — drag a rectangle to capture terrain heights, textures, and objects
- **Stamp library** — stores up to 10 stamps (configurable, max 50)
- **Paste with rotation** — 0°, 90°, 180°, 270° via `[` and `]` keys
- **Edge blending** — optional smooth blending at stamp borders
- **Include objects** — optionally stamp objects along with terrain
- **Grid snapping** — snaps to the 24-unit cell grid

### Layers

- Full layer system with groups, visibility toggles, and export toggles
- Each layer tracks height, texture, road, and scenery changes independently
- Drag-and-drop reordering and nesting
- Base layer always at bottom (cannot be deleted or reordered)
- Layer compositing for export (top-to-bottom, first non-null wins)

### History & Snapshots

- Undo / redo for all operations (default 50 entries, configurable 5–10,000)
- History panel — click any entry to jump to that state
- Named snapshots that persist between sessions
- Forward entries shown dimmed in the history panel

### Dungeon Editor

- **Room palette** — browse all dungeon environment rooms with 2D cross-section previews
- **Portal snapping** — rooms auto-snap together via portal connections
- **Surface editing** — change wall and floor textures per cell
- **Static objects** — place and move objects inside dungeon cells
- **Copy template** — duplicate a dungeon from one landblock to another
- **Undo / redo** — full history support for dungeon operations

### Spell Editor *(beta)*

- Browse and search all spells by name, school, or type
- Edit spell properties: power, range, duration, components (1–8 slots)
- Icon picker with visual DAT icon browser
- Add and delete spells, save back to SpellTable

### Spell Set Editor *(beta)*

- Edit equipment set spell assignments
- Tiered spell slot management (add/remove tiers and spells per tier)

### Skill Editor *(beta)*

- Browse and filter skills by category (Combat, Magic, Other)
- Edit training costs, formulas (attribute contributions, divisor), bounds, learn mod
- Icon picker, add/delete skills

### Experience Table Editor *(beta)*

- Edit level progression, attribute/vital/skill XP cost tables
- Auto-scale generator with power-curve formulas for quick table generation
- Add/remove levels and ranks

### Vital Table Editor *(beta)*

- Edit Health, Stamina, and Mana formulas
- Configure attribute contributions, divisors, and multipliers
- Live formula preview

### Character Creation Editor *(beta)*

- Edit heritage groups: names, icons, attribute/skill credits, setup models
- 3D model preview for heritage setups with rotation and zoom
- Manage starting areas and spawn locations

### UI Layout Viewer *(beta)*

- Browse all LayoutDesc entries from the DAT
- Element tree hierarchy with property inspector
- Visual preview canvas with selection highlighting

### Object Debug

- Load any **Setup** (`0x02…`) or **GfxObj** (`0x01…`) by ID with searchable ID lists
- **3D preview** with orbit-style navigation (separate from the main terrain camera)
- **Export / import Wavefront OBJ** for mesh experimentation; optional **Surface DID** when importing (reuses a retail portal surface material)

### Weenie Editor *(beta)*

- **ACE MySQL** — browse weenies from your world database (configure connection in Settings)
- Edit **scalar** weenie properties (int, int64, bool, float, string, DID, IID) with add/remove rows per ACE property type
- **Create new weenie** in the database from scratch or from **JSON starter templates** (built-in + user template folders)
- **3D setup preview** and **icon** preview driven by DAT assets

### Monster Creator *(beta)*

- Search **creatures** from ACE, load body **parts** from DATs with **texture thumbnails**
- Per-slot **surface texture overrides**, **remove part**, or **donor GfxObj** (mix-and-match from another creature where supported)
- **Live 3D preview** reflecting overrides and palette
- **SQL output** for `weenie_properties_texture_map` / `weenie_properties_anim_part` style workflows
- **Replace RenderSurface** — pick an existing RenderSurface ID and import a replacement image; import/export **validates pixel format** so incompatible surfaces (for example DXT where uncompressed BGRA is required) are rejected with a clear error instead of corrupting the DAT

### Custom Textures

- **Terrain texture replacement** — import custom images to replace terrain types where supported
- **Dungeon surface import** — create or replace dungeon wall/floor textures
- **Safe in-place replacement** — terrain and RenderSurface swaps require a compatible **A8R8G8B8**-style surface; mismatched or DXT data is rejected before write
- **Creature textures** — use Monster Creator for part/slot work and **Replace RenderSurface** when you must target a specific existing RenderSurface ID

### DAT Export

- Exports to `client_cell_1.dat`, `client_portal.dat`, `client_highres.dat`, and `client_local_English.dat`
- Configurable portal iteration
- Layer-based export control (toggle which layers are included)
- Overwrite protection
- Optional **ACE MySQL instance reposition** after export (with connection test and threshold options)
- **Instance SQL alongside DATs** (when your project has placements): writes `dungeon_instances.sql` for dungeon generator/item/portal placements, and `landblock_instances.sql` for outdoor placements from the landscape editor; optional **apply directly** to MySQL when enabled in export settings

### Camera & Navigation

- **Perspective camera** — WASD + mouse look, Space to go up, Shift to go down
- **Top-down orthographic camera** — pan and zoom, ideal for large-scale editing
- **Toggle** — press Q to switch between modes
- **Go To Landblock** — Ctrl+G, enter a hex ID or X,Y coordinates
- **Location search** — search named locations (towns, dungeons, landmarks) and teleport to them
- **Camera bookmarks** — save and recall camera positions
- **Position HUD** — shows current coordinates in the viewport
- **Grid overlay** — landblock boundaries (magenta) and cell boundaries (cyan)
- **Overlay toggles** — grid, static objects, scenery, dungeons, unwalkable slopes
- Camera position and mode saved between sessions

### Projects

- Point at your **base DAT directory**, choose a **name** and **folder**, and save a **`.wbproj`** file
- Recent projects list on the splash screen
- All edit state stored in a **local SQLite** database inside the project; originals are not modified until you **Export Dats…**

### Other tools

- **File → Analyze Dungeon Rooms…** — utility for dungeon room analysis workflows

### Performance

- Streaming terrain chunks — only nearby chunks loaded, distant ones unloaded
- Background geometry generation — no frame stalls during terrain loading
- Frustum culling — only visible static objects are rendered
- Two-phase GPU upload — models load in background, upload to GPU in batches
- Texture disk cache — processed RGBA data cached to disk after first load
- Camera-aware streaming — top-down loads the visible rectangle, perspective uses proximity
- Zoom-scaled scenery — trees and rocks appear at appropriate zoom levels, buildings load everywhere
- Load/unload hysteresis — prevents objects flickering at view distance edges
- Distance-prioritized loading — closest landblocks load first
- Capped batch sizes and max loaded landblocks to keep memory and frame times stable

---

## Controls

### Camera

| Action | Key |
|---|---|
| Move forward / back / left / right | W / S / A / D or Arrow Keys |
| Rotate camera (perspective) | Shift + Arrow Keys, or mouse look |
| Move up | Space |
| Move down | Shift (held) |
| Toggle perspective / top-down | Q |
| Zoom in / out | Mouse wheel, or + / - |

### Editing

| Action | Key |
|---|---|
| Go to landblock | Ctrl+G |
| Undo | Ctrl+Z |
| Redo | Ctrl+Shift+Z or Ctrl+Y |
| Copy | Ctrl+C |
| Paste | Ctrl+V |
| Delete | Delete |
| Cancel / deselect | Escape |
| Multi-select | Ctrl+Click |
| Box select | Click and drag |
| Rotate stamp | [ / ] |
| Lower terrain (with raise tool) | Shift+Left-Click |
| Context menu | Right-Click |

---

## Default Settings

| Setting | Default |
|---|---|
| Projects directory | `Documents/ACME WorldBuilder/Projects` |
| History limit | 50 |
| Max draw distance | 4,000 units |
| Field of view | 60° |
| Mouse sensitivity | 1.0 |
| Movement speed | 1,000 units/sec |
| Light intensity | 0.45 |
| Grid visible | Yes |
| Grid opacity | 40% |
| Static objects visible | Yes |
| Scenery visible | Yes |
| Dungeons visible | Yes |
| Building interiors visible | No |
| Slope highlight | Off (threshold 45°) |
| Stamp library size | 10 |

All settings are configurable in the Settings panel and persist between sessions.

---

## Building

Requires .NET 8.0 SDK or later.

```
dotnet build WorldBuilder.slnx
```

## Running

```
dotnet run --project WorldBuilder.Windows
```

Platform-specific projects are also available: `WorldBuilder.Windows` (recommended), `WorldBuilder.Mac`, `WorldBuilder.Linux`.

---

## Thanks

- **Trevis** — original WorldBuilder vision and groundwork (DatReaderWriter + the base that this all grew from, before the big refactor...)
- **Gmriggs** — testing, research, and invaluable AC knowledge
- **Advan** — testing and bug reports
- **Vermino** — PRs and code contributions
- **The AC community** — everyone who has contributed, tested, reported bugs, or just kept Dereth going. If you helped and aren't listed, you know who you are.
