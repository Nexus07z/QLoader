﻿using System;
using System.IO;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient.DeviceCommands;
using Avalonia.Controls.Notifications;
using QSideloader.Common;
using QSideloader.Exceptions;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Services;
using QSideloader.Utilities;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;
using Serilog.Context;

namespace QSideloader.ViewModels;

public class TaskViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly AdbService _adbService;
    private AdbService.AdbDevice? _adbDevice;
    private bool _tiedToDevice;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly DownloaderService _downloaderService;
    private readonly Game? _game;
    private long? _gameSizeBytes;
    private readonly InstalledApp? _app;
    private readonly Backup? _backup;
    private readonly SettingsData _sideloaderSettings;
    private string? _path;
    private readonly BackupOptions? _backupOptions;

    /// <summary>
    /// Dummy constructor for XAML, do not use
    /// </summary>
    [Obsolete("Only for XAML", true)]
    public TaskViewModel()
    {
        _adbService = AdbService.Instance;
        _adbDevice = _adbService.Device;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        _game = new Game("GameName", "ReleaseName", 1337, "NoteText");
        TaskId = new TaskId();
        TaskName = "TaskName";
        ProgressStatus = "ProgressStatus";
        RunTask = ReactiveCommand.Create(() => { Hint = Resources.ClickToCancel; });
        Activator = new ViewModelActivator();
    }

    public TaskViewModel(TaskOptions taskOptions)
    {
        _adbService = AdbService.Instance;
        _adbDevice = _adbService.Device!;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        TaskId = new TaskId();
        TaskType = taskOptions.Type;
        Func<Task> action;
        Activator = new ViewModelActivator();
        switch (taskOptions.Type)
        {
            case TaskType.DownloadAndInstall:
                _game = taskOptions.Game ??
                        throw new ArgumentException(
                            $"Game not specified for {nameof(TaskType.DownloadAndInstall)} task");
                TaskName = _game.GameName ?? "N/A";
                action = RunDownloadAndInstallAsync;
                break;
            case TaskType.DownloadOnly:
                _game = taskOptions.Game ??
                        throw new ArgumentException($"Game not specified for {nameof(TaskType.DownloadOnly)} task");
                TaskName = _game.GameName ?? "N/A";
                action = RunDownloadOnlyAsync;
                break;
            case TaskType.InstallOnly:
                _game = taskOptions.Game ??
                        throw new ArgumentException($"Game not specified for {nameof(TaskType.InstallOnly)} task");
                _path = taskOptions.Path ??
                        throw new ArgumentException($"Game path not specified for {nameof(TaskType.InstallOnly)} task");
                TaskName = _game.GameName ?? "N/A";
                action = RunInstallOnlyAsync;
                break;
            case TaskType.Uninstall:
                if (taskOptions.Game is null && taskOptions.App is null)
                    throw new ArgumentException($"Game or App not specified for {nameof(TaskType.Uninstall)} task");
                if (taskOptions.Game is not null && taskOptions.App is not null)
                    throw new ArgumentException($"Game and App both specified for {nameof(TaskType.Uninstall)} task");
                _game = taskOptions.Game;
                _app = taskOptions.App;
                TaskName = _game?.GameName ?? _app?.Name ?? "N/A";
                action = RunUninstallAsync;
                break;
            case TaskType.BackupAndUninstall:
                _game = taskOptions.Game ??
                        throw new ArgumentException(
                            $"Game not specified for {nameof(TaskType.BackupAndUninstall)} task");
                _backupOptions = taskOptions.BackupOptions ??
                                 throw new ArgumentException(
                                     $"Backup options not specified for {nameof(TaskType.BackupAndUninstall)} task");
                TaskName = _game.GameName ?? "N/A";
                action = RunBackupAndUninstallAsync;
                break;
            case TaskType.Backup:
                _game = taskOptions.Game ??
                        throw new ArgumentException($"Game not specified for {nameof(TaskType.Backup)} task");
                _backupOptions = taskOptions.BackupOptions ??
                                 throw new ArgumentException(
                                     $"Backup options not specified for {nameof(TaskType.Backup)} task");
                TaskName = _game.GameName ?? "N/A";
                action = RunBackupAsync;
                break;
            case TaskType.Restore:
                _backup = taskOptions.Backup ??
                          throw new ArgumentException($"Backup not specified for {nameof(TaskType.Restore)} task");
                TaskName = _backup.Name;
                action = RunRestoreAsync;
                break;
            case TaskType.PullAndUpload:
                _app = taskOptions.App ??
                       throw new ArgumentException($"App not specified for {nameof(TaskType.PullAndUpload)} task");
                TaskName = _app.Name;
                action = RunPullAndUploadAsync;
                break;
            case TaskType.InstallTrailersAddon:
                _path = taskOptions.Path;
                action = RunInstallTrailersAddonAsync;
                TaskName = "Trailers addon";
                break;
            case TaskType.PullMedia:
                _path = taskOptions.Path ??
                        throw new ArgumentException($"Path not specified for {nameof(TaskType.PullMedia)}");
                action = RunPullMediaAsync;
                TaskName = "Pull pictures and videos";
                break;
            case TaskType.Extract:
                _app = taskOptions.App ??
                       throw new ArgumentException($"App not specified for {nameof(TaskType.Extract)} task");
                _path = taskOptions.Path ??
                        throw new ArgumentException($"Path not specified for {nameof(TaskType.Extract)}");
                TaskName = _app.Name;
                action = RunExtractAsync;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(taskOptions), "Unknown task type");
        }

        RunTask = ReactiveCommand.CreateFromTask(() =>
        {
            Hint = Resources.ClickToCancel;
            return action();
        });
        RunTask.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Task {TaskId} {TaskType} {TaskName} failed", TaskId, TaskType, TaskName);
            if (!IsFinished)
                OnFinished(TaskResult.UnknownError, ex);
        });
    }

    private ReactiveCommand<Unit, Unit> RunTask { get; }

    public TaskId TaskId { get; }
    public TaskType TaskType { get; }

    public string TaskName { get; }
    public bool IsFinished { get; private set; }
    public string? PackageName => _app?.PackageName ?? _game?.PackageName;
    [Reactive] public string Status { get; private set; } = "Status";
    [Reactive] public string ProgressStatus { get; private set; } = "";
    [Reactive] public string Hint { get; private set; } = "";

    public ViewModelActivator Activator { get; }

    public void Run()
    {
        RunTask.Execute().Subscribe();
    }

    private void RefreshDownloadStats((double downloadSpeedBytes, double downloadedBytes)? stats)
    {
        if (stats is null)
        {
            ProgressStatus = "";
            return;
        }

        double speedMBytes;
        double progressPercent;

        if (_gameSizeBytes is not null)
        {
            speedMBytes = Math.Round(stats.Value.downloadSpeedBytes / 1000000, 2);
            progressPercent = Math.Floor(stats.Value.downloadedBytes / (double) _gameSizeBytes * 100);
            if (progressPercent <= 100)
            {
                ProgressStatus = $"{progressPercent}%, {speedMBytes}MB/s";
                return;
            }
        }

        speedMBytes = Math.Round(stats.Value.downloadSpeedBytes / 1000000, 2);
        var downloadedMBytes = Math.Round(stats.Value.downloadedBytes / 1000000, 2);
        progressPercent = Math.Floor(downloadedMBytes / _game!.GameSize * 97);
        var progressPercentString = progressPercent <= 100 ? $"{progressPercent}%" : "--%";

        ProgressStatus = $"{progressPercentString}, {speedMBytes}MB/s";
    }

    private void RefreshDownloadStats((double bytesPerSecond, long downloadedBytes, long totalBytes) stats)
    {
        var speedMBytes = Math.Round(stats.bytesPerSecond / 1000000, 2);
        var progressPercent = Math.Floor((double) stats.downloadedBytes / stats.totalBytes * 100);
        var progressPercentString = progressPercent <= 100 ? $"{progressPercent}%" : "--%";

        ProgressStatus = $"{progressPercentString}, {speedMBytes}MB/s";
    }

    private async Task RunDownloadAndInstallAsync()
    {
        await EnsureDeviceConnectedAsync(false);
        await DoCancellableAsync(async () => { _path = await DownloadAsync(); }, TaskResult.DownloadFailed);


        await DoCancellableAsync(() =>
        {
            var deleteAfterInstall =
                _sideloaderSettings.DownloadsPruningPolicy == DownloadsPruningPolicy.DeleteAfterInstall;
            return InstallAsync(_path ?? throw new InvalidOperationException("path is null"),
                deleteAfterInstall);
        }, TaskResult.InstallFailed); // successResult isn't needed here (InstallAsync will call OnFinished)
    }

    private Task RunDownloadOnlyAsync()
    {
        return DoCancellableAsync(async () => { _path = await DownloadAsync(); }, TaskResult.DownloadFailed,
            TaskResult.DownloadSuccess);
    }

    private async Task RunInstallOnlyAsync()
    {
        await EnsureDeviceConnectedAsync(false);
        
        await DoCancellableAsync(() =>
        {
            _ = _path ?? throw new InvalidOperationException("path is null");
            var deleteAfterInstall = _path.StartsWith(_sideloaderSettings.DownloadsLocation) &&
                                     _sideloaderSettings.DownloadsPruningPolicy ==
                                     DownloadsPruningPolicy.DeleteAfterInstall;
            return InstallAsync(_path, deleteAfterInstall);
        }, TaskResult.InstallFailed); // successResult isn't needed here (InstallAsync will call OnFinished)
    }

    private async Task RunUninstallAsync()
    {
        await EnsureDeviceConnectedAsync(false);
        await DoCancellableAsync(UninstallAsync, TaskResult.UninstallFailed,
            TaskResult.UninstallSuccess);
    }

    private async Task RunBackupAndUninstallAsync()
    {
        await EnsureDeviceConnectedAsync(false);
        await DoCancellableAsync(async () =>
        {
            await BackupAsync();
            await UninstallAsync();
        }, TaskResult.UninstallFailed, TaskResult.UninstallSuccess);
    }

    private async Task RunBackupAsync()
    {
        await EnsureDeviceConnectedAsync(false);
        await DoCancellableAsync(BackupAsync, TaskResult.BackupFailed,
            TaskResult.BackupSuccess);
    }

    private async Task RunRestoreAsync()
    {
        await EnsureDeviceConnectedAsync(false);
        await DoCancellableAsync(() => RestoreAsync(_backup!), TaskResult.RestoreFailed,
            TaskResult.RestoreSuccess);
    }

    private async Task RunPullAndUploadAsync()
    {
        await EnsureDeviceConnectedAsync();
        await DoCancellableAsync(async () =>
        {
            Status = Resources.PullingFromDevice;
            var path = await _adbDevice!.PullAppAsync(_app!.PackageName, "donations", _cancellationTokenSource.Token);
            Status = Resources.PreparingToUpload;
            var apkInfo = await GeneralUtils.GetApkInfoAsync(Path.Combine(path, _app!.PackageName + ".apk"));
            if (string.IsNullOrEmpty(apkInfo.PackageName))
                throw new InvalidOperationException("Failed to get package name from APK");
            if (string.IsNullOrEmpty(apkInfo.ApplicationLabel))
                throw new InvalidOperationException("Application label is empty");
            var archiveName =
                GeneralUtils.SanitizeFileName(
                    $"{apkInfo.ApplicationLabel} v{apkInfo.VersionCode} {apkInfo.PackageName}.zip");
            await File.WriteAllTextAsync(Path.Combine(path, "HWID.txt"),
                Convert.ToHexString(SHA256.HashData(Globals.SideloaderSettings.InstallationId.ToByteArray())));
            var archivePath = await ZipUtil.CreateArchiveAsync(path, "donations",
                archiveName, _cancellationTokenSource.Token);
            Directory.Delete(path, true);
            Status = Resources.Uploading;
            await _downloaderService.UploadDonationAsync(archivePath,
                _cancellationTokenSource.Token);
            await Globals.MainWindowViewModel!.OnGameDonatedAsync(apkInfo.PackageName, apkInfo.VersionCode);
        }, TaskResult.DonationFailed, TaskResult.DonationSuccess);
    }

    private Task RunInstallTrailersAddonAsync()
    {
        return DoCancellableAsync(() =>
        {
            if (!Directory.Exists(PathHelper.TrailersPath) || File.Exists(_path))
                return InstallTrailersAddonAsync();
            OnFinished(TaskResult.AlreadyInstalled);
            return Task.CompletedTask;

        }, TaskResult.InstallFailed, TaskResult.InstallSuccess);
    }

    private async Task RunExtractAsync()
    {
        await EnsureDeviceConnectedAsync();
        Status = Resources.PullingFromDevice;
        await DoCancellableAsync(
            async () => await _adbDevice!.PullAppAsync(_app!.PackageName, _path!, _cancellationTokenSource.Token),
            TaskResult.ExtractionFailed, TaskResult.ExtractionSuccess);
    }

    private async Task RunPullMediaAsync()
    {
        await EnsureDeviceConnectedAsync();
        Status = Resources.PullingPicturesAndVideos;
        await DoCancellableAsync(() => _adbDevice!.PullMediaAsync(_path!, _cancellationTokenSource.Token),
            TaskResult.PullMediaFailed, TaskResult.PullMediaSuccess);
    }


    private async Task DoCancellableAsync(Func<Task> func, TaskResult? failureResult = null,
        TaskResult? successResult = null)
    {
        try
        {
            await func();
            if (successResult is not null)
                OnFinished(successResult.Value);
        }
        catch (OperationCanceledException)
        {
            OnFinished(TaskResult.Cancelled);
        }
        catch (DownloaderServiceException e) when (e.InnerException is NotEnoughDiskSpaceException)
        {
            OnFinished(TaskResult.NotEnoughDiskSpace, e);
        }
        catch (Exception e)
        {
            if (failureResult is not null)
                OnFinished(failureResult.Value, e);
        }
    }

    private async Task<string> DownloadAsync()
    {
        var downloadStatsSubscription = Disposable.Empty;
        Status = Resources.DownloadQueued;
        await DownloaderService.TakeDownloadLockAsync(_cancellationTokenSource.Token);
        try
        {
            Status = Resources.CalculatingSize;
            _gameSizeBytes = await _downloaderService.GetGameSizeBytesAsync(_game!, _cancellationTokenSource.Token);
            Status = Resources.Downloading;
            var statsPort = DownloaderService.GetAvailableStatsPort();
            if (statsPort is not null)
            {
                Log.Debug("Polling download stats on port {Port}", statsPort);
                downloadStatsSubscription = DownloaderService
                    .PollStats(statsPort.Value, TimeSpan.FromMilliseconds(100), ThreadPoolScheduler.Instance)
                    .SubscribeOn(RxApp.TaskpoolScheduler)
                    .Subscribe(RefreshDownloadStats);
            }

            var gamePath =
                await _downloaderService.DownloadGameAsync(_game!, statsPort, _cancellationTokenSource.Token);
            downloadStatsSubscription.Dispose();
            if (_game?.ReleaseName is not null && TaskType != TaskType.DownloadOnly)
                _downloaderService.PruneDownloadedVersions(_game.ReleaseName);
            return gamePath;
        }
        finally
        {
            ProgressStatus = "";
            downloadStatsSubscription.Dispose();
            DownloaderService.ReleaseDownloadLock();
        }
    }

    private async Task InstallAsync(string gamePath, bool deleteAfterInstall = false)
    {
        Status = Resources.InstallQueued;
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            await EnsureDeviceConnectedAsync();
        }
        catch (InvalidOperationException)
        {
            AdbService.ReleasePackageOperationLock();
            throw;
        }

        using var _ = LogContext.PushProperty("Device", _adbDevice!.ToString());
        Status = Resources.Installing;

        // Here I assume that Install is the last step in the process, this might change in the future
        _adbDevice!.SideloadGame(_game!, gamePath, _cancellationTokenSource.Token)
            .SubscribeOn(RxApp.TaskpoolScheduler)
            .Subscribe(
                x =>
                {
                    Status = x.status;
                    ProgressStatus = x.progress ?? "";
                },
                e =>
                {
                    AdbService.ReleasePackageOperationLock();
                    if (e is OperationCanceledException)
                        OnFinished(TaskResult.Cancelled);
                    else if (e.InnerException is PackageInstallationException &&
                             e.InnerException.Message.Contains("INSTALL_FAILED_OLDER_SDK"))
                        OnFinished(TaskResult.OsVersionTooOld, e);
                    else if (e.InnerException is NotEnoughDeviceSpaceException ||
                             e.InnerException?.InnerException is NotEnoughDeviceSpaceException ||
                             e.ToString().Contains("No space left on device"))
                        OnFinished(TaskResult.NotEnoughDeviceSpace, e);
                    else
                        OnFinished(TaskResult.InstallFailed, e);
                },
                () =>
                {
                    ProgressStatus = "";
                    AdbService.ReleasePackageOperationLock();
                    if (deleteAfterInstall && Directory.Exists(gamePath))
                    {
                        Log.Information("Deleting downloaded files from {Path}", gamePath);
                        Status = Resources.DeletingDownloadedFiles;
                        try
                        {
                            Directory.Delete(gamePath, true);
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, "Failed to delete downloaded files");
                            // Treating as success because the installation is still successful
                            OnFinished(TaskResult.DownloadCleanupFailed);
                        }
                    }

                    OnFinished(TaskResult.InstallSuccess);
                });
    }

    private async Task UninstallAsync()
    {
        Status = Resources.UninstallQueued;
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            await EnsureDeviceConnectedAsync();
            using var _ = LogContext.PushProperty("Device", _adbDevice!.ToString());
            Status = Resources.Uninstalling;
            if (_game is not null)
                await _adbDevice!.UninstallPackageAsync(_game.PackageName);
            else if (_app is not null)
                await _adbDevice!.UninstallPackageAsync(_app.PackageName);
            else
                throw new InvalidOperationException("Both game and app are null");
        }
        finally
        {
            AdbService.ReleasePackageOperationLock();
        }
    }

    private async Task BackupAsync()
    {
        Status = Resources.BackupQueued;
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            await EnsureDeviceConnectedAsync();
            using var _ = LogContext.PushProperty("Device", _adbDevice!.ToString());
            Status = Resources.CreatingBackup;
            await _adbDevice!.CreateBackupAsync(_game!.PackageName!, _backupOptions!, _cancellationTokenSource.Token);
        }
        finally
        {
            AdbService.ReleasePackageOperationLock();
        }
    }

    private async Task RestoreAsync(Backup backup)
    {
        Status = Resources.RestoreQueued;
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            await EnsureDeviceConnectedAsync();
            using var _ = LogContext.PushProperty("Device", _adbDevice!.ToString());
            Status = Resources.RestoringBackup;
            await _adbDevice!.RestoreBackupAsync(backup);
        }
        finally
        {
            AdbService.ReleasePackageOperationLock();
        }
    }

    private async Task InstallTrailersAddonAsync()
    {
        Status = Resources.Downloading;
        if (!File.Exists(_path))
        {
            var progress =
                new DelegateProgress<(double bytesPerSecond, long downloadedBytes, long totalBytes)>(
                    RefreshDownloadStats);
            _path = await _downloaderService.DownloadTrailersAddon(progress, _cancellationTokenSource.Token);
        }

        Status = Resources.Installing;
        ProgressStatus = "";
        await GeneralUtils.InstallTrailersAddonAsync(_path, true);
        OnFinished(TaskResult.InstallSuccess);
    }

    private void OnFinished(TaskResult result, Exception? e = null)
    {
        if (IsFinished)
            return;
        var isSuccess = result.IsSuccess();
        Hint = Resources.ClickToDismiss;
        IsFinished = true;
        Status = result.GetMessage();
        ProgressStatus = "";
        Log.Information("Task {TaskId} {TaskType} {TaskName} finished. Result: {Status}. Is success: {IsSuccess}",
            TaskId, TaskType, TaskName,
            result, isSuccess);
        Globals.MainWindowViewModel!.OnTaskFinished(isSuccess, TaskId);
        if (isSuccess) return;
        if (e is not null)
        {
            Log.Error(e, "Task {TaskName} failed", TaskName);
            Globals.ShowErrorNotification(e, string.Format(Resources.TaskNameFailed, TaskName));
        }
        else
        {
            Log.Error("Task {TaskName} failed", TaskName);
            Globals.ShowNotification(Resources.Error, string.Format(Resources.TaskNameFailed, TaskName),
                NotificationType.Error, TimeSpan.Zero);
        }
    }

    public void Cancel()
    {
        if (_cancellationTokenSource.IsCancellationRequested || IsFinished) return;
        _cancellationTokenSource.Cancel();
        Log.Information("Requested cancellation of task {TaskType} {TaskName}", TaskType, TaskName);
    }

    /// <summary>
    /// Ensure that the device is connected and it's the correct device.
    /// </summary>
    /// <param name="tieDevice">Whether to tie the task to the current device.</param>
    /// <exception cref="InvalidOperationException">Thrown if device is not connected.</exception>
    private async Task EnsureDeviceConnectedAsync(bool tieDevice = true)
    {
        if (!_tiedToDevice)
        {
            if (await _adbService.CheckDeviceConnectionAsync())
            {
                // If user switched to another device before we tied to a specific device, we can reassign the device
                _adbDevice = _adbService.Device!;
                if (tieDevice)
                    _tiedToDevice = true;
                return;
            }
        }
        // If we have already tied to a specific device, we only want to see that device
        else
        {
            if (_adbDevice is not null && await _adbDevice.PingAsync())
                return;
        }

        OnFinished(TaskResult.NoDeviceConnection);
        throw new InvalidOperationException("No device connection");
    }
}