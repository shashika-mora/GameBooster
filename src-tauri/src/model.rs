use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Game {
    pub id: i64,
    pub name: String,
    pub executable: Option<String>,
    pub install_path: Option<String>,
    pub source: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Profile {
    pub game_id: i64,
    pub preset: String,
    pub power_scheme: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Session {
    pub id: i64,
    pub game_id: i64,
    pub game_name: String,
    pub profile_preset: String,
    pub started_at: String,
    pub ended_at: Option<String>,
    pub status: String,
    pub restoration_result: Option<String>,
}

#[cfg(test)]
mod tests {
    use super::Profile;

    #[test]
    fn profile_round_trip_uses_frontend_field_names() {
        let value = Profile {
            game_id: 7,
            preset: "Custom".into(),
            power_scheme: Some("381b4222-f694-41f0-9685-ff5bb260df2e".into()),
        };
        let json = serde_json::to_string(&value).unwrap();
        assert!(json.contains("gameId"));
        assert!(json.contains("powerScheme"));
        let decoded: Profile = serde_json::from_str(&json).unwrap();
        assert_eq!(decoded.game_id, value.game_id);
        assert_eq!(decoded.power_scheme, value.power_scheme);
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MonitorSample {
    pub cpu_percent: f32,
    pub used_memory_bytes: u64,
    pub total_memory_bytes: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Recovery {
    pub session_id: i64,
    pub game_name: String,
    pub previous_scheme: Option<String>,
    pub applied_scheme: Option<String>,
    pub status: String,
}
