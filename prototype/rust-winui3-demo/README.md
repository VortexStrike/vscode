# Rust + WinUI 3 Prototype

A small Windows-native editor shell that explores a Rust workspace service behind a WinUI 3 interface. This is a prototype, not a replacement for the full VS Code editor or extension host.

## What works

- Choose a workspace folder with the native Windows folder picker.
- Browse the project tree with lazy folder expansion.
- Open multiple UTF-8 text files in editor tabs, edit, and save them.
- Use Explorer context menus to create files/folders, rename entries, or delete files and empty folders.
- See basic keyword, string, and line-comment coloring in the editor surface.
- The Rust service confines workspace operations to the selected root and skips symbolic links while listing.

## Layout

```text
prototype/rust-winui3-demo/
├── rust-backend/   Rust + Axum workspace/file API
└── winui3-app/     WinUI 3 Explorer and tabbed editor
```

## Requirements

- Rust toolchain with Cargo.
- Windows 10 SDK and Visual Studio 2022 with the Windows App SDK / WinUI 3 workload.
- The Windows app and Rust service run on the same machine.

## Run

Start the Rust service from the repository root or pass an existing folder path as its initial workspace:

```powershell
cd prototype/rust-winui3-demo/rust-backend
cargo run
# Or: cargo run -- "C:\\path\\to\\workspace"
```

The service binds only to `127.0.0.1:3030`. Then open `winui3-app/WinUI3App.csproj` in Visual Studio, select `Debug x64`, and run. Use **Open Folder** to choose a project directory.

## Local API

- `GET /health` — backend status.
- `GET /workspace?path=relative/path` — list one folder beneath the selected workspace.
- `POST /workspace` with `{ "path": "C:\\project" }` — change the selected workspace.
- `GET /file?path=relative/file.txt` — read a UTF-8 file.
- `POST /file` with `{ "parent": "relative/folder", "name": "new.txt" }` — create an empty text file.
- `PUT /file` with `{ "path": "relative/file.txt", "content": "..." }` — save an existing file.
- `POST /directory` with `{ "parent": "relative/folder", "name": "new-folder" }` — create a folder.
- `PUT /entry` with `{ "path": "relative/old.txt", "name": "new.txt" }` — rename an entry.
- `DELETE /entry?path=relative/file.txt` — delete a file or an empty folder.

The API rejects traversal outside the workspace. Deleting folders is intentionally limited to empty folders. The syntax coloring is a lightweight prototype, not a parser or language service; unsaved-change prompts, file watching, and language intelligence are not implemented.
