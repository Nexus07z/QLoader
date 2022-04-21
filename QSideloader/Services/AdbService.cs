﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using CliWrap;
using CliWrap.Buffered;
using Newtonsoft.Json;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.ViewModels;
using Serilog;

namespace QSideloader.Services;

public class AdbService
{
    private List<AdbDevice> _deviceList = new();
    private readonly SideloaderSettingsViewModel _sideloaderSettings;
    private readonly AdbServerClient _adb;
    private DeviceMonitor? _deviceMonitor;
    private static readonly SemaphoreSlim DeviceSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim DeviceListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim AdbServerSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim SideloadSemaphoreSlim = new(1, 1);
    private readonly Subject<AdbDevice> _deviceChangeSubject = new();
    private readonly Subject<Unit> _packageListChangeSubject = new();
    private readonly Subject<List<AdbDevice>> _deviceListChangeSubject = new();

    public AdbService()
    {
        _sideloaderSettings = Globals.SideloaderSettings;
        _adb = new AdbServerClient();
        Task.Run(async () =>
        {
            CheckDeviceConnection();
            var lastWirelessAdbHost = _sideloaderSettings.LastWirelessAdbHost;
            if (!string.IsNullOrEmpty(lastWirelessAdbHost))
                await TryConnectWirelessAdbAsync(lastWirelessAdbHost);
        });
    }

    private bool FirstDeviceSearch { get; set; } = true;

    public AdbDevice? Device { get; private set; }
    public IReadOnlyList<AdbDevice> DeviceList
    {
        get => _deviceList.AsReadOnly();
        private set => _deviceList = value.ToList();
    }
    public IObservable<AdbDevice> WhenDeviceChanged => _deviceChangeSubject.AsObservable();
    public IObservable<Unit> WhenPackageListChanged => _packageListChangeSubject.AsObservable();
    public IObservable<List<AdbDevice>> WhenDeviceListChanged => _deviceListChangeSubject.AsObservable();

    private string? RunShellCommand(DeviceData device, string command, bool logCommand = false)
    {
        ConsoleOutputReceiver receiver = new();
        try
        {
            if (logCommand)
                Log.Debug("Running shell command: {Command}", command);
            _adb.AdbClient.ExecuteRemoteCommand(command, device, receiver);
        }
        catch
        {
            // TODO: throw exception instead of returning null
            return null;
        }

        var result = receiver.ToString().Trim();
        if (!logCommand) return result;
        if (!string.IsNullOrWhiteSpace(result))
            Log.Debug("Command returned: {Result}", result);
        return result;
    }

    public bool CheckDeviceConnection(bool assumeOffline = false)
    {
        try
        {
            EnsureADBRunning();
        }
        catch
        {
            return false;
        }

        var connectionStatus = false;
        try
        {
            DeviceSemaphoreSlim.Wait();
            if (Device is not null && !assumeOffline)
            {
                connectionStatus = PingDevice(Device);
            }

            if (!connectionStatus)
            {
                DeviceData? foundDevice = null;
                RefreshDeviceList();
                foreach (var device in DeviceList)
                {
                    connectionStatus = PingDevice(device);
                    if (!connectionStatus) continue;
                    foundDevice = device;
                    break;
                }

                if ((Device is null || Device?.Serial != foundDevice?.Serial) && foundDevice is not null &&
                    connectionStatus)
                {
                    var device = new AdbDevice(foundDevice, this);
                    OnDeviceOnline(new AdbDeviceEventArgs(device));
                }
                else if (Device is not null && !connectionStatus)
                    OnDeviceOffline(new AdbDeviceEventArgs(Device));
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Error while checking device connection");
        }
        finally
        {
            DeviceSemaphoreSlim.Release();
        }

        FirstDeviceSearch = false;
        return connectionStatus;
    }

    private void RefreshDeviceList()
    {
        try
        {
            var skipScan = DeviceListSemaphoreSlim.CurrentCount == 0;
            DeviceListSemaphoreSlim.Wait();
            try
            {
                EnsureADBRunning();
            }
            catch
            {
                return;
            }

            if (skipScan) return;
            var deviceList = GetOculusDevices();
            var listChanged = deviceList.Any(device => DeviceList.All(x => x.Serial != device.Serial))
                              || DeviceList.Any(device => deviceList.All(x => x.Serial != device.Serial));
            if (!listChanged) return;
            DeviceList = deviceList;
            _deviceListChangeSubject.OnNext(deviceList);
        }
        finally
        {
            DeviceListSemaphoreSlim.Release();
        }
    }

    // Respect preferred connection type
    private void CheckConnectionPreference()
    {
        if (Device is null) return;
        var preferredConnectionType = _sideloaderSettings.PreferredConnectionType;
        switch (Device.IsWireless)
        {
            case true when preferredConnectionType == "USB":
            {
                if (DeviceList.FirstOrDefault(x => x.TrueSerial == Device.TrueSerial && !x.IsWireless) is
                    { } preferredDevice)
                {
                    Log.Information("Auto-switching to preferred connection type ({ConnectionType})", 
                        preferredConnectionType);
                    TrySwitchDevice(preferredDevice);
                }
                break;
            }
            case false when preferredConnectionType == "Wireless":
            {
                if (DeviceList.FirstOrDefault(x => x.TrueSerial == Device.TrueSerial && x.IsWireless) is
                    { } preferredDevice)
                {
                    Log.Information("Auto-switching to preferred connection type ({ConnectionType})", 
                        preferredConnectionType);
                    TrySwitchDevice(preferredDevice);
                }
                break;
            }
        }
    }
    
    private void EnsureADBRunning()
    {
        try
        {
            var restartNeeded = false;
            AdbServerSemaphoreSlim.Wait();
            try
            {
                var adbServerStatus = _adb.AdbServer.GetStatus();
                if (adbServerStatus.IsRunning)
                {
                    var requiredAdbVersion = new Version("1.0.40");
                    if (adbServerStatus.Version >= requiredAdbVersion)
                    {
                        StartDeviceMonitor(false);
                        return;
                    }

                    restartNeeded = true;
                    Log.Warning("ADB daemon is outdated and will be restarted");
                }
            }
            catch
            {
                Log.Warning("Failed to check ADB server status");
                restartNeeded = true;
            }

            Log.Information("Starting ADB server");

            // Workaround for issues with AdbServer.StartServer
            try
            {
                var adbPath = PathHelper.AdbPath;
                if (restartNeeded)
                    try
                    {
                        Cli.Wrap(adbPath)
                            .WithArguments("kill-server")
                            .ExecuteBufferedAsync()
                            .GetAwaiter().GetResult();
                    }
                    catch
                    {
                        Array.ForEach(Process.GetProcessesByName("adb"), p => p.Kill());
                    }

                Cli.Wrap(adbPath)
                    .WithArguments("start-server")
                    .ExecuteBufferedAsync()
                    .GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                // TODO: handle failures
                Log.Error(e, "Failed to start ADB server");
                throw new AdbServiceException("Failed to start ADB server", e);
            }

            if (!_adb.AdbServer.GetStatus().IsRunning)
            {
                Log.Error("Failed to start ADB server");
                throw new AdbServiceException("Failed to start ADB server");
            }

            _adb.AdbClient.Connect("127.0.0.1:62001");
            Log.Information("Started ADB server");
            StartDeviceMonitor(true);
        }
        finally
        {
            AdbServerSemaphoreSlim.Release();
        }
    }

    private void WakeDevice(DeviceData device)
    {
        RunShellCommand(device, "input keyevent KEYCODE_WAKEUP");
    }
    
    private bool PingDevice(DeviceData device)
    {
        WakeDevice(device);
        return device.State == DeviceState.Online && RunShellCommand(device, "echo 1")?.Trim() == "1";
    }

    private void StartDeviceMonitor(bool restart)
    {
        if (_deviceMonitor is null || restart)
        {
            if (_deviceMonitor is not null)
                _deviceMonitor.DeviceChanged -= OnDeviceChanged;
            _deviceMonitor = new DeviceMonitor(new AdbSocket(new IPEndPoint(IPAddress.Loopback, 5037)));
        }
        if (_deviceMonitor.IsRunning) return;
        _deviceMonitor.DeviceChanged += OnDeviceChanged;
        _deviceMonitor.Start();
        Log.Debug("Started device monitor");
    }

    private void OnDeviceChanged(object? sender, DeviceDataEventArgs e)
    {
        Log.Debug("OnDeviceChanged: got event. Device State = {DeviceState}", e.Device.State);
        switch (e.Device.State)
        {
            case DeviceState.Online:
                if (DeviceList.All(x => x.Serial != e.Device.Serial))
                {
                    RefreshDeviceList();
                    CheckConnectionPreference();
                }
                CheckDeviceConnection();
                break;
            case DeviceState.Offline:
                if (e.Device.Serial == Device?.Serial)
                    CheckDeviceConnection(true);
                else
                {
                    RefreshDeviceList();
                    CheckConnectionPreference();
                }
                break;
            case DeviceState.Unauthorized:
            case DeviceState.BootLoader:
            case DeviceState.Host:
            case DeviceState.Recovery:
            case DeviceState.NoPermissions:
            case DeviceState.Sideload:
            case DeviceState.Authorizing:
            case DeviceState.Unknown:
            default:
                break;
        }
    }

    private void OnDeviceOffline(AdbDeviceEventArgs e)
    {
        Log.Information("Device {Device} disconnected", e.Device);
        e.Device.State = DeviceState.Offline;
        _deviceChangeSubject.OnNext(e.Device);
        Device = null;
    }

    private void OnDeviceOnline(AdbDeviceEventArgs e)
    {
        Log.Information("Connected to device {Device}", e.Device);
        e.Device.State = DeviceState.Online;
        Device = e.Device;
        if (!FirstDeviceSearch)
            //DeviceOnline?.Invoke(this, e);
            _deviceChangeSubject.OnNext(e.Device);
    }

    private static string GetHashedId(string deviceSerial)
    {
        using var sha256Hash = SHA256.Create();
        var hashedId = Convert.ToHexString(
            sha256Hash.ComputeHash(
                Encoding.ASCII.GetBytes(deviceSerial)))[..16];
        return hashedId;
    }

    private List<AdbDevice> GetOculusDevices()
    {
        Log.Information("Searching for devices");
        List<AdbDevice> oculusDeviceList = new();
        var deviceList = _adb.AdbClient.GetDevices();
        if (deviceList.Count == 0)
        {
            Log.Warning("No ADB devices found");
            return oculusDeviceList;
        }

        foreach (var device in deviceList)
        {
            var hashedDeviceId = GetHashedId(device.Serial);
            if (IsOculusQuest(device))
            {
                try
                {
                    var adbDevice = new AdbDevice(device, this);
                    oculusDeviceList.Add(adbDevice);
                    Log.Information("Found Oculus Quest device: {Device}", adbDevice);
                }
                catch
                {
                    // ignored
                }

                continue;
            }

            if (device.State == DeviceState.Online)
                Log.Information("Not an Oculus Quest device! ({HashedDeviceId} - {Product})", hashedDeviceId,
                    device.Product);
            else
                Log.Information("Found device in {State} state! ({HashedDeviceId})", device.State, hashedDeviceId);
        }

        if (oculusDeviceList.Count == 0)
        {
            Log.Warning("No Oculus devices found");
            return oculusDeviceList;
        }

        return Globals.SideloaderSettings.PreferredConnectionType switch
        {
            "USB" => oculusDeviceList.OrderBy(x => x.IsWireless).ToList(),
            "Wireless" => oculusDeviceList.OrderByDescending(x => x.IsWireless).ToList(),
            _ => oculusDeviceList
        };
    }

    public void TrySwitchDevice(AdbDevice device)
    {
        if (device.Serial == Device?.Serial) return;
        if (device.State != DeviceState.Online || !PingDevice(device))
        {
            Log.Debug("Attempted switch to offline device {Device}", device);
            RefreshDeviceList();
            return;
        }
        Log.Information("Switching to device {Device}", device);
        OnDeviceOnline(new AdbDeviceEventArgs(device));
    }

    public async Task EnableWirelessAdbAsync(AdbDevice device)
    {
        Log.Information("Enabling Wireless ADB");
        if (device.IsWireless)
        {
            Log.Warning("Device {Device} is already wireless!", device);
            return;
        }

        try
        {
            var host = device.EnableWirelessAdb();
            await TryConnectWirelessAdbAsync(host, true);
            _sideloaderSettings.LastWirelessAdbHost = host;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to enable Wireless ADB");
        }
    }
    
    private async Task TryConnectWirelessAdbAsync(string host, bool silent = false)
    {
        if (DeviceList.Any(x => x.Serial.Contains(host)))
        {
            if (!silent)
                Log.Debug("Wireless device on {Host} is already connected, skipping", host);
            return;
        }
        if (!silent)
            Log.Debug("Trying to connect wireless device, host {Host}", host);
        
        try
        {
            await Task.Delay(1000);
            for (var i = 0; i < 10; i++)
            {
                _adb.AdbClient.Connect(host);
                if (_adb.AdbClient.GetDevices().Any(x => x.Serial.Contains(host)))
                {
                    RefreshDeviceList();
                    _sideloaderSettings.LastWirelessAdbHost = host;
                    break;
                }

                await Task.Delay(300);
            }
        }
        catch
        {
            if (!silent)
                Log.Warning("Couldn't connect wireless device");
            _sideloaderSettings.LastWirelessAdbHost = "";
        }
    }

    private static bool IsOculusQuest(DeviceData device)
    {
        return device.Product is "hollywood" or "monterey" or "vr_monterey";
    }

    public static async Task TakeSideloadLockAsync(CancellationToken ct = default)
    {
        await SideloadSemaphoreSlim.WaitAsync(ct);
    }

    public static void ReleaseSideloadLock()
    {
        SideloadSemaphoreSlim.Release();
    }

    private class AdbServerClient
    {
        public AdvancedAdbClient AdbClient { get; } = new();
        public AdbServer AdbServer { get; } = new();
    }


    public class AdbDevice : DeviceData
    {
        private readonly AdbServerClient _adb;
        private readonly AdbService _adbService;
        private readonly SideloaderSettingsViewModel _sideloaderSettings;
        private static readonly SemaphoreSlim DeviceInfoSemaphoreSlim = new(1, 1);
        private static readonly SemaphoreSlim PackagesSemaphoreSlim = new(1, 1);

        public AdbDevice(DeviceData deviceData, AdbService adbService)
        {
            _adbService = adbService;
            _adb = adbService._adb;
            _sideloaderSettings = Globals.SideloaderSettings;
            
            Serial = deviceData.Serial;
            IsWireless = deviceData.Serial.Contains('.');
            try
            {
                // corrected serial for wireless connection
                TrueSerial = _adbService.RunShellCommand(deviceData, "getprop ro.boot.serialno") ?? deviceData.Serial;
            }
            catch
            {
                // ignored
            }

            State = deviceData.State;
            Model = deviceData.Model;
            Product = deviceData.Product;
            Name = deviceData.Name;
            Features = deviceData.Features;
            Usb = deviceData.Usb;
            TransportId = deviceData.TransportId;
            Message = deviceData.Message;

            FriendlyName = deviceData.Product switch
            {
                "monterey" => "Quest 1",
                "hollywood" => "Quest 2",
                _ => "Unknown?"
            };

            HashedId = GetHashedId(TrueSerial ?? Serial);
            
            PackageManager = new PackageManager(_adb.AdbClient, this, true);
        }

        public override string ToString()
        {
            return IsWireless ? $"{HashedId} (wireless)" : HashedId;
        }

        public string? RunShellCommand(string command, bool logCommand = false)
        {
            return _adbService.RunShellCommand(this, command, logCommand);
        }

        public void RefreshInstalledPackages()
        {
            PackagesSemaphoreSlim.Wait();
            try
            {
                _ = PackageManager ?? throw new InvalidOperationException("PackageManager must be initialized");
                PackageManager.RefreshPackages();
                InstalledPackages = new List<string>(PackageManager.Packages.Keys);
            }
            finally
            {
                PackagesSemaphoreSlim.Release();
            }
        }

        public void RefreshInfo()
        {
            // Check whether refresh is already running
            var alreadyRefreshing = DeviceInfoSemaphoreSlim.CurrentCount < 1;
            DeviceInfoSemaphoreSlim.Wait();
            try
            {
                Log.Debug("Refreshing device info");
                // If device info has just been refreshed we can skip
                if (alreadyRefreshing)
                {
                    Log.Debug("Device info already refreshed, skipping");
                    return;
                }

                var dfOutput = RunShellCommand("df /storage/emulated") ??
                               throw new AdbServiceException("RefreshInfo: df RunShellCommand returned null");
                var dfOutputSplit = dfOutput.Split(new[] {"\r\n", "\r", "\n"}, StringSplitOptions.None);
                var line = Regex.Split(dfOutputSplit[1], @"\s{1,}");
                SpaceTotal = (float) Math.Round(float.Parse(line[1]) / 1000000, 2);
                SpaceUsed = (float) Math.Round(float.Parse(line[2]) / 1000000, 2);
                SpaceFree = (float) Math.Round(float.Parse(line[3]) / 1000000, 2);

                var dumpsysOutput = RunShellCommand("dumpsys battery | grep level") ??
                                    throw new AdbServiceException(
                                        "RefreshInfo: dumpsys RunShellCommand returned null");
                BatteryLevel = int.Parse(Regex.Match(dumpsysOutput, @"[0-9]{1,3}").ToString());
                Log.Debug("Refreshed device info");
            }
            catch
            {
                SpaceTotal = 0;
                SpaceUsed = 0;
                SpaceFree = 0;
                Log.Debug("Failed to refresh device info");
            }
            finally
            {
                DeviceInfoSemaphoreSlim.Release();
            }
        }

        public List<InstalledGame> GetInstalledGames()
        {
            PackagesSemaphoreSlim.Wait();
            try
            {
                _ = Globals.AvailableGames ??
                    throw new InvalidOperationException("Globals.AvailableGames must be initialized");
                _ = PackageManager ?? throw new InvalidOperationException("PackageManager must be initialized");
                // PackageManager.GetVersionInfo can return null
                var query = from package in InstalledPackages
                    let versionInfo = PackageManager.GetVersionInfo(package)
                    let versionCode = versionInfo?.VersionCode ?? 0
                    let game = Globals.AvailableGames.FirstOrDefault(g => g.PackageName == package)
                    where game != null
                    select new InstalledGame(game, versionCode);
                var installedGames = query.ToList();
                Log.Debug("Found {Count} installed games: {InstalledGames}", installedGames.Count,
                    installedGames.Select(x => x.PackageName));
                return installedGames;
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to get installed games");
                return new List<InstalledGame>();
            }
            finally
            {
                PackagesSemaphoreSlim.Release();
            }
        }

        private void PushFile(string localPath, string remotePath)
        {
            Log.Debug("Pushing file: \"{LocalPath}\" -> \"{RemotePath}\"", localPath, remotePath);
            using var syncService = new SyncService(_adb.AdbClient, this);
            using var file = File.OpenRead(localPath);
            syncService.Push(file, remotePath, 771, DateTime.Now, null, CancellationToken.None);
        }

        private void PushDirectory(string localPath, string remotePath)
        {
            if (!remotePath.EndsWith("/"))
                remotePath += "/";
            var localDir = new DirectoryInfo(localPath).Name;
            Log.Debug("Pushing directory: \"{LocalPath}\" -> \"{RemotePath}\"",
                localPath, remotePath + localPath.Replace("\\", "/"));
            var dirList = Directory.GetDirectories(localPath, "*", SearchOption.AllDirectories).ToList();
            var relativeDirList = dirList.Select(dirPath => Path.GetRelativePath(localPath, dirPath));

            RunShellCommand($"mkdir -p \"{remotePath + localDir}/\"", true);
            foreach (var dirPath in relativeDirList)
                RunShellCommand($"mkdir -p \"{remotePath + localDir + "/" + dirPath.Replace("./", "")}\"", true);

            var fileList = Directory.EnumerateFiles(localPath, "*.*", SearchOption.AllDirectories);
            var relativeFileList = fileList.Select(filePath => Path.GetRelativePath(localPath, filePath));
            foreach (var file in relativeFileList)
                PushFile(localPath + Path.DirectorySeparatorChar + file,
                    remotePath + localDir + "/" + file.Replace(@"\", "/"));
        }

        private void PullFile(string remotePath, string localPath)
        {
            var remoteFileName = remotePath.Split('/').Last(x => !string.IsNullOrEmpty(x));
            var localFilePath = localPath + Path.DirectorySeparatorChar + remoteFileName;
            Log.Debug("Pulling file: \"{RemotePath}\" -> \"{LocalPath}\"", remotePath, localFilePath);
            using var syncService = new SyncService(_adb.AdbClient, this);
            using var file = File.OpenWrite(localFilePath);
            syncService.Pull(remotePath, file,  null, CancellationToken.None);
        }

        private void PullDirectory(string remotePath, string localPath, string[]? excludeDirs = default)
        {
            if (!remotePath.EndsWith("/"))
                remotePath += "/";
            var remoteDirName = remotePath.Split('/').Last(x => !string.IsNullOrEmpty(x));
            var localDir = Path.Combine(localPath, remoteDirName);
            Directory.CreateDirectory(localDir);
            Log.Debug("Pulling directory: \"{RemotePath}\" -> \"{LocalPath}\"",
                remotePath, localDir);
            using var syncService = new SyncService(_adb.AdbClient, this);
            // Get listing for remote directory, excluding directories in excludeDirs
            var remoteDirectoryListing = syncService.GetDirectoryListing(remotePath).Where(x =>
                excludeDirs is null || !excludeDirs.Contains(x.Path.Split('/').Last(s => !string.IsNullOrEmpty(s)))).ToList();
            foreach (var remoteFile in remoteDirectoryListing.Where(x => x.Path != "." && x.Path != ".."))
            {
                if (remoteFile.FileMode.HasFlag(UnixFileMode.Directory))
                {
                    PullDirectory(remotePath + remoteFile.Path, localDir, excludeDirs);
                }
                else if (remoteFile.FileMode.HasFlag(UnixFileMode.Regular))
                {
                    PullFile(remotePath + remoteFile.Path, localDir);
                }
            }
        }

        private bool DirectoryExists(string path)
        {
            try
            {
                using var syncService = new SyncService(_adb.AdbClient, this);
                return syncService.Stat(path).FileMode.HasFlag(UnixFileMode.Directory);
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to stat the provided path");
                return false;
            }
        }

        public IObservable<string> SideloadGame(Game game, string gamePath)
        {
            return Observable.Create<string>(observer =>
            {
                var reinstall = false;
                try
                {
                    _ = PackageManager ?? throw new InvalidOperationException("PackageManager must be initialized");
                    Log.Information("Sideloading game {GameName}", game.GameName);
                    
                    if (game.PackageName is not null)
                        reinstall = PackageManager.Packages.ContainsKey(game.PackageName);

                    if (File.Exists(Path.Combine(gamePath, "install.txt")))
                    {
                        observer.OnNext("Performing custom install");
                        Log.Information("Running commands from install.txt");
                        RunInstallScript(Path.Combine(gamePath, "install.txt"));
                    }
                    else if (File.Exists(Path.Combine(gamePath, "Install.txt")))
                    {
                        observer.OnNext("Performing custom install");
                        Log.Information("Running commands from Install.txt");
                        RunInstallScript(Path.Combine(gamePath, "Install.txt"));
                    }
                    else
                        // install APKs, copy OBB dir
                    {
                        foreach (var apkPath in Directory.EnumerateFiles(gamePath, "*.apk"))
                        {
                            observer.OnNext("Installing APK");
                            InstallPackage(apkPath, false, true);
                        }

                        if (game.PackageName is not null && Directory.Exists(Path.Combine(gamePath, game.PackageName)))
                        {
                            observer.OnNext("Pushing OBB");
                            PushDirectory(Path.Combine(gamePath, game.PackageName), "/sdcard/Android/obb/");
                        }
                    }

                    Log.Information("Installed game {GameName}", game.GameName);
                    _adbService._packageListChangeSubject.OnNext(new Unit());
                    observer.OnCompleted();
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error installing game");
                    if (!reinstall)
                        CleanupRemnants(game);
                    observer.OnError(new AdbServiceException("Error installing game", e));
                }

                return Disposable.Empty;
            });
        }

        private void RunInstallScript(string scriptPath)
        {
            try
            {
                if (!File.Exists(scriptPath))
                    throw new ArgumentException("Install script path is not valid");
                var scriptName = Path.GetFileName(scriptPath);
                // Regex pattern to split command into list of arguments
                const string argsPattern = @"[\""].+?[\""]|[^ ]+";
                var gamePath = Path.GetDirectoryName(scriptPath)!;
                var scriptCommands = File.ReadAllLines(scriptPath);
                foreach (var archivePath in Directory.GetFiles(gamePath, "*.7z", SearchOption.TopDirectoryOnly))
                    ZipUtil.ExtractArchive(archivePath, gamePath);
                foreach (var rawCommand in scriptCommands)
                {
                    if (string.IsNullOrWhiteSpace(rawCommand) || rawCommand.StartsWith("#")) continue;
                    var command = rawCommand.Replace(" > NUL 2>&1", "");
                    Log.Information("{ScriptName}: Running command: \"{Command}\"", scriptName, command);
                    var args = Regex.Matches(command, argsPattern)
                        .Select(x => x.Value.Trim('"'))
                        .ToList();
                    if (args[0] == "adb")
                    {
                        var adbCommand = args[1];
                        args.RemoveRange(0, 2);
                        switch (adbCommand)
                        {
                            case "install":
                            {
                                var reinstall = args.Contains("-r");
                                var grantRuntimePermissions = args.Contains("-g");
                                var apkPath = Path.Combine(gamePath, args.First(x => x.EndsWith(".apk")));
                                InstallPackage(apkPath, reinstall, grantRuntimePermissions);
                                break;
                            }
                            case "uninstall":
                            {
                                var packageName = args.First();
                                // FIXME: backup for reinstall 
                                //BackupGame(packageName);
                                Log.Information("Ignoring uninstall command");
                                //UninstallPackage(packageName);
                                break;
                            }
                            case "push":
                            {
                                var source = Path.Combine(gamePath, args[0]);
                                var destination = Path.Combine(gamePath, args[1]);
                                if (Directory.Exists(source))
                                    PushDirectory(source, destination);
                                else
                                    PushFile(source, destination);
                                break;
                            }
                            case "shell":
                            {
                                args = args.Select(x => x.Contains(' ') ? $"\"{x}\"" : x).ToList();
                                if (args.Count > 2 && args[0] == "pm" && args[1] == "uninstall")
                                {
                                    var packageName = args[2];
                                    // FIXME: backup for reinstall 
                                    //BackupGame(packageName);
                                    Log.Information("Ignoring uninstall command");
                                    //UninstallPackage(packageName);
                                    break;
                                }

                                var shellCommand = string.Join(" ", args);
                                RunShellCommand(shellCommand, true);
                                break;
                            }
                            default:
                                throw new AdbServiceException("Encountered unknown adb command");
                        }
                    }
                    else
                    {
                        throw new AdbServiceException("Encountered unknown command");
                    }
                }
            }
            catch (Exception e)
            {
                throw new AdbServiceException("Failed to run install script", e);
            }
        }

        public void UninstallGame(Game game)
        {
            _ = game.PackageName ?? throw new ArgumentException("game.PackageName must not be null");
            try
            {
                Log.Information("Uninstalling package {PackageName}", game.PackageName);
                UninstallPackage(game.PackageName);
            }
            catch (PackageNotFoundException)
            {
                 Log.Warning("Package {PackageName} is not installed", game.PackageName);
            }
            CleanupRemnants(game);
            _adbService._packageListChangeSubject.OnNext(new Unit());
        }

        private void CleanupRemnants(Game game)
        {
            CleanupRemnants(game.PackageName 
                            ?? throw new ArgumentException("game.PackageName must not be null"));
        }

        private void CleanupRemnants(string packageName)
        {
            try
            {
                const string packageNamePattern = @"^([A-Za-z]{1}[A-Za-z\d_]*\.)+[A-Za-z][A-Za-z\d_]*$";
                if (string.IsNullOrWhiteSpace(packageName) || !Regex.IsMatch(packageName, packageNamePattern))
                    throw new ArgumentException("packageName is invalid");
                try
                {
                    UninstallPackage(packageName);
                }
                catch (PackageNotFoundException)
                {
                    // ignored
                }
                RunShellCommand(
                    $"rm -r /sdcard/Android/data/{packageName}/; rm -r /sdcard/Android/obb/{packageName}/");
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to clean up remnants of package {PackageName}", packageName);
            }
        }

        private void InstallPackage(string apkPath, bool reinstall, bool grantRuntimePermissions)
        {
            _ = PackageManager ?? throw new InvalidOperationException("PackageManager must be initialized");
            Log.Information("Installing APK: {ApkFileName}", Path.GetFileName(apkPath));
            // Using legacy method here as AdbClient.Install hangs occasionally
            PackageManager.InstallPackage(apkPath, reinstall, grantRuntimePermissions);
            // List<string> args = new ();
            // if (reinstall)
            //     args.Add("-r");
            // if (grantRuntimePermissions)
            //     args.Add("-g");
            // using Stream stream = File.OpenRead(apkPath);
            // Adb.AdbClient.Install(this, stream, args.ToArray());
            Log.Information("Package installed");
        }

        private void UninstallPackage(string packageName)
        {
            try
            {
                _adb.AdbClient.UninstallPackage(this, packageName);
            }
            catch (PackageInstallationException e)
            {
                if (e.Message == "DELETE_FAILED_INTERNAL_ERROR" && string.IsNullOrWhiteSpace(
                        RunShellCommand($"pm list packages -3 | grep -w \"package:{packageName}\"")))
                {
                    throw new PackageNotFoundException(packageName);
                }
                throw;
            }
        }

        public string EnableWirelessAdb()
        {
            const int port = 5555;
            const string ipAddressPattern = @"src ([\d]{1,3}.[\d]{1,3}.[\d]{1,3}.[\d]{1,3})";
            RunShellCommand("settings put global wifi_wakeup_available 1");
            RunShellCommand("settings put global wifi_wakeup_enabled 1");
            RunShellCommand("settings put global wifi_sleep_policy 2");
            RunShellCommand("settings put global wifi_suspend_optimizations_enabled 0");
            RunShellCommand("settings put global wifi_watchdog_poor_network_test_enabled 0");
            RunShellCommand("svc wifi enable");
            var ipRouteOutput = RunShellCommand("ip route");
            var ipAddress = Regex.Match(ipRouteOutput!, ipAddressPattern).Groups[1].ToString();
            _adb.AdbClient.TcpIp(this, port);
            return ipAddress;
        }

        public void BackupGame(Game game, bool backupData = true, bool backupApk = false, bool backupObb = false)
        {
            BackupGame(game.PackageName!, backupData, backupApk, backupObb);
        }

        public void BackupGame(string packageName, bool backupData = true, bool backupApk = false, bool backupObb = false)
        {
            Log.Information("Backing up {PackageName}", packageName);
            var backupPath = Path.Combine(_sideloaderSettings.BackupsLocation, $"{DateTime.Now:yyyyMMddTHHmmss}_{packageName}");
            var backupMetadataPath = Path.Combine(backupPath, "backup.json");
            var dataPath = $"/sdcard/Android/data/{packageName}/";
            var dataBackupPath = Path.Combine(backupPath, "data");
            var obbPath = $"/sdcard/Android/obb/{packageName}/";
            var obbBackupPath = Path.Combine(backupPath, "obb");
            const string apkPathPattern = @"package:(\S+)";
            var apkPath = Regex.Match(RunShellCommand($"pm path {packageName}")!, apkPathPattern).Groups[1]
                .ToString();
            Directory.CreateDirectory(backupPath);
            var empty = true;
            if (backupData && DirectoryExists(dataPath))
            {
                empty = false;
                Directory.CreateDirectory(dataBackupPath);
                PullDirectory(dataPath, dataBackupPath, new []{"cache"});
            }
            if (backupApk)
            {
                empty = false;
                PullFile(apkPath, backupPath);
            }
            if (backupObb && DirectoryExists(obbPath))
            {
                empty = false;
                Directory.CreateDirectory(obbBackupPath);
                PullDirectory(obbPath, obbBackupPath);
            }

            if (!empty)
            {
                //var json = JsonConvert.SerializeObject(game);
                //File.WriteAllText("game.json", json);
            }
            else
            {
                Log.Information("Nothing to backup");
                Directory.Delete(backupPath, true);
            }
        }

        private PackageManager? PackageManager { get; }
        public float SpaceUsed { get; private set; }
        public float SpaceFree { get; private set; }
        public float SpaceTotal { get; private set; }
        public float BatteryLevel { get; private set; }
        private List<string> InstalledPackages { get; set; } = new();
        public string FriendlyName { get; }
        private string HashedId { get; }
        public string? TrueSerial { get; }
        public bool IsWireless { get; }
    }
    
    public class AdbDeviceEventArgs : EventArgs
    {
        public AdbDeviceEventArgs(AdbDevice device)
        {
            Device = device;
        }

        public AdbDevice Device { get; }
    }
}

public class AdbServiceException : Exception
{
    public AdbServiceException(string message)
        : base(message)
    {
    }

    public AdbServiceException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public class PackageNotFoundException : AdbServiceException
{
    public PackageNotFoundException(string packageName)
        : base($"Package {packageName} not found")
    {
    }
}