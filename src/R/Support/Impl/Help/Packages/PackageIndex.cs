﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Common.Core;
using Microsoft.Common.Core.Idle;
using Microsoft.Common.Core.Shell;
using Microsoft.Common.Core.Threading;
using Microsoft.R.Components.InteractiveWorkflow;
using Microsoft.R.Components.PackageManager;
using Microsoft.R.Components.PackageManager.Model;
using Microsoft.R.Host.Client;
using Microsoft.R.Host.Client.Session;
using Newtonsoft.Json.Linq;
using static System.FormattableString;

namespace Microsoft.R.Support.Help.Packages {
    /// <summary>
    /// Index of packages available from the R engine.
    /// </summary>
    [Export(typeof(IPackageIndex))]
    [Export(typeof(IPackageInstallationNotifications))]
    public sealed class PackageIndex : IPackageIndex {
        private readonly IRInteractiveWorkflow _workflow;
        private readonly IRSession _interactiveSession;
        private readonly ICoreShell _shell;
        private readonly IIntellisenseRSession _host;
        private readonly IFunctionIndex _functionIndex;
        private readonly ConcurrentDictionary<string, PackageInfo> _packages = new ConcurrentDictionary<string, PackageInfo>();
        private readonly BinaryAsyncLock _buildIndexLock = new BinaryAsyncLock();

        public static IEnumerable<string> PreloadedPackages { get; } = new string[]
            { "base", "stats", "utils", "graphics", "datasets", "methods" };

        [ImportingConstructor]
        public PackageIndex(
            IRInteractiveWorkflowProvider interactiveWorkflowProvider, ICoreShell shell, IIntellisenseRSession host, IFunctionIndex functionIndex) {
            _shell = shell;
            _host = host;
            _functionIndex = functionIndex;

            _workflow = interactiveWorkflowProvider.GetOrCreate();

            _interactiveSession = _workflow.RSession;
            _interactiveSession.Connected += OnSessionConnected;
            _interactiveSession.PackagesRemoved += OnPackagesRemoved;

            _workflow.RSessions.BrokerStateChanged += OnBrokerStateChanged;

            if (_workflow.RSession.IsHostRunning) {
                BuildIndexAsync().DoNotWait();
            }
        }

        private void OnSessionConnected(object sender, RConnectedEventArgs e) {
            BuildIndexAsync().DoNotWait();
        }

        private void OnBrokerStateChanged(object sender, BrokerStateChangedEventArgs e) {
            if (e.IsConnected) {
                BuildIndexAsync().DoNotWait();
            }
        }

        public Task BeforePackagesInstalledAsync(CancellationToken ct) {
            var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(5000);
            return _host.StopSessionAsync(timeoutCts.Token);
        }

        public Task AfterPackagesInstalledAsync(CancellationToken ct) {
            UpdateInstalledPackagesAsync(ct).DoNotWait();
            return Task.CompletedTask;
        }

        private void OnPackagesRemoved(object sender, EventArgs e) {
            RemovePackagesAsync().DoNotWait();
        }

        #region IPackageIndex
        /// <summary>
        /// Collection of all packages (base, user and project-specific)
        /// </summary>
        public IEnumerable<IPackageInfo> Packages => _packages.Values;

        public async Task BuildIndexAsync() {
            var lockToken = await _buildIndexLock.WaitAsync();
            await BuildIndexAsync(lockToken);
        }

        private async Task BuildIndexAsync(IBinaryAsyncLockToken lockToken) {
            try {
                if (!lockToken.IsSet) {
                    await TaskUtilities.SwitchToBackgroundThread();
                    var stopwatch = new Stopwatch();
                    stopwatch.Start();

                    // Ensure session is started
                    await _host.StartSessionAsync();
                    Debug.WriteLine("R function host start: {0} ms", stopwatch.ElapsedMilliseconds);

                    // Fetch list of package functions from R session
                    await LoadInstalledPackagesIndexAsync();
                    Debug.WriteLine("Fetch list of package functions from R session: {0} ms", stopwatch.ElapsedMilliseconds);

                    // Try load missing functions from cache or explicitly
                    await LoadRemainingPackagesFunctions();
                    Debug.WriteLine("Try load missing functions from cache or explicitly: {0} ms", stopwatch.ElapsedMilliseconds);

                    // Build index
                    await _functionIndex.BuildIndexAsync(this);
                    Debug.WriteLine("R function index total: {0} ms", stopwatch.ElapsedMilliseconds);

                    stopwatch.Stop();
                }
            } catch (Exception ex) when (!ex.IsCriticalException()) {
                Debug.WriteLine(ex.Message);
                ScheduleIdleTimeRebuild();
            } finally {
                lockToken.Set();
            }
        }

        /// <summary>
        /// Retrieves R package information by name. If package is not in the index,
        /// attempts to locate the package in the current R session.
        /// </summary>
        public async Task<IPackageInfo> GetPackageInfoAsync(string packageName) {
            packageName = packageName.TrimQuotes().Trim();
            IPackageInfo package = GetPackageInfo(packageName);
            if (package != null) {
                return package;
            }

            Debug.WriteLine(Invariant($"Missing package: {packageName}"));
            return (await TryAddMissingPackagesAsync(new string[] { packageName })).FirstOrDefault();
        }

        /// <summary>
        /// Retrieves information on multilple R packages. If one of the packages 
        /// is not in the index, attempts to locate the package in the current R session.
        /// </summary>
        public async Task<IEnumerable<IPackageInfo>> GetPackagesInfoAsync(IEnumerable<string> packageNames) {
            var list = new List<IPackageInfo>();
            var missing = new List<string>();

            foreach (var n in packageNames) {
                var name = n.TrimQuotes().Trim();
                IPackageInfo package = GetPackageInfo(name);
                if (package != null) {
                    list.Add(package);
                } else {
                    Debug.WriteLine(Invariant($"Missing package: {name}"));
                    missing.Add(name);
                }
            }

            list.AddRange(await TryAddMissingPackagesAsync(missing));
            return list;
        }

        public void WriteToDisk() {
            if (_buildIndexLock.IsSet) {
                foreach (var pi in _packages.Values) {
                    pi.WriteToDisk();
                }
            }
        }
        #endregion

        public void Dispose() {
            if (_interactiveSession != null) {
                _interactiveSession.PackagesRemoved -= OnPackagesRemoved;
                _interactiveSession.Connected -= OnSessionConnected;
                _workflow.RSessions.BrokerStateChanged -= OnBrokerStateChanged;
            }
            _host?.Dispose();
        }

        private async Task LoadInstalledPackagesIndexAsync() {
            try {
                await _host.StartSessionAsync();
                var packagesFunctions = await _host.Session.InstalledPackagesFunctionsAsync(REvaluationKind.BaseEnv);
                foreach (var package in packagesFunctions) {
                    var name = package.Value<string>("Package");
                    var description = package.Value<string>("Description");
                    var version = package.Value<string>("Version");
                    var functions = package.Value<JArray>("Functions");
                    if (functions.HasValues) {
                        var functionNames = functions.Children<JValue>().Select(v => (string)v.Value);
                        _packages[name] = new PackageInfo(_host, name, description, version, functionNames);
                    } else {
                        _packages[name] = new PackageInfo(_host, name, description, version);
                    }
                }

                if (!_packages.ContainsKey("rtvs")) {
                    _packages["rtvs"] = new PackageInfo(_host, "rtvs", "R Tools", "1.0");
                }
            } catch (RException) { } catch (OperationCanceledException) { }
        }

        private async Task LoadRemainingPackagesFunctions() {
            foreach (var pi in _packages.Values.Where(p => !p.Functions.Any())) {
                await pi.LoadFunctionsIndexAsync();
            }
        }

        private async Task RemovePackagesAsync() {
            var token = await _buildIndexLock.ResetAsync();
            if (!token.IsSet) {
                try {
                    var installed = await GetInstalledPackagesAsync();
                    var installedNames = installed.Select(p => p.Package).Append("rtvs").ToList();

                    var currentNames = _packages.Keys.ToArray();
                    var removedNames = currentNames.Except(installedNames);
                    _packages.RemoveWhere((kvp) => removedNames.Contains(kvp.Key));
                } catch (RException) { } catch (OperationCanceledException) { } finally {
                    token.Reset();
                }
            }
        }

        private async Task UpdateInstalledPackagesAsync(CancellationToken ct) {
            var token = await _buildIndexLock.ResetAsync(ct);
            if (!token.IsSet) {
                try {
                    var installed = await GetInstalledPackagesAsync();
                    var currentNames = _packages.Keys.ToArray();

                    var added = installed.Where(p => !currentNames.Contains(p.Package));
                    await AddPackagesToIndexAsync(added);
                } catch (RException) { } catch (OperationCanceledException) { } finally {
                    token.Reset();
                }
            }
        }

        /// <summary>
        /// From the supplied names selects packages that are not in the index and attempts
        /// to add them to the index. This typically applies to packages that were just installed.
        /// </summary>
        private async Task<IEnumerable<IPackageInfo>> TryAddMissingPackagesAsync(IEnumerable<string> packageNames) {
            var info = Enumerable.Empty<IPackageInfo>();
            // Do not attempt to add new package when index is still being built
            if (packageNames.Any() && _buildIndexLock.IsSet) {
                try {
                    var installedPackages = await GetInstalledPackagesAsync();
                    var packagesNotInIndex = installedPackages.Where(p => packageNames.Contains(p.Package));
                    info = await AddPackagesToIndexAsync(packagesNotInIndex);
                } catch (RException) { } catch (OperationCanceledException) { }
            }
            return info;
        }

        private async Task<IEnumerable<IPackageInfo>> AddPackagesToIndexAsync(IEnumerable<RPackage> packages) {
            var list = new List<IPackageInfo>();
            foreach (var p in packages) {
                var info = new PackageInfo(_host, p.Package, p.Description, p.Version);
                _packages[p.Package] = info;

                await info.LoadFunctionsIndexAsync();
                _functionIndex.RegisterPackageFunctions(info);
                list.Add(info);
            }
            return list;
        }

        private async Task<IEnumerable<RPackage>> GetInstalledPackagesAsync(CancellationToken ct = default(CancellationToken)) {
            await _host.StartSessionAsync(ct);
            var result = await _host.Session.InstalledPackagesAsync();
            return result.Select(p => p.ToObject<RPackage>());
        }

        internal static string CacheFolderPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\VisualStudio\RTVS\IntelliSense\");

        public static void ClearCache() {
            try {
                if (Directory.Exists(CacheFolderPath)) {
                    Directory.Delete(CacheFolderPath, recursive: true);
                }
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private void ScheduleIdleTimeRebuild() {
            IdleTimeAction.Cancel(typeof(PackageIndex));
            IdleTimeAction.Create(() => RebuildIndexAsync().DoNotWait(), 100, typeof(PackageIndex), _shell);
        }

        private async Task RebuildIndexAsync() {
            if (!_buildIndexLock.IsSet) {
                // Still building, try again later
                ScheduleIdleTimeRebuild();
                return;
            }

            var lockToken = await _buildIndexLock.ResetAsync();
            await BuildIndexAsync(lockToken);
        }

        /// <summary>
        /// Retrieves information on the package from index. Does not attempt to locate the package
        /// if it is not in the index such as when package was just installed.
        /// </summary>
        private IPackageInfo GetPackageInfo(string packageName) {
            PackageInfo package = null;
            packageName = packageName.TrimQuotes().Trim();
            _packages.TryGetValue(packageName, out package);
            return package;
        }
    }
}
