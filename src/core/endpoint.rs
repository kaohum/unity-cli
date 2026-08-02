use std::path::{Path, PathBuf};

use anyhow::Result;

use crate::core::config::{default_host, default_port};
use crate::core::instances;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResolvedEndpoint {
    pub host: String,
    pub port: u16,
}

pub fn resolve_endpoint(
    host_override: Option<String>,
    port_override: Option<u16>,
) -> Result<ResolvedEndpoint> {
    let host = host_override.map(|value| value.trim().to_string());
    let env_host = crate::core::config::read_env(&["UNITY_CLI_HOST"]);
    let env_port = crate::core::config::read_env_u16("UNITY_CLI_PORT");

    if let Some(port) = port_override.or(env_port) {
        return Ok(ResolvedEndpoint {
            host: host.or(env_host).unwrap_or_else(default_host),
            port,
        });
    }

    if host.is_none() && env_host.is_none() {
        if let Some((active_host, active_port)) = instances::active_endpoint()? {
            return Ok(ResolvedEndpoint {
                host: active_host,
                port: active_port,
            });
        }
    }

    // ②' 按 Unity 工程路径派生端口（与 UnityCliBridge.CalculatePortFromProjectPath 同算法）
    //    让 unity-cli 自给自足：无需 env / 启动器，按所在工程算出与 bridge 同一个端口。
    //    host 用 127.0.0.1（bridge 默认 bind），与 bridge 对齐，避免 localhost→IPv6 的解析坑。
    if let Some(project) = detect_unity_project_root() {
        return Ok(ResolvedEndpoint {
            host: host.or(env_host).unwrap_or_else(|| "127.0.0.1".to_string()),
            port: port_from_project_path(&project),
        });
    }

    Ok(ResolvedEndpoint {
        host: host.or(env_host).unwrap_or_else(default_host),
        port: default_port(),
    })
}

/// 探测当前 Unity 工程根（含 `Assets/` 的目录）。
fn detect_unity_project_root() -> Option<PathBuf> {
    // 优先：unity-cli 装在 <工程>/.unity-cli/unity-cli → 二进制的 grandparent 即工程根
    if let Ok(exe) = std::env::current_exe() {
        if let Some(project) = exe.parent().and_then(|p| p.parent()) {
            if project.join("Assets").is_dir() {
                return Some(project.to_path_buf());
            }
        }
    }
    // 其次：从 CWD 向上找含 Assets/ 的目录（CWD 在工程内时）
    let cwd = std::env::current_dir().ok()?;
    let mut cur: &Path = cwd.as_path();
    loop {
        if cur.join("Assets").is_dir() {
            return Some(cur.to_path_buf());
        }
        match cur.parent() {
            Some(p) => cur = p,
            None => return None,
        }
    }
}

/// 复刻 UnityCliBridgeSettings.CalculatePortFromProjectPath：
/// 路径 `/`→`\` 后逐字符 (int)c 求和，(sum % 100) + 6400。
fn port_from_project_path(project_root: &Path) -> u16 {
    let normalized = project_root.to_string_lossy().replace('/', "\\");
    let sum: u64 = normalized.chars().map(|c| c as u64).sum();
    ((sum % 100) + 6400) as u16
}

#[cfg(test)]
mod tests {
    use super::{port_from_project_path, resolve_endpoint};
    use std::path::Path;

    #[test]
    fn port_matches_bridge_algorithm() {
        // D:\work\slg\client\game → 6420（对齐 UnityCliBridge.CalculatePortFromProjectPath 的实测值）
        assert_eq!(port_from_project_path(Path::new(r"D:\work\slg\client\game")), 6420);
    }

    #[test]
    fn port_normalizes_slashes_like_bridge() {
        // bridge 在哈希前做 Replace('/', '\\')，正反斜杠需等价
        assert_eq!(
            port_from_project_path(Path::new("D:/work/slg/client/game")),
            port_from_project_path(Path::new(r"D:\work\slg\client\game"))
        );
    }

    #[test]
    fn cli_override_wins_over_active_instance() {
        let _guard = crate::test_env::env_lock()
            .lock()
            .unwrap_or_else(|poison| poison.into_inner());
        std::env::set_var("UNITY_CLI_HOST", "env-host");
        std::env::set_var("UNITY_CLI_PORT", "7777");
        let value = resolve_endpoint(Some("cli-host".to_string()), Some(9999))
            .expect("endpoint should resolve");
        assert_eq!(value.host, "cli-host");
        assert_eq!(value.port, 9999);
        std::env::remove_var("UNITY_CLI_HOST");
        std::env::remove_var("UNITY_CLI_PORT");
    }

    #[test]
    fn env_port_wins_when_no_cli_override() {
        let _guard = crate::test_env::env_lock()
            .lock()
            .unwrap_or_else(|poison| poison.into_inner());
        std::env::set_var("UNITY_CLI_HOST", "env-host");
        std::env::set_var("UNITY_CLI_PORT", "7777");
        let value = resolve_endpoint(None, None).expect("endpoint should resolve");
        assert_eq!(value.host, "env-host");
        assert_eq!(value.port, 7777);
        std::env::remove_var("UNITY_CLI_HOST");
        std::env::remove_var("UNITY_CLI_PORT");
    }
}
