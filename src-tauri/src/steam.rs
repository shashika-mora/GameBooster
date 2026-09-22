use std::path::{Path, PathBuf};

#[derive(Debug)]
pub struct DiscoveredGame {
    pub name: String,
    pub install_path: String,
    pub executable: Option<String>,
}

fn quoted_field(text: &str, field: &str) -> Option<String> {
    text.lines().find_map(|line| {
        let parts: Vec<&str> = line.split('"').collect();
        if parts.len() >= 4 && parts[1].eq_ignore_ascii_case(field) {
            Some(parts[3].to_owned())
        } else {
            None
        }
    })
}

fn likely_executable(dir: &Path, name: &str) -> Option<String> {
    let expected = name.to_ascii_lowercase().replace(' ', "");
    let mut candidates = Vec::new();
    for entry in std::fs::read_dir(dir).ok()? {
        let path = entry.ok()?.path();
        if path
            .extension()
            .and_then(|x| x.to_str())
            .is_some_and(|x| x.eq_ignore_ascii_case("exe"))
        {
            let stem = path
                .file_stem()?
                .to_string_lossy()
                .to_ascii_lowercase()
                .replace(' ', "");
            if stem == expected {
                return Some(path.to_string_lossy().into_owned());
            }
            if !stem.contains("uninstall") && !stem.contains("setup") && !stem.contains("launcher")
            {
                candidates.push(path);
            }
        }
    }
    (candidates.len() == 1).then(|| candidates[0].to_string_lossy().into_owned())
}

pub fn scan() -> Vec<DiscoveredGame> {
    let mut roots = Vec::new();
    for key in ["PROGRAMFILES(X86)", "PROGRAMFILES"] {
        if let Some(value) = std::env::var_os(key) {
            roots.push(PathBuf::from(value).join("Steam"));
        }
    }
    let mut libraries = roots.clone();
    for root in roots {
        let path = root.join("steamapps/libraryfolders.vdf");
        if let Ok(text) = std::fs::read_to_string(path) {
            for line in text.lines() {
                let parts: Vec<&str> = line.split('"').collect();
                if parts.len() >= 4 && parts[1].eq_ignore_ascii_case("path") {
                    libraries.push(PathBuf::from(parts[3].replace("\\\\", "\\")));
                }
            }
        }
    }
    libraries.sort();
    libraries.dedup();
    let mut result = Vec::new();
    for library in libraries {
        let steamapps = library.join("steamapps");
        let Ok(entries) = std::fs::read_dir(&steamapps) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if !path
                .file_name()
                .is_some_and(|n| n.to_string_lossy().starts_with("appmanifest_"))
                || path.extension().and_then(|x| x.to_str()) != Some("acf")
            {
                continue;
            }
            let Ok(text) = std::fs::read_to_string(path) else {
                continue;
            };
            let (Some(name), Some(folder)) = (
                quoted_field(&text, "name"),
                quoted_field(&text, "installdir"),
            ) else {
                continue;
            };
            let install = steamapps.join("common").join(folder);
            if !install.is_dir() {
                continue;
            }
            result.push(DiscoveredGame {
                name: name.clone(),
                executable: likely_executable(&install, &name),
                install_path: install.to_string_lossy().into_owned(),
            });
        }
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn parses_manifest_field() {
        assert_eq!(
            quoted_field("\"name\"  \"Portal 2\"", "name"),
            Some("Portal 2".into())
        );
    }
}
