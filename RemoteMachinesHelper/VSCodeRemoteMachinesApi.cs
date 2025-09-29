// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Text.Json;
using Community.PowerToys.Run.Plugin.VSCodeWorkspaces.SshConfigParser;
using Community.PowerToys.Run.Plugin.VSCodeWorkspaces.VSCodeHelper;
using System.Threading;
using System.Threading.Tasks;

namespace Community.PowerToys.Run.Plugin.VSCodeWorkspaces.RemoteMachinesHelper
{
    public class VSCodeRemoteMachinesApi
    {
        private List<VSCodeRemoteMachine>? _cachedMachines;
        private DateTime _lastCacheUpdate = DateTime.MinValue;
        private readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(30);
        private readonly object _cacheLock = new object();
        private Task<List<VSCodeRemoteMachine>>? _loadingTask;

        public VSCodeRemoteMachinesApi()
        {
        }

        public List<VSCodeRemoteMachine> Machines
        {
            get
            {
                lock (_cacheLock)
                {
                    // Return cached results if still valid
                    if (_cachedMachines != null && DateTime.Now - _lastCacheUpdate < _cacheLifetime)
                    {
                        return _cachedMachines;
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
                                _cachedMachines = _loadingTask.Result;
                                _lastCacheUpdate = DateTime.Now;
                                return _cachedMachines;
                            }
                        }
                        catch
                        {
                            // Ignore timeout/task exceptions, fall back to synchronous loading
                        }
                    }

                    // Start background loading for next time, but return synchronous results now
                    _loadingTask = Task.Run(LoadMachinesAsync);

                    // Load synchronously for immediate return
                    var results = LoadMachinesSync();
                    _cachedMachines = results;
                    _lastCacheUpdate = DateTime.Now;
                    
                    return results;
                }
            }
        }

        private async Task<List<VSCodeRemoteMachine>> LoadMachinesAsync()
        {
            return await Task.Run(LoadMachinesSync);
        }

        private List<VSCodeRemoteMachine> LoadMachinesSync()
        {
            var results = new List<VSCodeRemoteMachine>();

            foreach (var vscodeInstance in VSCodeInstances.Instances)
            {
                try
                {
                    // settings.json contains path of ssh_config
                    var vscode_settings = Path.Combine(vscodeInstance.AppData, "User\\settings.json");

                    if (File.Exists(vscode_settings))
                    {
                        var fileContent = File.ReadAllText(vscode_settings);

                        try
                        {
                            JsonElement vscodeSettingsFile = JsonSerializer.Deserialize<JsonElement>(fileContent, new JsonSerializerOptions
                            {
                                AllowTrailingCommas = true,
                                ReadCommentHandling = JsonCommentHandling.Skip,
                            });
                            if (vscodeSettingsFile.TryGetProperty("remote.SSH.configFile", out var pathElement))
                            {
                                var path = pathElement.GetString();

                                if (path != null && File.Exists(path))
                                {
                                    foreach (SshHost h in SshConfig.ParseFile(path))
                                    {
                                        var machine = new VSCodeRemoteMachine();
                                        machine.Host = h.Host ?? string.Empty;
                                        machine.VSCodeInstance = vscodeInstance;
                                        machine.HostName = h.HostName ?? string.Empty;
                                        machine.User = h.User ?? string.Empty;

                                        results.Add(machine);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            var message = $"Failed to deserialize {vscode_settings}";
                            Wox.Plugin.Logger.Log.Error($"VSCodeWorkSpaces: {message} Exception: {ex.Message}", typeof(VSCodeRemoteMachinesApi));
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue with other instances
                    System.Diagnostics.Debug.WriteLine($"Error loading machines for instance {vscodeInstance.ExecutablePath}: {ex.Message}");
                }
            }

            return results;
        }
    }
}