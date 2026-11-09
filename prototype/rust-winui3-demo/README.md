# Rust + WinUI 3 Prototype

This prototype creates a minimal architecture for a desktop app that pairs a Rust backend with a Windows WinUI 3 frontend.

## Overview

- `rust-backend/` hosts a small local HTTP API written in Rust.
- `winui3-app/` is a WinUI 3 desktop shell that calls the backend.
- The design proves the integration shape without rewriting the full VS Code product.

## Folder layout

```text
prototype/rust-winui3-demo/
├── README.md
├── rust-backend/
│   ├── Cargo.toml
│   └── src/
│       └── main.rs
└── winui3-app/
    ├── App.xaml
    ├── App.xaml.cs
    ├── MainWindow.xaml
    ├── MainWindow.xaml.cs
    └── WinUI3App.csproj
```

## Why this shape works

The app keeps the expensive UI work in WinUI 3 and the data/workflow logic in Rust. The WinUI 3 screen does not need to own the full editor engine; it can instead call a local service, display state, and evolve toward a real shell by replacing the backend with your editor logic.

## Build and run

### 1) Start the Rust backend

```bash
cd prototype/rust-winui3-demo/rust-backend
cargo run
```

Expected result:

```text
Rust backend running at http://127.0.0.1:3030
```

### 2) Open the WinUI 3 app in Visual Studio 2022

- Open `prototype/rust-winui3-demo/winui3-app/WinUI3App.csproj` in Visual Studio.
- Set the solution configuration to `Debug x64`.
- Press F5 to run.

The app sends a request to the backend and displays the response.

## Current prototype behavior

- `/health` returns app status JSON.
- `/echo?message=hello` returns the echoed message.
- The WinUI 3 button calls the backend and updates the UI.

## Next extension path

This prototype is intentionally minimal. The next steps would be:

1. replace the placeholder status screen with document tabs and a shell layout,
2. connect to file-system operations in Rust,
3. add settings persistence and service registration,
4. gradually migrate editor features behind the same Rust service boundary.

This keeps the migration small, testable, and Windows-native while avoiding a risky full replacement of VS Code.
