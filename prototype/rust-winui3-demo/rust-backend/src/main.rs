use axum::{
    extract::Query,
    http::StatusCode,
    response::Json,
    routing::get,
    Router,
};
use serde::Serialize;
use std::net::SocketAddr;

#[derive(Serialize)]
struct HealthResponse {
    status: &'static str,
    message: &'static str,
    app: &'static str,
}

#[tokio::main]
async fn main() {
    let app = Router::new()
        .route("/", get(root))
        .route("/health", get(health))
        .route("/echo", get(echo));

    let addr = SocketAddr::from(([127, 0, 0, 1], 3030));
    println!("Rust backend running at http://127.0.0.1:3030");

    let listener = tokio::net::TcpListener::bind(addr)
        .await
        .expect("failed to bind test server");

    axum::serve(listener, app)
        .await
        .expect("backend server failed");
}

async fn root() -> String {
    "Rust + WinUI 3 prototype backend".to_string()
}

async fn health() -> (StatusCode, Json<HealthResponse>) {
    let payload = HealthResponse {
        status: "ok",
        message: "Rust backend ready",
        app: "winui3-rust-prototype",
    };

    (StatusCode::OK, Json(payload))
}

async fn echo(Query(params): Query<std::collections::HashMap<String, String>>) -> (StatusCode, Json<serde_json::Value>) {
    let message = params.get("message").cloned().unwrap_or_else(|| "hello".to_string());
    let payload = serde_json::json!({
        "status": "ok",
        "echo": message,
        "source": "rust-backend"
    });

    (StatusCode::OK, Json(payload))
}
