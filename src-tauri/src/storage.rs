use crate::model::{Game, Profile, Recovery, Session};
use rusqlite::{params, Connection, OptionalExtension, Result};

pub fn migrate(db: &Connection) -> Result<()> {
    db.execute_batch(
        "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;
        CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS games(
            id INTEGER PRIMARY KEY, name TEXT NOT NULL, executable TEXT UNIQUE,
            install_path TEXT, source TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS profiles(
            game_id INTEGER PRIMARY KEY REFERENCES games(id) ON DELETE CASCADE,
            preset TEXT NOT NULL DEFAULT 'Balanced', power_scheme TEXT);
        CREATE TABLE IF NOT EXISTS sessions(
            id INTEGER PRIMARY KEY, game_id INTEGER NOT NULL REFERENCES games(id),
            started_at TEXT NOT NULL, ended_at TEXT, status TEXT NOT NULL,
            previous_scheme TEXT, applied_scheme TEXT, restoration_result TEXT);
        CREATE TABLE IF NOT EXISTS operation_records(
            id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL REFERENCES sessions(id),
            operation TEXT NOT NULL, previous_value TEXT, requested_value TEXT,
            status TEXT NOT NULL, detail TEXT);
        INSERT OR IGNORE INTO schema_migrations(version) VALUES (1);",
    )?;
    let version: i64 = db.query_row("SELECT MAX(version) FROM schema_migrations", [], |r| {
        r.get(0)
    })?;
    if version < 2 {
        db.execute_batch(
            "BEGIN;
            ALTER TABLE sessions ADD COLUMN profile_preset TEXT NOT NULL DEFAULT 'Balanced';
            INSERT INTO schema_migrations(version) VALUES (2);
            COMMIT;",
        )?;
    }
    Ok(())
}

pub fn games(db: &Connection) -> Result<Vec<Game>> {
    let mut query = db.prepare(
        "SELECT id,name,executable,install_path,source FROM games ORDER BY name COLLATE NOCASE",
    )?;
    let rows = query.query_map([], |r| {
        Ok(Game {
            id: r.get(0)?,
            name: r.get(1)?,
            executable: r.get(2)?,
            install_path: r.get(3)?,
            source: r.get(4)?,
        })
    })?;
    rows.collect()
}

pub fn add_game(db: &Connection, name: &str, executable: &str, source: &str) -> Result<i64> {
    db.execute(
        "INSERT INTO games(name,executable,install_path,source) VALUES (?1,?2,?3,?4)",
        params![
            name,
            executable,
            std::path::Path::new(executable)
                .parent()
                .map(|p| p.to_string_lossy().into_owned()),
            source
        ],
    )?;
    let id = db.last_insert_rowid();
    db.execute(
        "INSERT INTO profiles(game_id,preset) VALUES (?1,'Balanced')",
        [id],
    )?;
    Ok(id)
}

pub fn profile(db: &Connection, game_id: i64) -> Result<Profile> {
    db.query_row(
        "SELECT game_id,preset,power_scheme FROM profiles WHERE game_id=?1",
        [game_id],
        |r| {
            Ok(Profile {
                game_id: r.get(0)?,
                preset: r.get(1)?,
                power_scheme: r.get(2)?,
            })
        },
    )
}

pub fn save_profile(db: &Connection, value: &Profile) -> Result<()> {
    db.execute("INSERT INTO profiles(game_id,preset,power_scheme) VALUES (?1,?2,?3)
        ON CONFLICT(game_id) DO UPDATE SET preset=excluded.preset,power_scheme=excluded.power_scheme",
        params![value.game_id, value.preset, value.power_scheme])?;
    Ok(())
}

pub fn game(db: &Connection, id: i64) -> Result<Game> {
    db.query_row(
        "SELECT id,name,executable,install_path,source FROM games WHERE id=?1",
        [id],
        |r| {
            Ok(Game {
                id: r.get(0)?,
                name: r.get(1)?,
                executable: r.get(2)?,
                install_path: r.get(3)?,
                source: r.get(4)?,
            })
        },
    )
}

pub fn start_session(
    db: &Connection,
    game_id: i64,
    preset: &str,
    previous: Option<&str>,
    applied: Option<&str>,
) -> Result<i64> {
    db.execute("INSERT INTO sessions(game_id,started_at,status,previous_scheme,applied_scheme,profile_preset) VALUES (?1,?2,'prepared',?3,?4,?5)",
        params![game_id, chrono::Utc::now().to_rfc3339(), previous, applied, preset])?;
    let id = db.last_insert_rowid();
    if let Some(target) = applied {
        db.execute("INSERT INTO operation_records(session_id,operation,previous_value,requested_value,status)
            VALUES (?1,'power_scheme',?2,?3,'prepared')", params![id,previous,target])?;
    }
    Ok(id)
}

pub fn operation_status(
    db: &Connection,
    session_id: i64,
    status: &str,
    detail: Option<&str>,
) -> Result<()> {
    db.execute("UPDATE operation_records SET status=?2,detail=?3 WHERE session_id=?1 AND operation='power_scheme'",
        params![session_id,status,detail])?;
    Ok(())
}

pub fn set_status(
    db: &Connection,
    id: i64,
    status: &str,
    result: Option<&str>,
    finished: bool,
) -> Result<()> {
    let ended = finished.then(|| chrono::Utc::now().to_rfc3339());
    db.execute(
        "UPDATE sessions SET status=?2,restoration_result=?3,ended_at=?4 WHERE id=?1",
        params![id, status, result, ended],
    )?;
    Ok(())
}

pub fn pending(db: &Connection) -> Result<Option<Recovery>> {
    db.query_row("SELECT s.id,g.name,s.previous_scheme,s.applied_scheme,s.status FROM sessions s
        JOIN games g ON g.id=s.game_id WHERE s.status IN ('prepared','running','restore_failed') ORDER BY s.id DESC LIMIT 1", [],
        |r| Ok(Recovery { session_id: r.get(0)?, game_name: r.get(1)?, previous_scheme: r.get(2)?, applied_scheme: r.get(3)?, status: r.get(4)? })).optional()
}

pub fn sessions(db: &Connection) -> Result<Vec<Session>> {
    let mut query = db.prepare(
        "SELECT s.id,s.game_id,g.name,s.started_at,s.ended_at,s.status,s.restoration_result,s.profile_preset
        FROM sessions s JOIN games g ON g.id=s.game_id ORDER BY s.id DESC LIMIT 100",
    )?;
    let rows = query.query_map([], |r| {
        Ok(Session {
            id: r.get(0)?,
            game_id: r.get(1)?,
            game_name: r.get(2)?,
            profile_preset: r.get(7)?,
            started_at: r.get(3)?,
            ended_at: r.get(4)?,
            status: r.get(5)?,
            restoration_result: r.get(6)?,
        })
    })?;
    rows.collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pending_session_survives_reopen() {
        let path = std::env::temp_dir().join(format!("gamebooster-test-{}.db", std::process::id()));
        let _ = std::fs::remove_file(&path);
        {
            let db = Connection::open(&path).unwrap();
            migrate(&db).unwrap();
            let game = add_game(&db, "Example", "C:\\Example\\game.exe", "manual").unwrap();
            let id = start_session(&db, game, "Custom", Some("old"), Some("new")).unwrap();
            assert_eq!(pending(&db).unwrap().unwrap().session_id, id);
        }
        let db = Connection::open(&path).unwrap();
        assert_eq!(
            pending(&db).unwrap().unwrap().previous_scheme.as_deref(),
            Some("old")
        );
        assert_eq!(sessions(&db).unwrap()[0].profile_preset, "Custom");
        let _ = std::fs::remove_file(path);
    }
}
