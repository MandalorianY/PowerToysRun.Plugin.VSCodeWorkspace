// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Community.PowerToys.Run.Plugin.VSCodeWorkspaces.VSCodeHelper;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;

namespace Community.PowerToys.Run.Plugin.VSCodeWorkspaces.WorkspacesHelper
{
    public class VSCodeWorkspacesApi
    {
        private List<VsCodeWorkspace>? _cachedWorkspaces;
        private DateTime _lastCacheUpdate = DateTime.MinValue;
        private readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(30);
        private readonly object _cacheLock = new object();
        private Task<List<VsCodeWorkspace>>? _loadingTask;

        public VSCodeWorkspacesApi()
        {
        }

        public static VsCodeWorkspace? ParseVSCodeUri(string? uri, VSCodeInstance vscodeInstance)
        {
            if (uri is not null)
            {
                var unescapeUri = Uri.UnescapeDataString(uri);
                var typeWorkspace = WorkspacesHelper.ParseVSCodeUri.GetTypeWorkspace(unescapeUri);
                if (!typeWorkspace.workspaceLocation.HasValue) return null;
                var folderName = Path.GetFileName(unescapeUri);

                // Check we haven't returned '' if we have a path like C:\
                if (string.IsNullOrEmpty(folderName))
                {
                    DirectoryInfo dirInfo = new DirectoryInfo(unescapeUri);
                    folderName = dirInfo.Name.TrimEnd(':');
                }

                return new VsCodeWorkspace()
                {
                    Path = unescapeUri,
                    RelativePath = typeWorkspace.Path ?? string.Empty,
                    FolderName = folderName ?? string.Empty,
                    ExtraInfo = typeWorkspace.MachineName ?? string.Empty,
                    WorkspaceLocation = typeWorkspace.workspaceLocation.Value,
                    VSCodeInstance = vscodeInstance,
                };
            }

            return null;
        }

        public readonly Regex WorkspaceLabelParser = new Regex("(.+?)(\\[.+\\])");

        public List<VsCodeWorkspace> Workspaces
        {
            get
            {
                lock (_cacheLock)
                {
                    // Return cached results if still valid
                    if (_cachedWorkspaces != null && DateTime.Now - _lastCacheUpdate < _cacheLifetime)
                    {
                        return _cachedWorkspaces;
                    }

                    // If a loading task is already in progress, wait for it (with timeout)
                    if (_loadingTask != null && !_loadingTask.IsCompleted)
                    {
                        try
                        {
                            var timeoutTask = Task.Delay(2000); // 2 second timeout
                            var completedTask = Task.WaitAny(_loadingTask, timeoutTask);
                            
                            if (completedTask == 0 && _loadingTask.IsCompletedSuccessfully)
                            {
                                _cachedWorkspaces = _loadingTask.Result;
                                _lastCacheUpdate = DateTime.Now;
                                return _cachedWorkspaces;
                            }
                        }
                        catch
                        {
                            // Ignore timeout/task exceptions, fall back to synchronous loading
                        }
                    }

                    // Start background loading for next time, but return synchronous results now
                    _loadingTask = Task.Run(LoadWorkspacesAsync);

                    // Load synchronously for immediate return
                    var results = LoadWorkspacesSync();
                    _cachedWorkspaces = results;
                    _lastCacheUpdate = DateTime.Now;
                    
                    return results;
                }
            }
        }

        private async Task<List<VsCodeWorkspace>> LoadWorkspacesAsync()
        {
            return await Task.Run(LoadWorkspacesSync);
        }

        private List<VsCodeWorkspace> LoadWorkspacesSync()
        {
            var results = new List<VsCodeWorkspace>();

            foreach (var vscodeInstance in VSCodeInstances.Instances)
            {
                try
                {
                    // Try SQLite database first (preferred method)
                    var sqliteWorkspaces = GetWorkspacesFromSqliteDatabase(vscodeInstance);
                    if (sqliteWorkspaces.Count != 0)
                    {
                        // SQLite workspaces are already in correct order (most recent first)
                        results.AddRange(sqliteWorkspaces);
                    }
                    else
                    {
                        // Fallback to legacy storage.json only if SQLite failed
                        var legacyWorkspaces = GetWorkspacesFromLegacyStorage(vscodeInstance);
                        results.AddRange(legacyWorkspaces);
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue with other instances
                    System.Diagnostics.Debug.WriteLine($"Error loading workspaces for instance {vscodeInstance.ExecutablePath}: {ex.Message}");
                }
            }

            return results;
        }
        private List<VsCodeWorkspace> GetWorkspacesFromSqliteDatabase(VSCodeInstance vscodeInstance)
        {
            var workspaces = new List<VsCodeWorkspace>();
            
            try
            {
                var dbPath = Path.Combine(vscodeInstance.AppData, "User", "globalStorage", "state.vscdb");
                using var connection = new SqliteConnection($"Data Source={dbPath};mode=readonly;cache=shared;");
                connection.Open();
                
                var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM ItemTable WHERE key = 'history.recentlyOpenedPathsList'";
                var result = command.ExecuteScalar();
                
                if (result?.ToString() is { } resultString)
                {
                    using var historyDoc = JsonDocument.Parse(resultString);
                    var root = historyDoc.RootElement;
                    
                    if (root.TryGetProperty("entries", out var entries))
                    {
                        foreach (var entry in entries.EnumerateArray())
                        {
                            // Parse folder entries
                            if (entry.TryGetProperty("folderUri", out var folderUri) &&
                                ParseFolderEntry(folderUri, vscodeInstance, entry) is { } folderWorkspace)
                            {
                                workspaces.Add(folderWorkspace);
                            }
                            // Parse workspace entries
                            else if (entry.TryGetProperty("workspace", out var workspaceInfo) &&
                                     ParseWorkspaceEntry(workspaceInfo, vscodeInstance, entry) is { } workspace)
                            {
                                workspaces.Add(workspace);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Wox.Plugin.Logger.Log.Warn($"VSCodeWorkspaceApi: Failed to read SQLite database for {vscodeInstance.VSCodeVersion}. Exception: {ex.Message}", typeof(VSCodeWorkspacesApi));
            }

            return workspaces;
        }

        private List<VsCodeWorkspace> GetWorkspacesFromLegacyStorage(VSCodeInstance vscodeInstance)
        {
            var workspaces = new List<VsCodeWorkspace>();
            var storagePath = Path.Combine(vscodeInstance.AppData, "storage.json");

            if (!File.Exists(storagePath))
            {
                return workspaces;
            }

            try
            {
                var fileContent = File.ReadAllText(storagePath);
                var storageFile = JsonSerializer.Deserialize<VSCodeStorageFile>(fileContent);

                if (storageFile?.OpenedPathsList != null)
                {
                    // Handle legacy workspaces (older VS Code versions)
                    if (storageFile.OpenedPathsList.Workspaces3 != null)
                    {
                        var legacyWorkspaces = storageFile.OpenedPathsList.Workspaces3
                            .Select(uri => ParseVSCodeUri(uri, vscodeInstance))
                            .Where(workspace => workspace != null)
                            .Cast<VsCodeWorkspace>()
                            .ToList();
                        
                        legacyWorkspaces.Reverse(); // Most recent first
                        workspaces.AddRange(legacyWorkspaces);
                    }

                    // Handle newer format (VS Code v1.55.0+)
                    if (storageFile.OpenedPathsList.Entries != null)
                    {
                        var entries = storageFile.OpenedPathsList.Entries
                            .Where(entry => entry.FolderUri != null)
                            .Select(entry => ParseVSCodeUri(entry.FolderUri, vscodeInstance))
                            .Where(workspace => workspace != null)
                            .Cast<VsCodeWorkspace>()
                            .ToList();
                        
                        entries.Reverse(); // Most recent first
                        workspaces.AddRange(entries);
                    }
                }
            }
            catch (Exception ex)
            {
                Wox.Plugin.Logger.Log.Error($"VSCodeWorkspaceApi: Failed to deserialize {storagePath}. Exception: {ex.Message}", typeof(VSCodeWorkspacesApi));
            }

            return workspaces;
        }

        private VsCodeWorkspace? ParseWorkspaceEntry(JsonElement workspaceInfo, VSCodeInstance vscodeInstance,
            JsonElement entry)
        {
            if (workspaceInfo.TryGetProperty("configPath", out var configPath))
            {
                var configPathString = configPath.GetString();
                if (configPathString == null) return null;
                
                var workspace = ParseVSCodeUri(configPathString, vscodeInstance);
                if (workspace == null)
                    return null;

                if (entry.TryGetProperty("label", out var label))
                {
                    var labelString = label.GetString();
                    if (labelString != null)
                    {
                        var matchGroup = WorkspaceLabelParser.Match(labelString);
                        workspace = workspace with
                        {
                            Label = $"{matchGroup.Groups[2]} {matchGroup.Groups[1]}",
                            WorkspaceType = WorkspaceType.Workspace
                        };
                    }
                }

                return workspace;
            }

            return null;
        }

        private VsCodeWorkspace? ParseFolderEntry(JsonElement folderUri, VSCodeInstance vscodeInstance,
            JsonElement entry)
        {
            var workspaceUri = folderUri.GetString();
            if (workspaceUri == null) return null;
            
            var workspace = ParseVSCodeUri(workspaceUri, vscodeInstance);
            if (workspace == null)
                return null;

            if (entry.TryGetProperty("label", out var label))
            {
                var labelString = label.GetString();
                if (labelString != null)
                {
                    var matchGroup = WorkspaceLabelParser.Match(labelString);
                    workspace = workspace with
                    {
                        Label = $"{matchGroup.Groups[2]} {matchGroup.Groups[1]}",
                        WorkspaceType = WorkspaceType.Folder
                    };
                }
            }

            return workspace;
        }
    }
}