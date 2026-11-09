use axum::{
    extract::{Query, State},
    http::StatusCode,
    response::Json,
    routing::{get, post, put},
    Router,
};
use serde::{Deserialize, Serialize};
use std::{
    env,
    path::{Component, Path, PathBuf},
    sync::Arc,
};
use tokio::{fs, sync::RwLock};

#[derive(Clone)]
struct AppState {
    root: Arc<RwLock<PathBuf>>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct WorkspaceListing {
    root: String,
    path: String,
    entries: Vec<WorkspaceEntry>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct WorkspaceEntry {
    name: String,
    path: String,
    is_directory: bool,
}

#[derive(Deserialize)]
struct WorkspaceRequest {
    path: String,
}

#[derive(Deserialize)]
struct PathQuery {
    path: String,
}

#[derive(Deserialize)]
struct WriteFileRequest {
    path: String,
    content: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct FileContents {
    path: String,
    content: String,
}

#[derive(Serialize)]
struct HealthResponse {
    status: &'static str,
    app: &'static str,
}

type ApiResult<T> = Result<Json<T>, (StatusCode, String)>;

#[tokio::main]
async fn main() {
    let requested_root = env::args_os()
        .nth(1)
        .map(PathBuf::from)
        .unwrap_or_else(|| env::current_dir().expect("failed to get current directory"));
    let root = fs::canonicalize(&requested_root)
        .await
        .expect("workspace root must exist");
    if !root.is_dir() {
        panic!("workspace root must be a directory");
    }

    let state = AppState {
        root: Arc::new(RwLock::new(root)),
    };
    let app = Router::new()
        .route("/health", get(health))
        .route("/workspace", get(get_workspace).post(set_workspace))
        .route("/file", get(read_file).put(write_file))
        .with_state(state);

    let address = "127.0.0.1:3030";
    let listener = tokio::net::TcpListener::bind(address)
        .await
        .expect("failed to bind local backend");
    println!("Rust workspace backend running at http://{address}");
    axum::serve(listener, app)
        .await
        .expect("backend server failed");
}

async fn health() -> Json<HealthResponse> {
    Json(HealthResponse {
        status: "ok",
        app: "rust-winui3-prototype",
    })
}

async fn get_workspace(
    State(state): State<AppState>,
    Query(query): Query<PathQuery>,
) -> ApiResult<WorkspaceListing> {
    listing_for(&state, &query.path).await.map(Json)
}

async fn set_workspace(
    State(state): State<AppState>,
    Json(request): Json<WorkspaceRequest>,
) -> ApiResult<WorkspaceListing> {
    let selected = fs::canonicalize(&request.path)
        .await
        .map_err(|error| (StatusCode::BAD_REQUEST, format!("Cannot open folder: {error}")))?;
    if !selected.is_dir() {
        return Err((StatusCode::BAD_REQUEST, "Selected path is not a folder".into()));
    }

    *state.root.write().await = selected;
    listing_for(&state, "").await.map(Json)
}

async fn listing_for(state: &AppState, relative_path: &str) -> Result<WorkspaceListing, (StatusCode, String)> {
    let directory = resolve_existing(state, relative_path).await?;
    if !directory.is_dir() {
        return Err((StatusCode::BAD_REQUEST, "Path is not a folder".into()));
    }

    let root = state.root.read().await.clone();
    let mut entries = fs::read_dir(&directory)
        .await
        .map_err(internal_error)?;
    let mut items = Vec::new();
    while let Some(entry) = entries.next_entry().await.map_err(internal_error)? {
        let kind = entry.file_type().await.map_err(internal_error)?;
        if kind.is_symlink() || (!kind.is_dir() && !kind.is_file()) {
            continue;
        }
        let name = entry.file_name().to_string_lossy().into_owned();
        let full_path = entry.path();
        let relative = full_path
            .strip_prefix(&root)
            .map_err(internal_error)?
            .to_string_lossy()
            .replace('\\', "/");
        items.push(WorkspaceEntry {
            name,
            path: relative,
            is_directory: kind.is_dir(),
        });
    }
    items.sort_by(|left, right| {
        right
            .is_directory
            .cmp(&left.is_directory)
            .then_with(|| left.name.to_lowercase().cmp(&right.name.to_lowercase()))
    });

    Ok(WorkspaceListing {
        root: root.to_string_lossy().into_owned(),
        path: relative_path.replace('\\', "/"),
        entries: items,
    })
}

async fn read_file(
    State(state): State<AppState>,
    Query(query): Query<PathQuery>,
) -> ApiResult<FileContents> {
    let file = resolve_existing(&state, &query.path).await?;
    if !file.is_file() {
        return Err((StatusCode::BAD_REQUEST, "Path is not a file".into()));
    }
    let content = fs::read_to_string(&file)
        .await
        .map_err(|error| (StatusCode::BAD_REQUEST, format!("Cannot read text file: {error}")))?;

    Ok(Json(FileContents {
        path: query.path,
        content,
    }))
}

async fn write_file(
    State(state): State<AppState>,
    Json(request): Json<WriteFileRequest>,
) -> ApiResult<FileContents> {
    let file = resolve_existing(&state, &request.path).await?;
    if !file.is_file() {
        return Err((StatusCode::BAD_REQUEST, "Path is not a file".into()));
    }
    fs::write(&file, &request.content)
        .await
        .map_err(internal_error)?;

    Ok(Json(FileContents {
        path: request.path,
        content: request.content,
    }))
}

async fn resolve_existing(state: &AppState, relative: &str) -> Result<PathBuf, (StatusCode, String)> {
    let relative_path = Path::new(relative);
    if relative_path.is_absolute()
        || relative_path
            .components()
            .any(|component| !matches!(component, Component::Normal(_)))
    {
        return Err((StatusCode::BAD_REQUEST, "Path must be relative to the selected workspace".into()));
    }

    let root = state.root.read().await.clone();
    let candidate = root.join(relative_path);
    let resolved = fs::canonicalize(candidate)
        .await
        .map_err(|error| (StatusCode::NOT_FOUND, format!("Path not found: {error}")))?;
    if !resolved.starts_with(&root) {
        return Err((StatusCode::FORBIDDEN, "Path is outside the selected workspace".into()));
    }
    Ok(resolved)
}

fn internal_error(error: impl std::fmt::Display) -> (StatusCode, String) {
    (StatusCode::INTERNAL_SERVER_ERROR, error.to_string())
}
