# Datum

An MCP server that lets AI assistants (Claude, Codex, Cursor, local models) model parts in SOLIDWORKS through its COM API. You describe a part in plain English. The assistant sketches, extrudes, fillets and patterns it, checks the result in numbers and pictures, and exports it.

Status: v0.1. Parts only. Assemblies and drawings come later.

## What it does

- **Works on the SOLIDWORKS you have.** One build covers SOLIDWORKS 2021 and later on every license, Student and Education included. `datum diagnose` reports what it found.
- **Reports every change.** Each write replies with the volume before and after, the body count and any rebuild errors. A feature that fails or changes nothing is rolled back.
- **Can undo its own work.** Every change Datum makes is journaled, so the assistant can restore any earlier checkpoint.
- **Lets the assistant see the part.** `view_capture` returns a PNG of a standard view, or a four-view sheet drawn at one scale.
- **Keeps the tool list short.** About two dozen core tools are always listed. The rest load on demand, so idle tools don't cost tokens.

## Requirements

- Windows 10 or 11, x64
- SOLIDWORKS 2021 or later, running on the same machine. Any license works.
- An MCP client that runs local servers over stdio, such as Claude Code, Claude Desktop, Cursor or Codex.

Tested on SOLIDWORKS 2026 SP4.1 Student.

## Install

1. Download `datum-0.1.0-win-x64.zip` from [Releases](https://github.com/emodarklord/datum-mcp/releases) and unzip it, for example to `C:\Tools\datum`. The exe is self-contained, so you don't need to install .NET. It isn't code-signed, so Windows SmartScreen may warn the first time you run it.
2. Start SOLIDWORKS, then check that Datum can reach it:

   ```
   C:\Tools\datum\datum.exe diagnose
   ```

   This prints the Datum version, the folder it may write to, and the SOLIDWORKS version, license and add-ins. Run `datum diagnose --launch` to start SOLIDWORKS if it isn't running.

## Connect a client

**Claude Code:**

```
claude mcp add --scope user datum -- C:\Tools\datum\datum.exe
```

Or put this in a project's `.mcp.json`:

```json
{
  "mcpServers": {
    "datum": { "command": "C:\\Tools\\datum\\datum.exe", "args": [] }
  }
}
```

**Claude Desktop:** add the same `datum` entry under `mcpServers` in `%APPDATA%\Claude\claude_desktop_config.json`, then restart the app.

**Other clients:** use a stdio server whose command is `C:\Tools\datum\datum.exe`.

Options:

| Option | Effect |
|---|---|
| `--allow <folder>` | An extra folder Datum may write to. Repeat it for more folders. `DATUM_ALLOW` takes a `;`-separated list. The default is `Documents\Datum`. |
| `--all-tools` | List every tool up front, for clients that don't handle `tools/list_changed`. |

Then ask for a part, for example: *"Make a 60 × 40 × 10 mm plate with a 6 mm hole 8 mm in from each corner, fillet the vertical edges 3 mm, and export it as plate.step."*

## Tools

These are always available:

| Area | Tools |
|---|---|
| Status | `sw_status`, `find_tools`, `toolset_enable` |
| Documents | `doc_new`, `doc_open`, `doc_save`, `doc_export` (STEP, IGES, STL, Parasolid, PDF, DXF, DWG), `doc_list`, `doc_activate`, `doc_close`, `doc_allow_edit` |
| Inspection | `model_tree`, `model_info` (mass, volume, bounding box, material), `model_faces` (faces and edges with filters), `model_select`, `selection_get` |
| Sketching | `sketch_begin`, `sketch_add` (lines, centerlines, rectangles, circles, arcs, polygons, slots, polylines and points, in one batch), `sketch_constrain`, `sketch_end` |
| Features | `feature_extrude` (boss or cut) |
| Undo | `checkpoint_list`, `checkpoint_restore` |
| Viewing | `view_capture` |

These toolsets load on request (`toolset_enable`), and `find_tools` searches them:

| Toolset | Tools |
|---|---|
| `features` | `feature_revolve`, `feature_fillet`, `feature_chamfer`, `feature_hole`, `feature_pattern` (linear or circular), `feature_mirror`, `feature_shell` |
| `edit` | `model_dimensions`, `dimension_set`, `feature_suppress`, `feature_delete`, `rebuild`, `checkpoint_create` |
| `diagnostics` | `sw_addins` |

Tools that the connected SOLIDWORKS can't run (because of its version, license or add-ins) are hidden. `find_tools` says why.

Conventions:
- Lengths are in mm and angles in degrees.
- Faces, edges and features come back as short handles (`face#12`, `edge#40`, `feat#7`). Other tools accept them, and they stay the same across rebuilds.
- Errors read `CODE: message. Hint: what to do`.

## Safety

- Datum writes only inside its allowed folders: `Documents\Datum` by default, plus any you add with `--allow`.
- It changes only documents it created, or ones you release with `doc_allow_edit`. Releasing a document backs it up first.
- It won't overwrite a file or discard unsaved changes unless the call asks to. A replaced file is backed up to `%LOCALAPPDATA%\Datum\backups`.
- Checkpoints cover Datum's own changes only, not edits you make by hand. Restoring one discards the later ones, so there's no redo. A deleted feature can't be brought back, so restores stop there.
- Some SOLIDWORKS settings are switched temporarily, such as the dimension input prompt and the background colour while capturing. They're journaled and restored, even after a crash, the next time Datum connects.

## Limitations

- Parts only. Assemblies and drawings aren't supported yet.
- `view_capture` renders through the SOLIDWORKS window, so that window must not be minimized. While a sketch is open, its entities don't show in the image.
- Windows only, because SOLIDWORKS is.

## Building from source

You need the .NET 10 SDK and a local SOLIDWORKS install: the interop assemblies are read from `<SOLIDWORKS>\api\redist`.

```
dotnet build datum.slnx
dotnet test tests/Datum.Tests
```

Set `SolidWorksApiDir` if your install is somewhere else:

```
dotnet build datum.slnx -p:SolidWorksApiDir="D:\SOLIDWORKS\api\redist"
```

`tests/Datum.LiveTests` drives a running SOLIDWORKS and must be run on a machine that has it. CI builds and runs only the projects that don't need SOLIDWORKS.

To build the release exe:

```
dotnet publish src/Datum.Server -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/datum
```

## Not affiliated

SOLIDWORKS is a trademark of Dassault Systèmes. This project is not affiliated with or endorsed by Dassault Systèmes.
