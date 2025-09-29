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
using System.Threading;
using System.Threading.Tasks;

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
        public static PluginInitContext? Context { get; set; }
        private string? IconPath { get; set; }
        private bool Disposed { get; set; }
        private VSCodeInstance? _defaultInstance;
        private readonly VSCodeWorkspacesApi _workspacesApi = new();
        private readonly VSCodeRemoteMachinesApi _machinesApi = new();
        
        // Background loading and caching
        private Timer? _refreshTimer;
        private volatile bool _isInitialLoadComplete = false;
        private readonly object _loadLock = new object();
        private Task? _backgroundLoadTask;

        public List<Result> Query(Query query)
        {
            var results = new List<Result>();
            var searchTokens = GetSearchTokens(query.Search);

            try
            {
                if (!_isInitialLoadComplete)
                {
                    results.Add(CreateLoadingResult());
                    if (VSCodeInstances.Instances.Count > 0) AddResults(results, searchTokens);
                    return DeduplicateResults(results);
                }

                AddResults(results, searchTokens);
            }
            catch (Exception ex)
            {
                Log.Error($"Error during query execution: {ex.Message}", GetType());
                results.Add(CreateErrorResult());
            }

            return DeduplicateResults(results);
        }

        private void AddResults(List<Result> results, string[] searchTokens)
        {
            if (DiscoverWorkspaces) TryAddScoredResults(results, _workspacesApi.Workspaces, searchTokens, CreateWorkspaceResult, GetWorkspaceSearchTargets, "workspaces");
            if (DiscoverMachines) TryAddScoredResults(results, _machinesApi.Machines, searchTokens, CreateMachineResult, GetMachineSearchTargets, "remote machines");
        }

        private void TryAddScoredResults<T>(List<Result> results, IEnumerable<T> items, string[] searchTokens, 
            Func<T, Result> createResult, Func<T, string[]> getSearchTargets, string description)
        {
            try 
            { 
                var scoredResults = items
                    .Select(item => new { Item = item, Score = FuzzySearchHelper.GetBestMatchScore(searchTokens, getSearchTargets(item)) })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .Select(x => 
                    {
                        var result = createResult(x.Item);
                        result.Score = x.Score;
                        return result;
                    });
                
                results.AddRange(scoredResults);
            }
            catch (Exception ex) { Log.Error($"Error loading {description}: {ex.Message}", GetType()); }
        }

        private Result CreateLoadingResult() => new()
        {
            Title = "Loading VS Code workspaces...",
            SubTitle = "Please wait while workspaces and remote machines are being discovered",
            IcoPath = IconPath ?? string.Empty,
            Score = int.MaxValue,
            Action = _ => false
        };

        private Result CreateErrorResult() => new()
        {
            Title = "Error loading VS Code data",
            SubTitle = "Try restarting PowerToys Run or check the logs for details",
            IcoPath = IconPath ?? string.Empty,
            Action = _ => false
        };

        private string[] GetSearchTokens(string search)
        {
            return string.IsNullOrWhiteSpace(search)
                ? Array.Empty<string>()
                : search.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private string[] GetWorkspaceSearchTargets(VsCodeWorkspace ws) => 
            new[] { ws.FolderName.ToString(), SystemPath.RealPath(ws.RelativePath) ?? "", ws.Label ?? "" }
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();

        private string[] GetMachineSearchTargets(VSCodeRemoteMachine machine) => 
            new[] { machine.Host ?? "", machine.User ?? "", machine.HostName ?? "" }
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();

        private List<Result> DeduplicateResults(List<Result> results)
        {
            return results.GroupBy(r => r.Title)
                .Select(g => g.OrderByDescending(r => r.Score).First())
                .OrderByDescending(r => r.Score)
                .ToList();
        }

        public void Init(PluginInitContext context)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            Context.API.ThemeChanged += OnThemeChanged;
            UpdateIconPath(Context.API.GetCurrentTheme());

            // Start background loading to avoid blocking the UI
            StartBackgroundLoading();
            
            // Set up periodic refresh every 30 seconds to catch new workspaces and machines
            _refreshTimer = new Timer(RefreshDataInBackground, null, 30000, 30000);
        }

        private Result CreateWorkspaceResult(VsCodeWorkspace ws)
        {
            var (title, tooltip) = BuildWorkspaceInfo(ws);
            return new Result
            {
                QueryTextDisplay = ws.FolderName,
                IcoPath = GetWorkspaceIcon(ws),
                Title = title,
                SubTitle = tooltip,
                ToolTipData = new ToolTipData("VS Code Workspace", tooltip),
                Action = c => OpenWorkspace(ws, c.SpecialKeyState.CtrlPressed),
                ContextData = ws,
            };
        }

        private (string title, string tooltip) BuildWorkspaceInfo(VsCodeWorkspace ws)
        {
            var title = ws.WorkspaceLocation == WorkspaceLocation.Local ? ws.FolderName.ToString() :
                !string.IsNullOrEmpty(ws.Label) ? ws.Label :
                $"{ws.FolderName}{(!string.IsNullOrEmpty(ws.ExtraInfo) ? $" - {ws.ExtraInfo}" : "")} ({ws.WorkspaceTypeToString()})";
            
            var location = ws.WorkspaceLocation != WorkspaceLocation.Local ? $" {Resources.In} {ws.WorkspaceTypeToString()}" : "";
            var tooltip = $"{Resources.Workspace}{location}: {SystemPath.RealPath(ws.RelativePath)}";
            
            return (title, tooltip);
        }

        private string GetWorkspaceIcon(VsCodeWorkspace ws) => 
            ws.WorkspaceLocation != WorkspaceLocation.Local 
                ? ws.VSCodeInstance?.RemoteIcon ?? IconPath ?? string.Empty
                : ws.VSCodeInstance?.WorkspaceIcon ?? IconPath ?? string.Empty;

        private bool OpenWorkspace(VsCodeWorkspace ws, bool ctrlPressed) => LaunchProcess(async () =>
        {
            if (ctrlPressed) return await LaunchProcessAsync("explorer.exe", $"\"{SystemPath.RealPath(ws.RelativePath)}\"", "folder in explorer");
            
            var uriFlag = ws.WorkspaceType == WorkspaceType.Workspace ? "--file-uri" : "--folder-uri";
            return await LaunchProcessAsync(ws.VSCodeInstance?.ExecutablePath ?? "", $"{uriFlag} {ws.Path}", "workspace");
        });

        private Result CreateMachineResult(VSCodeRemoteMachine machine)
        {
            var title = BuildMachineTitle(machine);

            return new Result
            {
                IcoPath = machine.VSCodeInstance?.RemoteIcon ?? IconPath ?? string.Empty,
                Title = title,
                SubTitle = Resources.SSHRemoteMachine,
                ToolTipData = new ToolTipData("SSH Remote Machine", Resources.SSHRemoteMachine),
                Action = _ => OpenSSHMachine(machine),
                ContextData = machine,
            };
        }

        private string BuildMachineTitle(VSCodeRemoteMachine machine) => 
            machine.Host + (!string.IsNullOrEmpty(machine.User) && !string.IsNullOrEmpty(machine.HostName) ? $" [{machine.User}@{machine.HostName}]" : "");

        private bool OpenSSHMachine(VSCodeRemoteMachine machine) => LaunchProcess(async () =>
            await LaunchProcessAsync(machine.VSCodeInstance?.ExecutablePath ?? "", 
                $"--new-window --enable-proposed-api ms-vscode-remote.remote-ssh --remote ssh-remote+{((char)34) + machine.Host + ((char)34)}", 
                "SSH remote machine"));

        private bool OpenInExplorer(string path) => LaunchProcess(async () => 
            await LaunchProcessAsync("explorer.exe", $"\"{path}\"", "folder in explorer"));

        private bool LaunchProcess(Func<Task<bool>> processFunc)
        {
            _ = Task.Run(processFunc);
            return true;
        }

        private async Task<bool> LaunchProcessAsync(string fileName, string arguments, string description)
        {
            try
            {
                await Task.Run(() => Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to open {description}: {ex.Message}", GetType());
                Context?.API?.ShowMsg(Name, $"Failed to open {description}", string.Empty);
                return false;
            }
        }

        public Control CreateSettingPanel() => throw new NotImplementedException();

        public void UpdateSettings(PowerLauncherPluginSettings settings)
        {
            var options = settings?.AdditionalOptions;
            DiscoverWorkspaces = options?.FirstOrDefault(x => x.Key == nameof(DiscoverWorkspaces))?.Value ?? true;
            DiscoverMachines = options?.FirstOrDefault(x => x.Key == nameof(DiscoverMachines))?.Value ?? true;
        }

        private void StartBackgroundLoading()
        {
            if (_backgroundLoadTask?.IsCompleted == false) return;
            _backgroundLoadTask = Task.Run(() => LoadData());
        }

        private void LoadData()
        {
            lock (_loadLock)
            {
                try
                {
                    VSCodeInstances.LoadVSCodeInstances();
                    _defaultInstance = VSCodeInstances.Instances.Find(e => e.VSCodeVersion == VSCodeVersion.Stable) ?? VSCodeInstances.Instances.FirstOrDefault();
                    
                    if (DiscoverWorkspaces) _ = _workspacesApi.Workspaces.Count;
                    if (DiscoverMachines) _ = _machinesApi.Machines.Count;
                    
                    _isInitialLoadComplete = true;
                }
                catch (Exception ex) { Log.Error($"Data loading failed: {ex.Message}", GetType()); }
            }
        }

        private void RefreshDataInBackground(object? state) => StartBackgroundLoading();

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (Disposed || !disposing) return;

            Context?.API?.ThemeChanged -= OnThemeChanged;
            _refreshTimer?.Dispose();
            try { _backgroundLoadTask?.Wait(TimeSpan.FromSeconds(1)); }
            catch (Exception ex) { Log.Error($"Error waiting for background task to complete: {ex.Message}", GetType()); }
            
            Disposed = true;
        }

        private void UpdateIconPath(Theme theme) => IconPath = theme == Theme.Light || theme == Theme.HighContrastWhite ? Context?.CurrentPluginMetadata?.IcoPathLight : Context?.CurrentPluginMetadata?.IcoPathDark;

        private void OnThemeChanged(Theme currentTheme, Theme newTheme) => UpdateIconPath(newTheme);

        public List<ContextMenuResult> LoadContextMenus(Result selectedResult)
        {
            var results = new List<ContextMenuResult>();

            if (selectedResult.ContextData is VsCodeWorkspace ws && ws.WorkspaceLocation == WorkspaceLocation.Local)
            {
                results.Add(new ContextMenuResult
                {
                    PluginName = Name,
                    Title = "Open Folder (Ctrl+Enter)",
                    FontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets",
                    Glyph = "\xE838",
                    AcceleratorKey = Key.Enter,
                    AcceleratorModifiers = ModifierKeys.Control,
                    Action = _ => OpenInExplorer(SystemPath.RealPath(ws.RelativePath)),
                });
            }

            return results;
        }
    }
}