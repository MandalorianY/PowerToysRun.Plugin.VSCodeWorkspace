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
            var workspaces = new List<VsCodeWorkspace>();

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
                workspaces.AddRange(_workspacesApi.Workspaces);
            }
            // Simple de-duplication
            results.AddRange(workspaces.Distinct()
                .Select(CreateWorkspaceResult)
            );

            if (DiscoverMachines)
            {
                foreach (var a in _machinesApi.Machines)
                {
                    var title = $"{a.Host}";

                    if (!string.IsNullOrEmpty(a.User) && !string.IsNullOrEmpty(a.HostName))
                    {
                        title += $" [{a.User}@{a.HostName}]";
                    }

                    if (results.Any(r => string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var tooltip = Resources.SSHRemoteMachine;

                    var instanceIcon = a.VSCodeInstance?.RemoteIcon ?? IconPath ?? string.Empty;
                    results.Add(new Result
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
                    });
                }
            }

            Log.Info("After adding remote machines:" + string.Join(" | ", results.Select((r, i) => $"#{i}:{r.Title}")), GetType());

            // Filter & score results based on search query
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                Log.Info("Filtering with query: " + query.Search, GetType());
                var search = query.Search.Trim();
                var tokens = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                bool OrderedSubsequence(string source, string pattern)
                {
                    if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(pattern)) return false;
                    var si = 0; // index in source
                    foreach (var pc in pattern)
                    {
                        var found = false;
                        while (si < source.Length)
                        {
                            if (char.ToLowerInvariant(source[si]) == char.ToLowerInvariant(pc))
                            {
                                found = true;
                                si++;
                                break;
                            }
                            si++;
                        }
                        if (!found) return false;
                    }
                    return true;
                }

                int IndexOfCI(string source, string value)
                {
                    return source?.IndexOf(value, StringComparison.OrdinalIgnoreCase) ?? -1;
                }

                static int RankIndex(int index)
                {
                    if (index < 0) return 0;
                    if (index == 0) return 20;
                    return Math.Max(1, 15 - index);
                }

                List<Result> filtered = new();
                foreach (var r in results)
                {
                    var title = r.Title ?? string.Empty;
                    var sub = r.SubTitle ?? string.Empty;

                    // Token rule: each token must appear in title or subtitle (CI)
                    var allTokensPresent = tokens.All(t =>
                        (!string.IsNullOrEmpty(title) && title.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(sub) && sub.Contains(t, StringComparison.OrdinalIgnoreCase)));

                    if (!allTokensPresent)
                    {
                        // As a fallback allow ordered subsequence of full search inside title
                        if (!OrderedSubsequence(title, search))
                        {
                            continue; // filtered out
                        }
                    }

                    var score = 0;

                    // Exact token matches & positioning
                    foreach (var t in tokens)
                    {
                        var idxTitle = IndexOfCI(title, t);
                        var idxSub = IndexOfCI(sub, t);
                        if (idxTitle >= 0)
                        {
                            score += 30 + RankIndex(idxTitle);
                        }
                        else if (idxSub >= 0)
                        {
                            score += 15 + RankIndex(idxSub);
                        }
                    }

                    // Ordered subsequence bonus (only if not all tokens present but subsequence matched)
                    if (!allTokensPresent && OrderedSubsequence(title, search))
                    {
                        score += 10 + search.Length; // small bonus proportional to length
                    }

                    // Length heuristic: shorter titles that match get a slight boost
                    if (score > 0 && title.Length > 0)
                    {
                        score += Math.Max(0, 10 - Math.Min(10, title.Length / 10));
                    }

                    r.Score = score;
                    if (score > 0)
                    {
                        filtered.Add(r);
                    }
                }

                // Order by score desc then title asc for stability
                results = filtered
                    .OrderByDescending(r => r.Score)
                    .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                // Sort by title for no query
                results = results.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
            }
            
            return results;
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
                            var path = SystemPath.RealPath(ws.RelativePath);
                            try
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = "explorer.exe",
                                    Arguments = $"\"{path}\"",
                                    UseShellExecute = true
                                });
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"Failed to open folder in explorer: {ex.Message}", GetType());
                                Context?.API?.ShowMsg(Name, "Failed to open folder in explorer", string.Empty);
                                return false;
                            }
                            return true;
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
                    Action = _ =>
                    {
                        var path = SystemPath.RealPath(ws.RelativePath);
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = $"\"{path}\"",
                                UseShellExecute = true
                            });
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Failed to open folder in explorer: {ex.Message}", GetType());
                            Context?.API?.ShowMsg(Name, "Failed to open folder in explorer", string.Empty);
                            return false;
                        }
                        return true;
                    },
                });
            }

            return results;
        }
    }
}