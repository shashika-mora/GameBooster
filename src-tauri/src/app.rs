use crate::model::{Game, MonitorSample, Profile, Recovery, Session};
use crate::{power, steam, storage};
use rusqlite::{params, Connection};
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Mutex,
};
use sysinfo::System;
use tauri::{Manager, State};

struct AppState {
    db: Mutex<Connection>,
    active: AtomicBool,
    monitor: Mutex<System>,
}

fn error<E: std::fmt::Display>(e: E) -> String {
    e.to_string()
}

#[tauri::command]
fn list_games(state: State<AppState>) -> Result<Vec<Game>, String> {
    storage::games(&*state.db.lock().map_err(error)?).map_err(error)
}

#[tauri::command]
fn add_game(state: State<AppState>, name: String, executable: String) -> Result<i64, String> {
    let path = std::path::Path::new(&executable);
    if !path.is_file()
        || !path
            .extension()
            .and_then(|x| x.to_str())
            .is_some_and(|x| x.eq_ignore_ascii_case("exe"))
    {
        return Err("Choose an existing .exe file".into());
    }
    let name = name.trim();
    if name.is_empty() {
        return Err("Game name is required".into());
    }
    storage::add_game(
        &*state.db.lock().map_err(error)?,
        name,
        &executable,
        "manual",
    )
    .map_err(error)
}

#[tauri::command]
fn set_game_executable(
    state: State<AppState>,
    game_id: i64,
    executable: String,
) -> Result<(), String> {
    let path = std::path::Path::new(&executable);
    if !path.is_file()
        || !path
            .extension()
            .and_then(|x| x.to_str())
            .is_some_and(|x| x.eq_ignore_ascii_case("exe"))
    {
        return Err("Choose an existing .exe file".into());
    }
    let changed = state
        .db
        .lock()
        .map_err(error)?
        .execute(
            "UPDATE games SET executable=?1 WHERE id=?2",
            params![executable, game_id],
        )
        .map_err(error)?;
    if changed == 0 {
        return Err("Game not found".into());
    }
    Ok(())
}

#[tauri::command]
fn scan_steam(state: State<AppState>) -> Result<usize, String> {
    let db = state.db.lock().map_err(error)?;
    let mut inserted = 0;
    for game in steam::scan() {
        let exists: i64 = db
            .query_row(
                "SELECT COUNT(*) FROM games WHERE install_path=?1",
                [&game.install_path],
                |r| r.get(0),
            )
            .map_err(error)?;
        if exists > 0 {
            continue;
        }
        let changed = db.execute(
            "INSERT OR IGNORE INTO games(name,executable,install_path,source) VALUES (?1,?2,?3,'steam')",
            params![game.name, game.executable, game.install_path],
        )
        .map_err(error)?;
        if changed == 0 {
            continue;
        }
        db.execute(
            "INSERT INTO profiles(game_id,preset) VALUES (?1,'Balanced')",
            [db.last_insert_rowid()],
        )
        .map_err(error)?;
        inserted += 1;
    }
    Ok(inserted)
}

#[tauri::command]
fn get_profile(state: State<AppState>, game_id: i64) -> Result<Profile, String> {
    storage::profile(&*state.db.lock().map_err(error)?, game_id).map_err(error)
}

#[tauri::command]
fn save_profile(state: State<AppState>, profile: Profile) -> Result<(), String> {
    if !["Balanced", "Performance", "Custom"].contains(&profile.preset.as_str()) {
        return Err("Invalid preset".into());
    }
    if profile
        .power_scheme
        .as_deref()
        .is_some_and(|g| !power::valid_guid(g))
    {
        return Err("Invalid power scheme GUID".into());
    }
    if profile.preset == "Balanced" && profile.power_scheme.is_some() {
        return Err("Balanced does not change the power plan".into());
    }
    if profile.preset == "Performance"
        && profile
            .power_scheme
            .as_deref()
            .is_none_or(|g| !g.eq_ignore_ascii_case("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"))
    {
        return Err("Performance uses the Windows High performance scheme".into());
    }
    let normalized = Profile {
        power_scheme: profile.power_scheme.map(|g| g.to_ascii_lowercase()),
        ..profile
    };
    storage::save_profile(&*state.db.lock().map_err(error)?, &normalized).map_err(error)
}

#[tauri::command]
fn list_sessions(state: State<AppState>) -> Result<Vec<Session>, String> {
    storage::sessions(&*state.db.lock().map_err(error)?).map_err(error)
}

#[tauri::command]
fn recovery(state: State<AppState>) -> Result<Option<Recovery>, String> {
    storage::pending(&*state.db.lock().map_err(error)?).map_err(error)
}

#[tauri::command]
fn session_active(state: State<AppState>) -> bool {
    state.active.load(Ordering::SeqCst)
}

#[tauri::command]
fn active_power_scheme() -> Result<String, String> {
    power::active()
}

#[tauri::command]
fn monitor_sample(state: State<AppState>) -> Result<MonitorSample, String> {
    let mut monitor = state.monitor.lock().map_err(error)?;
    monitor.refresh_cpu_usage();
    monitor.refresh_memory();
    Ok(MonitorSample {
        cpu_percent: monitor.global_cpu_usage(),
        used_memory_bytes: monitor.used_memory(),
        total_memory_bytes: monitor.total_memory(),
    })
}

fn finish_session(
    app: &tauri::AppHandle,
    id: i64,
    previous: Option<&str>,
    applied: Option<&str>,
    final_status: &str,
) {
    let result = power::restore(previous, applied);
    let state = app.state::<AppState>();
    if let Ok(db) = state.db.lock() {
        match result {
            Ok(message) => {
                let operation_status = if final_status == "apply_failed" {
                    "apply_failed"
                } else {
                    "restored"
                };
                let _ = storage::operation_status(&db, id, operation_status, Some(&message));
                let _ = storage::set_status(&db, id, final_status, Some(&message), true);
                log::info!("Session {id}: {message}");
            }
            Err(message) => {
                let _ = storage::operation_status(&db, id, "restore_failed", Some(&message));
                let _ = storage::set_status(&db, id, "restore_failed", Some(&message), false);
                log::error!("Session {id}: {message}");
            }
        }
    }
    state.active.store(false, Ordering::SeqCst);
}

#[tauri::command]
fn launch_game(app: tauri::AppHandle, state: State<AppState>, game_id: i64) -> Result<i64, String> {
    if state.active.swap(true, Ordering::SeqCst) {
        return Err("A game session is already running".into());
    }
    let attempt = (|| {
        let db = state.db.lock().map_err(error)?;
        if storage::pending(&db).map_err(error)?.is_some() {
            return Err("Recover the unfinished session before starting another".into());
        }
        let game = storage::game(&db, game_id).map_err(error)?;
        let executable = game
            .executable
            .ok_or("Add an executable path for this game first")?;
        let path = std::path::Path::new(&executable);
        if !path.is_file() {
            return Err("Game executable no longer exists".into());
        }
        let profile = storage::profile(&db, game_id).map_err(error)?;
        let previous = if profile.power_scheme.is_some() {
            Some(power::active()?)
        } else {
            None
        };
        let applied = profile
            .power_scheme
            .filter(|target| previous.as_deref() != Some(target));
        let id = storage::start_session(
            &db,
            game_id,
            &profile.preset,
            previous.as_deref(),
            applied.as_deref(),
        )
        .map_err(error)?;
        drop(db);
        if let Some(target) = &applied {
            if let Err(message) = power::set(target) {
                if let Ok(db) = state.db.lock() {
                    let _ = storage::operation_status(&db, id, "apply_failed", Some(&message));
                }
                finish_session(
                    &app,
                    id,
                    previous.as_deref(),
                    applied.as_deref(),
                    "apply_failed",
                );
                return Err(message);
            }
            if let Ok(db) = state.db.lock() {
                let _ = storage::operation_status(&db, id, "applied", Some("Change verified"));
            }
        }
        let child = std::process::Command::new(path)
            .current_dir(path.parent().ok_or("Invalid executable path")?)
            .spawn();
        let Ok(mut child) = child else {
            finish_session(
                &app,
                id,
                previous.as_deref(),
                applied.as_deref(),
                "launch_failed",
            );
            return Err(
                "Could not start the game. Any power-plan change was restored or left for recovery"
                    .into(),
            );
        };
        match state.db.lock() {
            Ok(db) => {
                if let Err(message) = storage::set_status(&db, id, "running", None, false) {
                    log::error!("Session {id}: could not persist running status: {message}");
                }
            }
            Err(message) => log::error!("Session {id}: database lock failed: {message}"),
        }
        std::thread::spawn(move || {
            let _ = child.wait();
            finish_session(
                &app,
                id,
                previous.as_deref(),
                applied.as_deref(),
                "completed",
            );
        });
        Ok(id)
    })();
    if attempt.is_err() {
        state.active.store(false, Ordering::SeqCst);
    }
    attempt
}

#[tauri::command]
fn recover_session(state: State<AppState>) -> Result<String, String> {
    if state.active.load(Ordering::SeqCst) {
        return Err("The game is still running".into());
    }
    let pending = storage::pending(&*state.db.lock().map_err(error)?)
        .map_err(error)?
        .ok_or("No pending session")?;
    match power::restore(
        pending.previous_scheme.as_deref(),
        pending.applied_scheme.as_deref(),
    ) {
        Ok(result) => {
            storage::operation_status(
                &*state.db.lock().map_err(error)?,
                pending.session_id,
                "restored",
                Some(&result),
            )
            .map_err(error)?;
            storage::set_status(
                &*state.db.lock().map_err(error)?,
                pending.session_id,
                "recovered",
                Some(&result),
                true,
            )
            .map_err(error)?;
            Ok(result)
        }
        Err(message) => {
            storage::operation_status(
                &*state.db.lock().map_err(error)?,
                pending.session_id,
                "restore_failed",
                Some(&message),
            )
            .map_err(error)?;
            storage::set_status(
                &*state.db.lock().map_err(error)?,
                pending.session_id,
                "restore_failed",
                Some(&message),
                false,
            )
            .map_err(error)?;
            Err(message)
        }
    }
}

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_log::Builder::new().build())
        .setup(|app| {
            let directory = app.path().app_data_dir()?;
            std::fs::create_dir_all(&directory)?;
            let db = Connection::open(directory.join("gamebooster.db"))?;
            storage::migrate(&db)?;
            app.manage(AppState {
                db: Mutex::new(db),
                active: AtomicBool::new(false),
                monitor: Mutex::new(System::new()),
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            list_games,
            add_game,
            set_game_executable,
            scan_steam,
            get_profile,
            save_profile,
            list_sessions,
            recovery,
            session_active,
            active_power_scheme,
            monitor_sample,
            launch_game,
            recover_session
        ])
        .run(tauri::generate_context!())
        .expect("GameBooster failed to start");
}
