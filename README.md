WorldBuilder - ACME Edition

Landscape and world building tool for Asheron's Call. Terrain editing, object placement, texture painting, road drawing, and DAT export.

Originally created by the Chorizite team (https://github.com/Chorizite). This fork is independently maintained by Vanquish.


Whats Working

Terrain
- Raise/lower height, set height, smooth
- Texture brush with shader-based WYSIWYG preview inside the brush circle
- Bucket fill with live texture + scenery preview (constrained to visible landblocks)
- Texture thumbnail palette that swaps in when terrain tools are active
- Road point/line placement and removal
- Slope overlay for seeing unwalkable areas

Objects
- Browse and search the full object catalog from the DATs (setups and gfxobjs)
- Search by hex ID or keyword, filter by buildings/scenery
- Lazy-loaded thumbnail cache that persists across sessions
- Place on terrain with snap, move, rotate, delete
- Multi-select with Ctrl+Click or marquee box select (drag to select)
- Right-click context menu (copy, paste, snap to terrain, delete)
- Copy/paste with Ctrl+C/V, including multi-object paste
- Properties panel with position, rotation, landcell, snap-to-terrain
- Delete confirmation dialog
- Static object height auto-adjustment when terrain changes underneath
- Building blueprint system for placing buildings with interior cells

Layers
- Full layer system with groups, visibility toggles, export toggles
- Each layer tracks its own height/texture/road/scenery changes independently
- Reorder and nest however you want, drag-and-drop
- Base layer always at bottom, cant be deleted or reordered
- Layer compositing for export (top-to-bottom, first non-null wins)

History / Snapshots
- Undo/redo for everything (50 entry limit, configurable)
- History panel lets you jump to any previous state
- Forward entries dimmed, Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z
- Named snapshots that persist between sessions

DAT Export
- Exports to client_cell_1.dat, client_portal.dat, client_highres.dat, and client_local_English.dat
- Configurable portal iteration, layer-based export control, overwrite protection

Camera / Navigation
- Perspective and top-down ortho cameras, WASD + mouse look
- Ctrl+G to go to a landblock by hex ID or X,Y coords
- Camera reset button in viewport toolbar
- Position HUD and grid overlay for landblock/cell boundaries
- All overlay toggles: grid, static objects, scenery, unwalkable slopes
- Camera position and mode persisted across sessions

UI / Settings
- UI state persistence (selected tool, panel widths) saved on exit
- File > Exit menu item with graceful shutdown
- Settings saved via MainWindow.Closing and ShutdownRequested
- Splash screen with loading state

Projects
- Point it at your base DAT directory, give it a name, and go
- Recent projects list, everything stored in a local SQLite db

Performance
- Streaming terrain chunks (only nearby chunks loaded, distant ones unloaded)
- Background terrain chunk geometry generation (no frame stalls on load)
- Static object frustum culling (only visible objects rendered)
- Background model loading with two-phase GPU upload
- Texture disk cache for processed RGBA data


Controls

WASD or Arrow Keys - move camera
Shift + Arrow Keys - rotate camera (perspective)
Q - toggle between perspective and top-down camera
+/- - zoom in/out
Mouse look for camera rotation, scroll wheel for zoom

Ctrl+G - go to landblock (hex ID or X,Y)
Ctrl+Z - undo
Ctrl+Shift+Z or Ctrl+Y - redo
Ctrl+C - copy selected object(s)
Ctrl+V - paste / enter placement mode
Ctrl+Click - multi-select objects
Delete - delete selected object(s) (with confirmation)
Escape - cancel placement or deselect
Right-click - context menu on selected objects, paste when clipboard has content


Building

Requires .NET 8.0 SDK or later.

dotnet build WorldBuilder.slnx


Running

dotnet run --project WorldBuilder.Desktop

There are also platform-specific projects: WorldBuilder.Windows, WorldBuilder.Mac, WorldBuilder.Linux.


Thanks

Big thanks to everyone who made this possible:

Trevis - original vision and groundwork/DatReadWriter/Worldbuilder 
Gmriggs - foundational tooling and research
and everyone else in the AC community whos contributed, tested, reported bugs, or just kept Dereth going. If you helped and arent listed, you know who you are.
