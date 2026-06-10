using System;
using System.Collections.Generic;

namespace UnityCliBridge.Helpers
{
    /// <summary>
    /// Decides which Unity CLI Bridge commands are safe to execute during Play Mode.
    /// We allow read-only/status/log/simulation; we block heavy write/refresh/import/scene-modifying operations.
    /// </summary>
    public static class PlayModeCommandPolicy
    {
        /// <summary>
        /// Commands that require Play Mode to function (e.g. FairyGUI input simulation, screenshots).
        /// Calling these while NOT in Play Mode will return an error.
        /// </summary>
        private static readonly HashSet<string> RequirePlayMode = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fairygui_tap", "fairygui_click_by_text", "fairygui_list_buttons",
            "capture_screenshot",
            // SLG build state query
            "query_build_state", "query_build_detail",
        };

        private static readonly HashSet<string> AllowedInPlay = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Status/Info
            "ping", "get_editor_state", "get_compilation_state", "read_console", "clear_logs",
            // Simulation
            "input_mouse", "input_keyboard", "input_touch", "input_gamepad",
            // UI simple interactions
            "click_ui_element", "set_ui_element_value", "simulate_ui_input",
            // FairyGUI (SLG project)
            "fairygui_tap", "fairygui_click_by_text", "fairygui_list_buttons",
            // SLG build/worker state query
            "query_build_state", "query_build_detail",
            // Animator queries
            "get_animator_state", "get_animator_runtime_info",
            // Screenshot / Video (game/scene)
            "capture_screenshot", "analyze_screenshot",
            "capture_video_start", "capture_video_stop", "capture_video_status",
            // Window/selection queries
            "get_hierarchy", "get_scene_info", "get_gameobject_details", "find_gameobject", "find_by_component",
            // PlayMode controls
            "play_game", "pause_game", "stop_game",
        };

        /// <summary>
        /// Returns true if the command type is allowed to run during Play Mode.
        /// </summary>
        public static bool IsAllowed(string commandType)
        {
            if (string.IsNullOrEmpty(commandType)) return false;
            if (AllowedInPlay.Contains(commandType)) return true;

            // Heuristic: Block script_* modification operations during Play
            if (commandType.StartsWith("script_", StringComparison.OrdinalIgnoreCase)) return false;

            // Block heavy asset DB operations during Play
            if (commandType.Equals("manage_asset_database", StringComparison.OrdinalIgnoreCase)) return false;
            if (commandType.Equals("analyze_asset_dependencies", StringComparison.OrdinalIgnoreCase)) return false;
            if (commandType.Equals("create_animator_controller", StringComparison.OrdinalIgnoreCase)) return false;
            if (commandType.Equals("create_animation_clip", StringComparison.OrdinalIgnoreCase)) return false;
            if (commandType.Equals("create_sprite_atlas", StringComparison.OrdinalIgnoreCase)) return false;

            // Block project settings and package manager changes during Play
            if (commandType.Equals("update_project_settings", StringComparison.OrdinalIgnoreCase)) return false;
            if (commandType.Equals("package_manager", StringComparison.OrdinalIgnoreCase)) return false;
            if (commandType.Equals("registry_config", StringComparison.OrdinalIgnoreCase)) return false;
            if (commandType.Equals("asset_import_settings", StringComparison.OrdinalIgnoreCase)) return false;

            // Scene structure modifications are risky; block by default
            if (commandType.Equals("create_scene", StringComparison.OrdinalIgnoreCase)) return false;
            if (commandType.Equals("load_scene", StringComparison.OrdinalIgnoreCase)) return false;
            if (commandType.Equals("save_scene", StringComparison.OrdinalIgnoreCase)) return false;
            if (commandType.Equals("create_gameobject", StringComparison.OrdinalIgnoreCase)) return false;
            if (commandType.Equals("modify_gameobject", StringComparison.OrdinalIgnoreCase)) return false;
            if (commandType.Equals("delete_gameobject", StringComparison.OrdinalIgnoreCase)) return false;

            // Default allow for other read-only queries
            return true;
        }

        /// <summary>
        /// Returns true if the command type requires Play Mode to function.
        /// </summary>
        public static bool RequiresPlayMode(string commandType)
        {
            return !string.IsNullOrEmpty(commandType) && RequirePlayMode.Contains(commandType);
        }
    }
}
