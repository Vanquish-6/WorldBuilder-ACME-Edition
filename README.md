WorldBuilder - ACME Edition

Landscape and world building tool for Asheron's Call. Terrain editing, object placement, texture painting, road drawing, and DAT export.

Originally created by the Chorizite team (https://github.com/Chorizite). This fork is independently maintained by Vanquish.


Whats Working

Terrain - raise/lower height, set height, smooth, texture brush, bucket fill, road point/line placement and removal. Theres a slope overlay too for seeing unwalkable areas.

Objects - browse and search the full object catalog from the DATs (setups and gfxobjs). Search by hex ID or keyword, filter by buildings/scenery. Place them on the terrain, move them around, rotate, delete, etc.

Layers - full layer system with groups, visibility toggles, export toggles. Each layer tracks its own height/texture/road/scenery changes independently. Reorder and nest them however you want.

History / Snapshots - undo/redo for everything. History panel lets you jump to any previous state. Named snapshots that persist between sessions so you can save your progress and revert whenever.

DAT Export - exports to client_cell_1.dat, client_portal.dat, client_highres.dat, and client_local_English.dat. Configurable portal iteration, layer-based export control, overwrite protection.

Camera / Navigation - perspective and top-down ortho cameras, WASD + mouse look. Ctrl+G to go to a landblock by hex ID or X,Y coords. Position HUD and grid overlay for landblock/cell boundaries.

Projects - point it at your base DAT directory, give it a name, and go. Recent projects list, everything stored in a local SQLite db.


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
Delete - delete selected object(s)
Escape - cancel placement or deselect


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
