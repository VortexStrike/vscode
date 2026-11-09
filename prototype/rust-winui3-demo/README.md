# Rust + WinUI 3 Prototype

A small Windows-native editor shell that explores a Rust workspace service behind a WinUI 3 interface. This is a prototype, not a replacement for the full VS Code editor or extension host.

## What works

- Choose a workspace folder with the native Windows folder picker.
- Browse folders and files in the Explorer pane.
- Open multiple UTF-8 text files in editor tabs.
- Edit the active tab and save it back to the workspace.
- The Rust service confines browse/read/write operations to the selected workspace and does not follow symlinks while listing files.

## Layout

```text
prototype/rust-winui3-demo/
├── rust-backend/   Rust + Axum workspace/file API
└── winui3-app/     WinUI 3 Explorer and tabbed text editor
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

## API

- `GET /health` — backend status.
- `GET /workspace?path=relative/path` — list one folder beneath the selected workspace.
- `POST /workspace` with `{ "path": "C:\\project" }` — change the selected workspace.
- `GET /file?path=relative/file.txt` — read a UTF-8 file.
- `PUT /file` with `{ "path": "relative/file.txt", "content": "..." }` — save an existing file.

The API rejects traversal outside the workspace. Writes are limited to existing files; creating, renaming, deleting, file watching, syntax highlighting, and language services are not implemented yet.
