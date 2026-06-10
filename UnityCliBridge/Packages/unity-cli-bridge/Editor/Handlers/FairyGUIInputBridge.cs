using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using FairyGUI;
using UnityCliBridge.Logging;

namespace UnityCliBridge.Handlers
{
    /// <summary>
    /// FairyGUI-specific input bridge for SLG project.
    /// FairyGUI on Windows uses Mouse.current, so Input System virtual devices cannot hit UI elements.
    /// This handler provides direct coordinate injection via Stage.SetCustomInput and
    /// text-based click via GButton.FireClick.
    /// </summary>
    public static class FairyGUIInputBridge
    {
        public static object Tap(JObject parameters)
        {
            // Accepts design resolution coordinates (same as fairygui_list_buttons returns)
            var designX = parameters?["x"]?.ToObject<float>() ?? 0f;
            var designY = parameters?["y"]?.ToObject<float>() ?? 0f;

            var root = GRoot.inst;
            if (root == null)
                return new { success = false, error = "GRoot instance not available" };

            var stage = Stage.inst;
            if (stage == null)
                return new { success = false, error = "Stage instance not available" };

            var screenW = stage.stageWidth;
            var screenH = stage.stageHeight;
            var designW = root.width;
            var designH = root.height;

            // Design (origin top-left, Y down) → Screen pixel (origin bottom-left, Y up)
            var screenX = designX * (screenW / designW);
            var screenY = screenH - designY * (screenH / designH);

            BridgeLogger.LogWarning($"[FairyGUI Tap] Design({designX}, {designY}) → Screen({screenX:F1}, {screenY:F1}) | Screen({screenW}, {screenH}) Design({designW}, {designH})");

            var screenPos = new Vector2(screenX, screenY);

            // Pre-hit test to identify the target node
            // HitTest expects Y-down coords (same direction as design), not Y-up (screen coords)
            var hitTestPos = new Vector2(designX * (screenW / designW), designY * (screenH / designH));
            string hitName = null;
            string hitPath = null;
            var hitTarget = stage.HitTest(hitTestPos, true);
            if (hitTarget != null && hitTarget.gOwner != null)
            {
                hitName = hitTarget.gOwner.name;
                hitPath = GetObjectPath(hitTarget.gOwner);
            }

            BridgeLogger.LogWarning($"[FairyGUI Tap] HitTest: name={hitName ?? "(none)"} path={hitPath ?? "(none)"}");

            stage.SetCustomInput(screenPos, true, false);

            UnityEditor.EditorApplication.delayCall += () =>
            {
                stage.SetCustomInput(screenPos, false, true);

                UnityEditor.EditorApplication.delayCall += () =>
                {
                    stage.SetCustomInput(Vector2.zero, false, false);
                };
            };

            return new
            {
                success = true,
                designX,
                designY,
                screenX,
                screenY,
                screenWidth = screenW,
                screenHeight = screenH,
                designWidth = designW,
                designHeight = designH,
                hitName,
                hitPath
            };
        }

        public static object ClickByText(JObject parameters)
        {
            var text = parameters?["text"]?.ToString();
            if (string.IsNullOrEmpty(text))
            {
                return new { success = false, error = "text parameter is required" };
            }

            var root = GRoot.inst;
            if (root == null)
            {
                return new { success = false, error = "GRoot instance not available" };
            }

            BridgeLogger.Log($"[FairyGUI ClickByText] Searching for text '{text}'");

            GButton targetButton = null;
            var searchStack = new Stack<GComponent>();
            searchStack.Push(root);

            while (searchStack.Count > 0)
            {
                var component = searchStack.Pop();
                var children = component.GetChildren();
                for (int i = 0; i < children.Length; i++)
                {
                    var child = children[i];
                    if (child is GButton button && !string.IsNullOrEmpty(button.title) &&
                        button.title.Contains(text))
                    {
                        targetButton = button;
                        BridgeLogger.Log($"[FairyGUI ClickByText] Found button: {button.name} at {GetObjectPath(button)}");
                        break;
                    }

                    if (child is GComponent childComponent)
                    {
                        searchStack.Push(childComponent);
                    }
                }

                if (targetButton != null) break;
            }

            if (targetButton == null)
            {
                BridgeLogger.Log($"[FairyGUI ClickByText] No button found with text containing '{text}'");
                return new { success = false, error = $"No button found with text containing '{text}'" };
            }

            InvokeFireClick(targetButton);
            BridgeLogger.Log($"[FairyGUI ClickByText] FireClick dispatched for '{targetButton.title}'");

            return new
            {
                success = true,
                buttonText = targetButton.title,
                buttonName = targetButton.name,
                buttonPath = GetObjectPath(targetButton)
            };
        }

        public static object ListButtons(JObject parameters)
        {
            var root = GRoot.inst;
            if (root == null)
            {
                return new { success = false, error = "GRoot instance not available" };
            }

            var buttons = new List<object>();
            var searchStack = new Stack<GComponent>();
            searchStack.Push(root);

            while (searchStack.Count > 0)
            {
                var component = searchStack.Pop();
                var children = component.GetChildren();
                for (int i = 0; i < children.Length; i++)
                {
                    var child = children[i];
                    if (child is GButton button)
                    {
                        var globalPos = button.LocalToRoot(Vector2.zero, root);
                        buttons.Add(new
                        {
                            name = button.name,
                            title = button.title,
                            visible = button.visible,
                            x = globalPos.x,
                            y = globalPos.y,
                            width = button.width,
                            height = button.height,
                            path = GetObjectPath(button)
                        });
                    }

                    if (child is GComponent childComponent)
                    {
                        searchStack.Push(childComponent);
                    }
                }
            }

            return new
            {
                success = true,
                count = buttons.Count,
                contentRectWidth = root.width,
                contentRectHeight = root.height,
                buttons
            };
        }

        /// <summary>
        /// Invoke FireClick via reflection from the FairyGUI assembly.
        /// Required because HybridCLR hotfix code cannot reliably call base class methods
        /// through polymorphism on AOT-compiled FairyGUI types.
        /// </summary>
        private static void InvokeFireClick(GButton button)
        {
            try
            {
                var gbuttonType = typeof(GButton);
                var method = gbuttonType.GetMethod("FireClick",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(bool), typeof(bool) },
                    null);

                if (method != null)
                {
                    method.Invoke(button, new object[] { true, true });
                }
                else
                {
                    BridgeLogger.LogWarning("FireClick(bool, bool) not found via reflection, trying single param");
                    var method1 = gbuttonType.GetMethod("FireClick",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(bool) },
                        null);
                    method1?.Invoke(button, new object[] { true });
                }
            }
            catch (Exception ex)
            {
                BridgeLogger.LogError($"Failed to invoke FireClick: {ex.Message}");
            }
        }

        private static string DescribeTarget(DisplayObject obj)
        {
            if (obj == null) return "null";
            var type = obj.GetType().Name;
            var name = obj.name ?? "(no-name)";
            return $"{type} name={name}";
        }

        private static string DescribeGObject(GObject obj)
        {
            if (obj == null) return "null";
            var type = obj.GetType().Name;
            var name = obj.name ?? "(no-name)";
            var title = (obj is GButton btn) ? btn.title : "";
            return $"{type} name={name} title='{title}' path={GetObjectPath(obj)}";
        }

        private static string GetObjectPath(GObject obj)
        {
            var parts = new List<string>();
            var current = obj;
            while (current != null)
            {
                parts.Insert(0, current.name ?? current.GetType().Name);
                current = current.parent;
            }
            return string.Join("/", parts);
        }
    }
}
