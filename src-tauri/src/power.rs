use std::process::Command;

fn guid_in(text: &str) -> Option<String> {
    text.split(|c: char| !c.is_ascii_hexdigit() && c != '-')
        .find_map(|part| {
            let bytes = part.as_bytes();
            (bytes.len() == 36
                && [8, 13, 18, 23].iter().all(|&i| bytes[i] == b'-')
                && bytes
                    .iter()
                    .enumerate()
                    .all(|(i, b)| [8, 13, 18, 23].contains(&i) || b.is_ascii_hexdigit()))
            .then(|| part.to_ascii_lowercase())
        })
}

pub fn valid_guid(value: &str) -> bool {
    guid_in(value).is_some_and(|guid| guid.eq_ignore_ascii_case(value))
}

pub fn active() -> Result<String, String> {
    let output = Command::new("powercfg")
        .arg("/getactivescheme")
        .output()
        .map_err(|e| e.to_string())?;
    if !output.status.success() {
        return Err("Cannot read the active Windows power scheme".into());
    }
    let text = String::from_utf8_lossy(&output.stdout);
    guid_in(&text).ok_or_else(|| "Windows did not return a power scheme GUID".into())
}

pub fn set(guid: &str) -> Result<(), String> {
    if !valid_guid(guid) {
        return Err("Invalid power scheme GUID".into());
    }
    let output = Command::new("powercfg")
        .args(["/setactive", guid])
        .output()
        .map_err(|e| e.to_string())?;
    if !output.status.success() {
        return Err("Windows rejected the power scheme change".into());
    }
    if active()? != guid.to_ascii_lowercase() {
        return Err("Power scheme change could not be verified".into());
    }
    Ok(())
}

#[derive(Debug, PartialEq, Eq)]
enum RestoreDecision {
    NoChange,
    AlreadyRestored,
    ApplyPrevious,
    Conflict,
}

fn restore_decision(
    previous: Option<&str>,
    applied: Option<&str>,
    current: Option<&str>,
) -> RestoreDecision {
    match (previous, applied) {
        (Some(old), Some(new)) if !old.eq_ignore_ascii_case(new) => match current {
            Some(value) if value.eq_ignore_ascii_case(old) => RestoreDecision::AlreadyRestored,
            Some(value) if value.eq_ignore_ascii_case(new) => RestoreDecision::ApplyPrevious,
            _ => RestoreDecision::Conflict,
        },
        _ => RestoreDecision::NoChange,
    }
}

pub fn restore(previous: Option<&str>, applied: Option<&str>) -> Result<String, String> {
    if restore_decision(previous, applied, None) == RestoreDecision::NoChange {
        return Ok("No system setting changed".into());
    }
    let current = active()?;
    match restore_decision(previous, applied, Some(&current)) {
        RestoreDecision::AlreadyRestored => Ok("Already restored".into()),
        RestoreDecision::ApplyPrevious => {
            set(previous.expect("previous scheme required for restore"))?;
            Ok("Original power plan restored and verified".into())
        }
        RestoreDecision::Conflict => Err("The power plan changed outside GameBooster; restore it manually or confirm the current plan before retrying".into()),
        RestoreDecision::NoChange => Ok("No system setting changed".into()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn extracts_guid_without_localized_label() {
        assert_eq!(
            guid_in("Scheme: 381b4222-f694-41f0-9685-ff5bb260df2e (Balanced)").as_deref(),
            Some("381b4222-f694-41f0-9685-ff5bb260df2e")
        );
        assert!(!valid_guid("& calc.exe"));
    }
    #[test]
    fn restoration_decisions_preserve_external_changes() {
        let old = Some("381b4222-f694-41f0-9685-ff5bb260df2e");
        let new = Some("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        assert_eq!(
            restore_decision(old, new, new),
            RestoreDecision::ApplyPrevious
        );
        assert_eq!(
            restore_decision(old, new, old),
            RestoreDecision::AlreadyRestored
        );
        assert_eq!(
            restore_decision(old, new, Some("unexpected")),
            RestoreDecision::Conflict
        );
        assert_eq!(restore_decision(old, None, None), RestoreDecision::NoChange);
    }
}
