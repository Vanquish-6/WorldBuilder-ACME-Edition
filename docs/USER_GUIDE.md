# ACME WorldBuilder — User Guide

This guide explains how to install the app, create a project, move between tools, and export safely. For a full feature list and default key bindings, see the [project README](../README.md).

---

## What this application is

ACME WorldBuilder is a desktop editor for **Asheron's Call** client data. It reads your retail-style **DAT** files, keeps edits in a **local project database**, and can **export** updated `client_*.dat` files. Some tools also talk to an **ACE** MySQL database (weenies, creatures, export helpers).

---

## Requirements

- **Windows 10 or 11** is the primary supported platform (installers and updater). `WorldBuilder.Mac` and `WorldBuilder.Linux` exist for developers building from source.
- **.NET 8.0** runtime (the installer can prompt to install it).
- A **copy of the game's DAT folder** you are allowed to modify (always work on a copy).
- For **Weenie Editor**, **Monster Creator**, and some **export** options: a reachable **MySQL** server with ACE world data (`ace_world` or your shard's database), configured in **Settings**.

---

## First launch

The first startup can take noticeably longer than later runs. The app builds **caches** (textures, thumbnails, terrain-related data) that are stored on disk and reused afterward.

---

## Projects: what gets stored where

### Base DAT directory

When you **create** a project, you choose:

1. **Project name** and **folder** — this is where the `.wbproj` file lives (default parent folder is `Documents/ACME WorldBuilder/Projects` unless you changed it in Settings).
2. **Base DAT directory** — the folder that contains your source `client_cell_1.dat`, `client_portal.dat`, `client_highres.dat`, and `client_local_English.dat` (or the subset your shard uses). The app reads game data from here.

The project file points at that DAT path and stores **SQLite** project data (terrain/dungeon edits, layers, history metadata, custom texture entries, and similar). It does **not** replace your DATs until you explicitly **export**.

### Opening a project

From the splash screen, pick a **recent** project or browse to a `.wbproj`. The app reloads the linked DATs and project database.

---

## Main window overview

After a project loads you see the **main editor window**.

| Area | Purpose |
|------|--------|
| **Menu bar** | File operations, global Edit commands, navigation, **Landscape** tools (only when the landscape editor is active), and **Editors** (switch tools). |
| **Central area** | The active editor (landscape, dungeon, spell, etc.). |
| **Docking / tool windows** | Landscape and dungeon editors use dockable panels. Use **Windows** on the menu to show or hide panels. |

Most editors are switched from **Editors** in the menu. Landscape is the default when a project opens.

---

## When to use which editor

| Goal | Editor |
|------|--------|
| Terrain height, textures, roads, outdoor objects, landblocks | **Landscape Editor** |
| Indoor dungeon layout, room connections, surfaces, statics | **Dungeon Editor** |
| Spell definitions, icons, components | **Spell Editor** |
| Equipment spell sets | **Spell Set Editor** |
| Skill definitions and formulas | **Skill Editor** |
| Level / XP tables | **Experience Table Editor** |
| Health / stamina / mana formulas | **Vital Table Editor** |
| Character creation / heritage / starting areas | **Character Creation Editor** |
| Client UI layout tree (`LayoutDesc`) | **UI Layout Viewer** |
| Inspect or export a single **Setup** / **GfxObj** mesh | **Object Debug** |
| Edit ACE **weenie** scalar properties in MySQL | **Weenie Editor** |
| Retexture creatures, mix-and-match parts, generate SQL | **Monster Creator** |

---

## Settings (important before DB-backed tools)

Open **File → Settings**.

- **Paths** — projects directory, defaults for new projects.
- **ACE / MySQL** — host, port, database name, user, and password used by **Weenie Editor** and **Monster Creator** to list and save weenies. Use **Test Connection** where offered.
- **Graphics / landscape** — draw distance, overlays, camera, input.

If MySQL is not configured, database-backed editors will show a status message instead of data.

---

## Landscape & dungeon workflow (short)

1. Open the right editor from **Editors**.
2. Use the **viewport** and **tool panels** (layers, objects, history, etc.). Undo/redo are under **Edit** and apply to the active editor where supported.
3. **File → Export Dats…** when you are ready to write client files.

**Landscape-only menu:** **Landscape** appears when the landscape editor is active (performance overlay, clear cache, fresh start, import heightmap).

**Navigation:** **Navigate → Go to Landblock…** jumps the landscape camera (shortcut shown in the menu).

---

## Object Debug

Use this when you need a **focused 3D look** at one asset, not full world editing.

1. Switch to **Editors → Object Debug**.
2. Enter a **Setup** (`0x02…`) or **GfxObj** (`0x01…`) ID (hex like `0x02000001`) and click **Load**, or pick from the filtered lists.
3. **Export .obj** / **Import .obj** — round-trip a mesh for experimentation. For import, set **Surface DID** to a valid portal surface texture if prompted (reuses retail materials).

---

## Weenie Editor

Connects to your **ACE** database.

1. Configure MySQL in **Settings**.
2. **Editors → Weenie Editor**.
3. **Search** for an existing weenie, or use **Create new** with optional **JSON templates** (built-in and user template folders).
4. Edit **scalar** property rows (int, bool, float, string, DID, IID, int64). Add or remove properties from the dropdowns per tab.
5. **Save scalars** (existing row) or **Create in DB** (new weenie).

There is a **3D setup preview** and **icon** preview when the weenie references those assets in the DATs.

> Complex weenie features (generators, spells nested structures, etc.) may still require other ACE tools or SQL. This editor targets **scalar property tables** clearly.

---

## Monster Creator

For **creature appearance**: which surface texture IDs each body part uses, optional **donor** creature parts, and **SQL** you can apply to ACE.

1. Configure MySQL in **Settings**.
2. **Editors → Monster Creator**.
3. **Search** and select a **base creature**. The editor loads parts from DATs and shows thumbnails.
4. Assign **replacement** surface texture IDs, remove parts, or **apply** a donor part's GfxObj where supported.
5. Use **SQL output** to capture changes for your shard workflow.

### Replacing a RenderSurface by ID

If you need to swap the **bitmap** behind an existing **RenderSurface** ID (for example a creature texture), use the **Replace RenderSurface** flow in this editor when available. The app only accepts replacements that match the expected **uncompressed A8R8G8B8**-style format for that slot; **DXT** or mismatched sizes are **rejected** with an error instead of corrupting the DAT.

Custom terrain/dungeon imports use the same safety rules on export.

---

## Data tables (Spell, Skill, Vital, Experience, Spell Set, CharGen, Layout)

These editors read and write structures inside the **DAT** files (and related tables). They are powerful but still treated as **early / beta** in places.

**Good practice:** export to a **test client folder** first, keep backups of original DATs, and verify in-game or with your team's pipeline before shipping to players.

---

## Exporting DAT files

1. **File → Export Dats…**
2. Choose an **export directory** (often a staging copy of your client `dat` folder).
3. Set **Portal iteration** as required by your workflow (see README for context).
4. Enable **Overwrite** only if you intend to replace files that are already there.

### Optional: ACE database helpers on export

The export dialog includes MySQL fields used for several post-export steps:

- **Reposition DB instances after export** — optionally **tests** the connection, then adjusts world **instance** positions to match exported geometry (threshold and “apply directly” options on the dialog). Results and optional SQL paths are summarized when the export finishes.
- **`dungeon_instances.sql`** — written when any **dungeon** document has stored **instance placements** (generators, items, portals). Batch insert SQL targets the database name you entered.
- **`landblock_instances.sql`** — written when the **landscape** editor has **outdoor** instance placements in the project.

If **Apply changes directly to database** is enabled for the ACE section, the app may run the generated SQL immediately (see success dialog text). Turn that off if you prefer to review files first.

These features assume you know how your shard applies SQL and how instances are keyed in ACE.

---

## File menu utilities

- **Analyze Dungeon Rooms…** — batch analysis utility for dungeon room data (use when directed by tooling or docs for your content pipeline).

---

## Custom textures (summary)

- **Terrain** — replace a terrain type's appearance with a custom image where supported.
- **Dungeon** — add or replace wall/floor **RenderSurface** entries used by dungeon cells.
- **Creatures** — use **Monster Creator** for texture maps / SQL; use **Replace RenderSurface** when you must target a specific existing RenderSurface ID.

Invalid formats (for example **DXT** where an uncompressed surface is required) should **fail with a clear message** and not write a broken DAT.

---

## Getting help and reporting issues

- **Keyboard shortcuts:** **File → Keyboard Shortcuts…**
- **Updates:** desktop builds can check the published appcast (see README Releases).
- **Bugs:** note your OS, project type, and whether the issue happens on **export** or only in-editor; attach minimal steps if possible.

---

## Backups

Keep backups of:

- Original **DAT** files.
- Your **`.wbproj`** project folder (includes SQLite and links to base DATs).

Treat beta features as **experimental** until you have verified them in your environment.
