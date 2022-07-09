﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using AdvancedSharpAdbClient;
using Avalonia.Threading;
using DynamicData;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.Services;
using QSideloader.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class InstalledGamesViewModel : ViewModelBase, IActivatableViewModel
{
    private static readonly SemaphoreSlim RefreshSemaphoreSlim = new(1, 1);
    private readonly AdbService _adbService;
    private readonly ReadOnlyObservableCollection<InstalledGame> _installedGames;
    private readonly SourceCache<InstalledGame, string> _installedGamesSourceCache = new(x => x.ReleaseName!);
    private readonly ObservableAsPropertyHelper<bool> _isBusy;

    public InstalledGamesViewModel()
    {
        _adbService = AdbService.Instance;
        Activator = new ViewModelActivator();
        Refresh = ReactiveCommand.CreateFromObservable(() => RefreshImpl());
        ManualRefresh = ReactiveCommand.CreateFromObservable(() => RefreshImpl(true));
        var isBusyCombined = Observable.Empty<bool>().StartWith(false)
            .CombineLatest(Refresh.IsExecuting, (x, y) => x || y)
            .CombineLatest(ManualRefresh.IsExecuting, (x, y) => x || y);
        isBusyCombined.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Update = ReactiveCommand.CreateFromObservable(UpdateImpl);
        UpdateAll = ReactiveCommand.CreateFromObservable(UpdateAllImpl);
        Uninstall = ReactiveCommand.CreateFromObservable(UninstallImpl);
        var cacheListBind = _installedGamesSourceCache.Connect()
            .RefCount()
            .SortBy(x => x.ReleaseName!)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _installedGames)
            .DisposeMany();
        this.WhenActivated(disposables =>
        {
            cacheListBind.Subscribe().DisposeWith(disposables);
            _adbService.WhenDeviceStateChanged.Subscribe(OnDeviceStateChanged).DisposeWith(disposables);
            _adbService.WhenPackageListChanged.Subscribe(_ => Refresh.Execute().Subscribe()).DisposeWith(disposables);
            IsDeviceConnected = _adbService.CheckDeviceConnectionSimple();
            Refresh.Execute().Subscribe();
        });
    }

    public ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> ManualRefresh { get; }
    public ReactiveCommand<Unit, Unit> Update { get; }
    public ReactiveCommand<Unit, Unit> UpdateAll { get; }
    public ReactiveCommand<Unit, Unit> Uninstall { get; }
    public ReadOnlyObservableCollection<InstalledGame> InstalledGames => _installedGames;
    public bool IsBusy => _isBusy.Value;
    [Reactive] public bool IsDeviceConnected { get; private set; }
    [Reactive] public bool MultiSelectEnabled { get; set; } = true;
    public ViewModelActivator Activator { get; }

    private IObservable<Unit> RefreshImpl(bool rescanGames = false)
    {
        return Observable.Start(() =>
        {
            // Check whether refresh is already running
            if (RefreshSemaphoreSlim.CurrentCount == 0) return;
            RefreshSemaphoreSlim.Wait();
            try
            {
                RefreshInstalledGames(rescanGames);
            }
            finally
            {
                RefreshSemaphoreSlim.Release();
            }
        });
    }

    private IObservable<Unit> UpdateImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnectionSimple())
            {
                Log.Warning("InstalledGamesViewModel.UpdateImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            var selectedGames = _installedGamesSourceCache.Items.Where(game => game.IsSelected).ToList();
            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                Globals.MainWindowViewModel!.EnqueueTask(game, TaskType.DownloadAndInstall);
                Log.Information("Queued for update: {ReleaseName}", game.ReleaseName);
            }
        });
    }

    private IObservable<Unit> UpdateAllImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnectionSimple())
            {
                Log.Warning("InstalledGamesViewModel.UpdateAllImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            Log.Information("Running auto-update");
            var runningInstalls = new List<TaskView>();
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                runningInstalls = Globals.MainWindowViewModel!.GetTaskList()
                    .Where(x => x.TaskType is TaskType.DownloadAndInstall or TaskType.InstallOnly && !x.IsFinished)
                    .ToList();
            }).Wait();
            // Find package name duplicates to avoid installing the wrong release
            var ambiguousReleases = _installedGamesSourceCache.Items.GroupBy(x => x.PackageName)
                .Where(x => x.Skip(1).Any()).SelectMany(x => x).ToList();
            Log.Information("Found {AmbiguousReleasesCount} ambiguous releases, which will be ignored",
                ambiguousReleases.Count);
            var selectedGames = _installedGamesSourceCache.Items
                .Where(game => game.AvailableVersionCode > game.InstalledVersionCode).Except(ambiguousReleases)
                .ToList();
            if (selectedGames.Count == 0)
            {
                Log.Information("No games to update");
                return;
            }

            foreach (var game in selectedGames)
            {
                if (runningInstalls.Any(x => x.PackageName == game.PackageName))
                {
                    Log.Debug("Skipping {GameName} because it is already being installed", game.GameName);
                    continue;
                }

                game.IsSelected = false;
                Globals.MainWindowViewModel!.EnqueueTask(game, TaskType.DownloadAndInstall);
                Log.Information("Queued for update: {ReleaseName}", game.ReleaseName);
            }
        });
    }

    private IObservable<Unit> UninstallImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnectionSimple())
            {
                Log.Warning("InstalledGamesViewModel.UninstallImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            var selectedGames = _installedGamesSourceCache.Items.Where(game => game.IsSelected).ToList();
            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                Globals.MainWindowViewModel!.EnqueueTask(game, TaskType.BackupAndUninstall);
            }
        });
    }

    private void OnDeviceStateChanged(DeviceState state)
    {
        switch (state)
        {
            case DeviceState.Online:
                OnDeviceOnline();
                break;
            case DeviceState.Offline:
                OnDeviceOffline();
                break;
        }
    }

    private void OnDeviceOnline()
    {
        IsDeviceConnected = true;
        Refresh.Execute().Subscribe();
    }

    private void OnDeviceOffline()
    {
        IsDeviceConnected = false;
        Dispatcher.UIThread.InvokeAsync(_installedGamesSourceCache.Clear);
    }

    private void RefreshInstalledGames(bool rescanGames)
    {
        if ((rescanGames && !_adbService.CheckDeviceConnection()) ||
            !rescanGames && !_adbService.CheckDeviceConnectionSimple())
        {
            Log.Warning("InstalledGamesViewModel.RefreshInstalledGames: no device connection!");
            OnDeviceOffline();
            return;
        }

        IsDeviceConnected = true;
        if (rescanGames)
            _adbService.Device!.RefreshInstalledGames();
        while (_adbService.Device!.IsRefreshingInstalledGames)
        {
            Thread.Sleep(100);
            if (_adbService.Device is null)
                return;
        }
        _installedGamesSourceCache.Edit(innerCache =>
        {
            innerCache.AddOrUpdate(_adbService.Device!.InstalledGames);
            innerCache.Remove(_installedGamesSourceCache.Items.Except(_adbService.Device!.InstalledGames));
        });
        
    }
}