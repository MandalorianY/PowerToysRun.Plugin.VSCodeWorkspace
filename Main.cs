// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using Wox.Plugin;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.VSCodeWorkspaces
{
    using Properties;
    using RemoteMachinesHelper;
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using VSCodeHelper;
    using WorkspacesHelper;

    public class Main : IPlugin, IContextMenu, ISettingProvider, IDisposable
    {
        public static string PluginID => "525995402BEF4A8CA860D92F6D108092";

        public string Name => "VS Code Workspaces";
        public string Description => "Opens workspaces, remote machines (SSH or Codespaces) and containers, previously opened in VS Code.";
        public IEnumerable<PluginAdditionalOption> AdditionalOptions => new[]
        {
            new PluginAdditionalOption
            {
                Key = nameof(DiscoverWorkspaces),
                DisplayLabel = "Discover Workspaces",
                DisplayDescription = "Automatically discover previously opened VS Code workspaces",
                PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Checkbox,
                Value = DiscoverWorkspaces,
            },
            new PluginAdditionalOption
            {
                Key = nameof(DiscoverMachines),
                DisplayLabel = "Discover Remote Machines",
                DisplayDescription = "Automatically discover SSH remote machines and containers",
                PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Checkbox,
                Value = DiscoverMachines,
            }
        };

        private bool DiscoverWorkspaces { get; set; } = true;

        private bool DiscoverMachines { get; set; } = true;

        // Made static so other helper classes can log/errors via Main.Context
        public static PluginInitContext? Context { get; set; }

        private string? IconPath { get; set; }

        private bool Disposed { get; set; }

        private VSCodeInstance? _defaultInstance;

        private readonly VSCodeWorkspacesApi _workspacesApi = new();

        private readonly VSCodeRemoteMachinesApi _machinesApi = new();

        /// Return a filtered list, based on the given query.
        public List<Result> Query(Query query)
        {
            var results = new List<Result>();

            VSCodeInstances.LoadVSCodeInstances();

            // User defined extra workspaces (if needed in future)
            // if (_defaultInstance != null)
            // {
            //     workspaces.AddRange(_settings.CustomWorkspaces.Select(uri =>
            //         VSCodeWorkspacesApi.ParseVSCodeUri(uri, _defaultInstance)));
            // }

            // Search opened workspaces
            if (DiscoverWorkspaces)
            {
                var workspaceResults = _workspacesApi.Workspaces.Select(CreateWorkspaceResult).ToList();
                Log.Info("Adding workspace results: " + string.Join(" | ", workspaceResults.Select((r, i) => $"#{i}:{r.Title}")), GetType());
                results.AddRange(workspaceResults);
            }

            if (DiscoverMachines)
            {
                foreach (var a in _machinesApi.Machines)
                {
                    var title = $"{a.Host}";

                    if (!string.IsNullOrEmpty(a.User) && !string.IsNullOrEmpty(a.HostName))
                    {
                        title += $" [{a.User}@{a.HostName}]";
                    }

                    var tooltip = Resources.SSHRemoteMachine;

                    var instanceIcon = a.VSCodeInstance?.RemoteIcon ?? IconPath ?? string.Empty;
                    var machineResult = new Result
                    {
                        QueryTextDisplay = query.Search,
                        IcoPath = instanceIcon,
                        Title = title,
                        SubTitle = Resources.SSHRemoteMachine,
                        ToolTipData = new ToolTipData("SSH Remote Machine", tooltip),
                        Action = _ =>
                        {
                            try
                            {
                                var process = new ProcessStartInfo
                                {
                                    FileName = a.VSCodeInstance?.ExecutablePath ?? string.Empty,
                                    UseShellExecute = true,
                                    Arguments =
                                        $"--new-window --enable-proposed-api ms-vscode-remote.remote-ssh --remote ssh-remote+{((char)34) + a.Host + ((char)34)}",
                                    WindowStyle = ProcessWindowStyle.Hidden,
                                };
                                Process.Start(process);

                                return true;
                            }
                            catch (Win32Exception ex)
                            {
                                Log.Error($"Failed to open SSH remote machine: {ex.Message}", GetType());
                                Context?.API?.ShowMsg(Name, "Failed to open SSH remote machine", string.Empty);
                                return false;
                            }
                        },
                        ContextData = a,
                    };

                    Log.Info($"Adding machine result: {machineResult.Title}", GetType());
                    results.Add(machineResult);
                }
            }

            Log.Info("After adding remote machines:" + string.Join(" | ", results.Select((r, i) => $"#{i}:{r.Title}")), GetType());

            // If there's a search query, perform a simple case-insensitive token-based filter
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                Log.Info("Filtering with query: " + query.Search, GetType());
                var search = query.Search.Trim();
                var tokens = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                var filtered = new List<Result>();
                foreach (var r in results)
                {
                    var title = r.Title ?? string.Empty;
                    var sub = r.SubTitle ?? string.Empty;

                    // Require that every token appears in either the title or subtitle (case-insensitive)
                    var allTokensPresent = tokens.All(t =>
                        (!string.IsNullOrEmpty(title) && title.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(sub) && sub.Contains(t, StringComparison.OrdinalIgnoreCase)));

                    if (allTokensPresent)
                    {
                        filtered.Add(r);
                    }
                }

                // Preserve original ordering (assumed to be recent-first from the data source)
                results = filtered;
                Log.Info("Filtered results (preserving discovery order): " + string.Join(" | ", results.Select((r, i) => $"#{i}:{r.Title}")), GetType());
            }
            else
            {
                // No query: keep the discovery order (most recent first)
                Log.Info("Results (no query) preserving discovery order: " + string.Join(" | ", results.Select((r, i) => $"#{i}:{r.Title}")), GetType());
            }
            
            results = DeduplicateResults(results);
            return results;
        }

        private List<Result> DeduplicateResults(List<Result> results)
        {
            return results.GroupBy(r => r.Title).Select(g => g.First()).ToList();
        }

        public void Init(PluginInitContext context)
        {
            Log.Info("Init", GetType());

            Context = context ?? throw new ArgumentNullException(nameof(context));
            Context.API.ThemeChanged += OnThemeChanged;
            UpdateIconPath(Context.API.GetCurrentTheme());

            VSCodeInstances.LoadVSCodeInstances();

            // Prefer stable version, or the first one we got
            _defaultInstance = VSCodeInstances.Instances.Find(e => e.VSCodeVersion == VSCodeVersion.Stable) ??
                              VSCodeInstances.Instances.FirstOrDefault();
        }

        private Result CreateWorkspaceResult(VsCodeWorkspace ws)
        {
            var title = $"{ws.FolderName}";
            var typeWorkspace = ws.WorkspaceTypeToString();

            if (ws.WorkspaceLocation != WorkspaceLocation.Local)
            {
                title = !string.IsNullOrEmpty(ws.Label)
                    ? $"{ws.Label}"
                    : $"{title}{(!string.IsNullOrEmpty(ws.ExtraInfo) ? $" - {ws.ExtraInfo}" : string.Empty)} ({typeWorkspace})";
            }

            var tooltip =
                $"{Resources.Workspace}{(ws.WorkspaceLocation != WorkspaceLocation.Local ? $" {Resources.In} {typeWorkspace}" : string.Empty)}: {SystemPath.RealPath(ws.RelativePath)}";

            return new Result
            {
                QueryTextDisplay = ws.FolderName,
                // Use the monitor (remote) icon for non-local workspaces, folder icon for local
                IcoPath = ws.WorkspaceLocation != WorkspaceLocation.Local
                    ? ws.VSCodeInstance?.RemoteIcon ?? IconPath ?? string.Empty
                    : ws.VSCodeInstance?.WorkspaceIcon ?? IconPath ?? string.Empty,
                Title = title,
                SubTitle = tooltip,
                ToolTipData = new ToolTipData("VS Code Workspace", tooltip),
                Action = c =>
                {
                    try
                    {
                        // Check for Ctrl modifier to open in file explorer
                        if (c.SpecialKeyState.CtrlPressed)
                        {
                            return OpenFolderInExplorer(SystemPath.RealPath(ws.RelativePath));
                        }

                        var process = new ProcessStartInfo
                        {
                            FileName = ws.VSCodeInstance?.ExecutablePath ?? string.Empty,
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Hidden,
                        };

                        process.ArgumentList.Add(ws.WorkspaceType == WorkspaceType.Workspace
                            ? "--file-uri"
                            : "--folder-uri");

                        process.ArgumentList.Add(ws.Path);

                        Process.Start(process);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Failed to open workspace: {ex.Message}", GetType());
                        Context?.API?.ShowMsg(Name, "Failed to open workspace", string.Empty);
                        return false;
                    }
                },
                ContextData = ws,
            };
        }

        private bool OpenFolderInExplorer(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to open folder in explorer: {ex.Message}", GetType());
                Context?.API?.ShowMsg(Name, "Failed to open folder in explorer", string.Empty);
                return false;
            }
        }

        public Control CreateSettingPanel() => throw new NotImplementedException();

        public void UpdateSettings(PowerLauncherPluginSettings settings)
        {
            Log.Info("UpdateSettings", GetType());

            // Use FirstOrDefault instead of SingleOrDefault to avoid InvalidOperationException if duplicates exist.
            DiscoverWorkspaces = settings?.AdditionalOptions?.FirstOrDefault(x => x.Key == nameof(DiscoverWorkspaces))?.Value ?? true;
            DiscoverMachines = settings?.AdditionalOptions?.FirstOrDefault(x => x.Key == nameof(DiscoverMachines))?.Value ?? true;
        }
        public void Dispose()
        {
            Log.Info("Dispose", GetType());

            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (Disposed || !disposing)
            {
                return;
            }

            if (Context?.API != null)
            {
                Context.API.ThemeChanged -= OnThemeChanged;
            }

            Disposed = true;
        }

        private void UpdateIconPath(Theme theme) => IconPath = theme == Theme.Light || theme == Theme.HighContrastWhite ? Context?.CurrentPluginMetadata?.IcoPathLight : Context?.CurrentPluginMetadata?.IcoPathDark;

        private void OnThemeChanged(Theme currentTheme, Theme newTheme) => UpdateIconPath(newTheme);
        public List<ContextMenuResult> LoadContextMenus(Result selectedResult)
        {
            Log.Info("LoadContextMenus", GetType());

            var results = new List<ContextMenuResult>();

            if (selectedResult.ContextData is VsCodeWorkspace ws && ws.WorkspaceLocation == WorkspaceLocation.Local)
            {
                results.Add(new ContextMenuResult
                {
                    PluginName = Name,
                    Title = "Open Folder (Ctrl+Enter)",
                    FontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets",
                    Glyph = "\xE838", // Folder
                    AcceleratorKey = Key.Enter,
                    AcceleratorModifiers = ModifierKeys.Control,
                    Action = _ => OpenFolderInExplorer(SystemPath.RealPath(ws.RelativePath)),
                });
                
                Log.Info("After adding open folder context menu", GetType());
            }

            return results;
        }
    }
}