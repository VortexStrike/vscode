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

#[derive(Deserialize)]
struct CreateEntryRequest {
    parent: String,
    name: String,
}

#[derive(Deserialize)]
struct RenameRequest {
    path: String,
    name: String,
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
        .route("/file", get(read_file).post(create_file).put(write_file))
        .route("/directory", post(create_directory))
        .route("/entry", put(rename_entry).delete(delete_entry))
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
    let mut entries = fs::read_dir(&directory).await.map_err(internal_error)?;
    let mut items = Vec::new();
    while let Some(entry) = entries.next_entry().await.map_err(internal_error)? {
        let kind = entry.file_type().await.map_err(internal_error)?;
        if kind.is_symlink() || (!kind.is_dir() && !kind.is_file()) {
            continue;
        }
        let name = entry.file_name().to_string_lossy().into_owned();
        let relative = entry
            .path()
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
    Ok(Json(FileContents { path: query.path, content }))
}

async fn write_file(
    State(state): State<AppState>,
    Json(request): Json<WriteFileRequest>,
) -> ApiResult<FileContents> {
    let file = resolve_existing(&state, &request.path).await?;
    if !file.is_file() {
        return Err((StatusCode::BAD_REQUEST, "Path is not a file".into()));
    }
    fs::write(&file, &request.content).await.map_err(internal_error)?;
    Ok(Json(FileContents { path: request.path, content: request.content }))
}

async fn create_file(
    State(state): State<AppState>,
    Json(request): Json<CreateEntryRequest>,
) -> Result<Json<WorkspaceEntry>, (StatusCode, String)> {
    let path = resolve_new_path(&state, &request.parent, &request.name).await?;
    fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&path)
        .await
        .map_err(|error| (StatusCode::CONFLICT, format!("Cannot create file: {error}")))?;
    Ok(Json(make_entry(&state, path, request.name, false).await?))
}

async fn create_directory(
    State(state): State<AppState>,
    Json(request): Json<CreateEntryRequest>,
) -> Result<Json<WorkspaceEntry>, (StatusCode, String)> {
    let path = resolve_new_path(&state, &request.parent, &request.name).await?;
    fs::create_dir(&path)
        .await
        .map_err(|error| (StatusCode::CONFLICT, format!("Cannot create folder: {error}")))?;
    Ok(Json(make_entry(&state, path, request.name, true).await?))
}

async fn rename_entry(
    State(state): State<AppState>,
    Json(request): Json<RenameRequest>,
) -> Result<Json<WorkspaceEntry>, (StatusCode, String)> {
    let source = resolve_existing(&state, &request.path).await?;
    validate_name(&request.name)?;
    let parent = source.parent().ok_or((StatusCode::BAD_REQUEST, "Cannot rename workspace root".into()))?;
    let destination = parent.join(&request.name);
    if fs::try_exists(&destination).await.map_err(internal_error)? {
        return Err((StatusCode::CONFLICT, "An entry with that name already exists".into()));
    }
    fs::rename(&source, &destination)
        .await
        .map_err(|error| (StatusCode::BAD_REQUEST, format!("Cannot rename entry: {error}")))?;
    let is_directory = fs::metadata(&destination).await.map_err(internal_error)?.is_dir();
    Ok(Json(make_entry(&state, destination, request.name, is_directory).await?))
}

async fn delete_entry(
    State(state): State<AppState>,
    Query(query): Query<PathQuery>,
) -> Result<StatusCode, (StatusCode, String)> {
    let target = resolve_existing(&state, &query.path).await?;
    if target == *state.root.read().await {
        return Err((StatusCode::BAD_REQUEST, "Cannot delete workspace root".into()));
    }
    let metadata = fs::symlink_metadata(&target).await.map_err(internal_error)?;
    if metadata.is_dir() {
        fs::remove_dir(&target).await.map_err(|error| {
            (StatusCode::BAD_REQUEST, format!("Folder must be empty before deletion: {error}"))
        })?;
    } else {
        fs::remove_file(&target).await.map_err(internal_error)?;
    }
    Ok(StatusCode::NO_CONTENT)
}

async fn resolve_new_path(
    state: &AppState,
    parent_relative: &str,
    name: &str,
) -> Result<PathBuf, (StatusCode, String)> {
    validate_name(name)?;
    let parent = resolve_existing(state, parent_relative).await?;
    if !parent.is_dir() {
        return Err((StatusCode::BAD_REQUEST, "Parent is not a folder".into()));
    }
    Ok(parent.join(name))
}

fn validate_name(name: &str) -> Result<(), (StatusCode, String)> {
    let path = Path::new(name);
    if name.trim().is_empty()
        || path.is_absolute()
        || path.components().count() != 1
        || !matches!(path.components().next(), Some(Component::Normal(_)))
    {
        return Err((StatusCode::BAD_REQUEST, "Name must be a single valid file or folder name".into()));
    }
    Ok(())
}

async fn make_entry(
    state: &AppState,
    path: PathBuf,
    name: String,
    is_directory: bool,
) -> Result<WorkspaceEntry, (StatusCode, String)> {
    let root = state.root.read().await.clone();
    let relative = path
        .strip_prefix(&root)
        .map_err(|_| (StatusCode::FORBIDDEN, "Path is outside workspace".into()))?
        .to_string_lossy()
        .replace('\\', "/");
    Ok(WorkspaceEntry { name, path: relative, is_directory })
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
    let candidate = if relative.is_empty() { root.clone() } else { root.join(relative_path) };
    let resolved = fs::canonicalize(candidate)
        .await
        .map_err(|error| (StatusCode::NOT_FOUND, format!("Path not found: {error}")))?;
    if !resolved.starts_with(&root) {
        return Err((StatusCode::FORBIDDEN, "Path is outside the selected workspace".into()));
    }
    let metadata = fs::symlink_metadata(&resolved).await.map_err(internal_error)?;
    if metadata.file_type().is_symlink() {
        return Err((StatusCode::FORBIDDEN, "Symbolic links are not supported".into()));
    }
    Ok(resolved)
}

fn internal_error(error: impl std::fmt::Display) -> (StatusCode, String) {
    (StatusCode::INTERNAL_SERVER_ERROR, error.to_string())
}
