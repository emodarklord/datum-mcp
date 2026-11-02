# Datum

An MCP server that lets AI assistants (Claude, Codex, Cursor, local models) drive SOLIDWORKS through its COM API.

Status: early development, nothing usable yet.

## Goals

- One build for SOLIDWORKS 2021 and later, on every license type including Student and Education
- Reach the whole SOLIDWORKS API, not just a fixed set of tools
- Every change checkpointed, verified and reversible
- Low token use, measured by a public benchmark

## Requirements

- Windows 10/11 x64
- SOLIDWORKS 2021 or later
- .NET 10 SDK

## Building

```
dotnet build
dotnet test
```

The SolidWorks interop assemblies are read from your local install (`<SOLIDWORKS>\api\redist`). Set `SolidWorksApiDir` if it lives somewhere else:

```
dotnet build -p:SolidWorksApiDir="D:\SOLIDWORKS\api\redist"
```

## Not affiliated

SOLIDWORKS is a trademark of Dassault Systèmes. This project is not affiliated with or endorsed by Dassault Systèmes.
