// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;

namespace Community.PowerToys.Run.Plugin.VSCodeWorkspaces.VSCodeHelper
{
    public static class VSCodeInstances
    {
        private static string _systemPath = string.Empty;
        private static readonly string? _userAppDataPath = Environment.GetEnvironmentVariable("AppData");
        private static readonly object _loadLock = new object();
        private static Task? _loadingTask;
        private static DateTime _lastLoadTime = DateTime.MinValue;
        private static readonly TimeSpan _loadInterval = TimeSpan.FromMinutes(5); // Reload every 5 minutes

        public static List<VSCodeInstance> Instances { get; set; } = new();

        private static BitmapImage Bitmap2BitmapImage(Bitmap bitmap)
        {
            using (var memory = new MemoryStream())
            {
                bitmap.Save(memory, ImageFormat.Png);
                memory.Position = 0;

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memory;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                return bitmapImage;
            }
        }

        private static Bitmap BitmapOverlayToCenter(Bitmap bitmap1, Bitmap overlayBitmap)
        {
            int bitmap1Width = bitmap1.Width; 
            int bitmap1Height = bitmap1.Height;

            Bitmap overlayBitmapResized = new Bitmap(overlayBitmap, new System.Drawing.Size(bitmap1Width / 2, bitmap1Height / 2));

            float marginLeft = (float)((bitmap1Width * 0.7) - (overlayBitmapResized.Width * 0.5));
            float marginTop = (float)((bitmap1Height * 0.7) - (overlayBitmapResized.Height * 0.5));

            Bitmap finalBitmap = new Bitmap(bitmap1Width, bitmap1Height);
            using (Graphics g = Graphics.FromImage(finalBitmap))
            {
                g.DrawImage(bitmap1, System.Drawing.Point.Empty);
                g.DrawImage(overlayBitmapResized, marginLeft, marginTop);
            }

            return finalBitmap;
        }

        // Gets the executablePath and AppData foreach instance of VSCode
        public static void LoadVSCodeInstances()
        {
            lock (_loadLock)
            {
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                var currentTime = DateTime.Now;

                // Skip loading if PATH hasn't changed and we loaded recently
                if (_systemPath == currentPath && currentTime - _lastLoadTime < _loadInterval)
                    return;

                LoadVSCodeInstancesInternal();
                _lastLoadTime = currentTime;
            }
        }

        public static async Task LoadVSCodeInstancesAsync()
        {
            if (_loadingTask != null && !_loadingTask.IsCompleted)
            {
                await _loadingTask;
                return;
            }

            _loadingTask = Task.Run(LoadVSCodeInstances);
            await _loadingTask;
        }

        private static void LoadVSCodeInstancesInternal()
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            
            if (_systemPath == currentPath && Instances.Count > 0)
                return;

            Instances = new List<VSCodeInstance>();
            _systemPath = currentPath;
            
            var paths = _systemPath.Split(";").Where(x =>
                x.Contains("VS Code", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("codium", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("vscode", StringComparison.OrdinalIgnoreCase));
            foreach (var path in paths)
            {
                if (!Directory.Exists(path))
                    continue;

                var newPath = path;
                if (!Path.GetFileName(path).Equals("bin", StringComparison.OrdinalIgnoreCase))
                    newPath = Path.Combine(path, "bin");

                if (!Directory.Exists(newPath))
                    continue;

                var files = Directory.EnumerateFiles(newPath).Where(x =>
                    (x.Contains("code", StringComparison.OrdinalIgnoreCase) ||
                     x.Contains("codium", StringComparison.OrdinalIgnoreCase))
                    && !x.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)).ToArray();

                var iconPath = Path.GetDirectoryName(newPath);
                if (iconPath == null) continue;

                if (files.Length <= 0)
                    continue;

                var file = files[0];
                var version = string.Empty;

                var instance = new VSCodeInstance
                {
                    ExecutablePath = file,
                };

                if (file.EndsWith("code"))
                {
                    version = "Code";
                    instance.VSCodeVersion = VSCodeVersion.Stable;
                }
                else if (file.EndsWith("code-insiders"))
                {
                    version = "Code - Insiders";
                    instance.VSCodeVersion = VSCodeVersion.Insiders;
                }
                else if (file.EndsWith("code-exploration"))
                {
                    version = "Code - Exploration";
                    instance.VSCodeVersion = VSCodeVersion.Exploration;
                }
                else if (file.EndsWith("codium"))
                {
                    version = "VSCodium";
                    instance.VSCodeVersion = VSCodeVersion.Stable;
                }

                if (version == string.Empty)
                    continue;

                if (_userAppDataPath == null) continue;

                var portableData = Path.Join(iconPath, "data");
                instance.AppData = Directory.Exists(portableData) ? Path.Join(portableData, "user-data") : Path.Combine(_userAppDataPath, version);
                var iconVSCode = Path.Join(iconPath, $"{version}.exe");

                var bitmapIconVscode = Icon.ExtractAssociatedIcon(iconVSCode)?.ToBitmap();
                if (bitmapIconVscode == null) continue;

                // Compose overlay icons and persist them to a temp directory so PowerToys Run can load them by path
                var assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (assemblyLocation == null) continue;

                var imagesDir = Path.Combine(assemblyLocation, "Images");
                var folderPng = Path.Combine(imagesDir, "folder.png");
                var monitorPng = Path.Combine(imagesDir, "monitor.png");

                // Fallback if images not found
                if (!File.Exists(folderPng) || !File.Exists(monitorPng))
                {
                    Instances.Add(instance);
                    continue;
                }

                using var folderIcon = (Bitmap)Image.FromFile(folderPng);
                using var monitorIcon = (Bitmap)Image.FromFile(monitorPng);

                using var workspaceOverlay = BitmapOverlayToCenter(folderIcon, bitmapIconVscode);
                using var remoteOverlay = BitmapOverlayToCenter(monitorIcon, bitmapIconVscode);

                // Create a per-process temp directory to avoid name collisions and allow cleanup on restart
                var tempRoot = Path.Combine(Path.GetTempPath(), "PTVSCodeWSIcons");
                Directory.CreateDirectory(tempRoot);

                // Unique prefix per executable so stable/insiders/exploration don't overwrite each other
                var prefix = instance.VSCodeVersion.ToString().ToLowerInvariant();
                var workspaceIconFile = Path.Combine(tempRoot, $"{prefix}_workspace.png");
                var remoteIconFile = Path.Combine(tempRoot, $"{prefix}_remote.png");

                try
                {
                    workspaceOverlay.Save(workspaceIconFile, ImageFormat.Png);
                    remoteOverlay.Save(remoteIconFile, ImageFormat.Png);
                    instance.WorkspaceIcon = workspaceIconFile;
                    instance.RemoteIcon = remoteIconFile;
                }
                catch
                {
                    // On failure, fall back to original base icons
                    instance.WorkspaceIcon = folderPng;
                    instance.RemoteIcon = monitorPng;
                }

                // Keep BitmapImage versions if some future UI consumer needs them (not used by PowerToys Run currently)
                instance.WorkspaceIconBitMap = Bitmap2BitmapImage((Bitmap)Image.FromFile(instance.WorkspaceIcon));
                instance.RemoteIconBitMap = Bitmap2BitmapImage((Bitmap)Image.FromFile(instance.RemoteIcon));

                Instances.Add(instance);
            }
        }
    }
}