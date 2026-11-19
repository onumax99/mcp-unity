using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace McpUnity.Tools
{
    internal sealed class ScreenshotTool : McpTool
    {
        public override string Name => "screenshot";
        public override string Description =>
            "Render an image so you can see the scene. source='game' renders a scene camera (Camera.main by default, or 'camera'); " +
            "source='scene' renders from the Scene view's current viewpoint. Screen-space overlay UI and gizmos are not included. " +
            "Optionally also saves the PNG inside the project.";
        public override bool ReadOnly => true;

        public override JObject InputSchema => Schema.Object()
            .Enum("source", "What to render (default 'game').", new[] { "game", "scene" })
            .Str("camera", "For source='game': the GameObject with the Camera (id, path or name). Default: Camera.main.")
            .Int("width", "Image width in pixels (default 960, max 2048).")
            .Int("height", "Image height in pixels (default 540, max 2048).")
            .Str("savePath", "Also save the PNG to this project-relative path, e.g. 'Screenshots/shot.png'.");

        protected override object Execute(JObject args, ToolContext context)
        {
            int width = Mathf.Clamp(args.GetInt("width", 960), 16, 2048);
            int height = Mathf.Clamp(args.GetInt("height", 540), 16, 2048);
            var source = args.GetString("source", "game").ToLowerInvariant();

            Camera camera;
            GameObject temporary = null;
            string cameraName;
            if (source == "scene")
            {
                var view = SceneView.lastActiveSceneView ?? SceneView.sceneViews.OfType<SceneView>().FirstOrDefault();
                if (view == null || view.camera == null)
                    throw new McpToolException("No Scene view is open. Open one (Window > General > Scene) or use source='game'.");
                temporary = new GameObject("__MCP_ScreenshotCamera") { hideFlags = HideFlags.HideAndDontSave };
                camera = temporary.AddComponent<Camera>();
                camera.CopyFrom(view.camera);
                camera.cameraType = CameraType.Game;
                camera.enabled = false;
                cameraName = "Scene view";
            }
            else
            {
                camera = FindCamera(args);
                cameraName = camera.name;
            }

            try
            {
                var png = Render(camera, width, height);
                string savedTo = null;
                var savePath = args.GetString("savePath");
                if (!string.IsNullOrEmpty(savePath))
                {
                    savedTo = savePath.Replace('\\', '/').TrimStart('/');
                    if (!savedTo.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase)) savedTo += ".png";
                    if (savedTo.Contains("..")) throw new McpToolException("savePath must stay inside the project.");
                    var full = Path.Combine(McpSettings.ProjectRoot, savedTo);
                    Directory.CreateDirectory(Path.GetDirectoryName(full) ?? McpSettings.ProjectRoot);
                    File.WriteAllBytes(full, png);
                    if (savedTo.StartsWith("Assets/")) AssetDatabase.ImportAsset(savedTo);
                }
                return new ToolResult()
                    .AddImage(png)
                    .AddJson(new { source, camera = cameraName, width, height, savedTo });
            }
            finally
            {
                if (temporary) Object.DestroyImmediate(temporary);
            }
        }

        static Camera FindCamera(JObject args)
        {
            if (args.Has("camera"))
            {
                var go = SceneQuery.Resolve(args.Arg("camera"));
                var cam = go.GetComponent<Camera>();
                if (cam == null) throw new McpToolException($"'{go.name}' has no Camera component.");
                return cam;
            }
            if (Camera.main != null) return Camera.main;
            var any = Camera.allCameras
                .OrderByDescending(c => c.depth)
                .FirstOrDefault(c => c.enabled);
            if (any == null) throw new McpToolException("The scene has no enabled camera. Pass 'camera' or use source='scene'.");
            return any;
        }

        static byte[] Render(Camera camera, int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            Texture2D texture = null;
            try
            {
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                return texture.EncodeToPNG();
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);
                if (texture) Object.DestroyImmediate(texture);
            }
        }
    }
}
