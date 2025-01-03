﻿using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using QSideloader.Controls;
using QSideloader.Models;
using QSideloader.Utilities;
using QSideloader.ViewModels;
using Serilog;

namespace QSideloader.Views.Pages;

// ReSharper disable once UnusedType.Global
public partial class InstalledGamesView : ReactiveUserControl<InstalledGamesViewModel>
{
    public InstalledGamesView()
    {
        InitializeComponent();
        ViewModel = new InstalledGamesViewModel();
        DataContext = ViewModel;
    }

    [SuppressMessage("ReSharper", "SuggestBaseTypeForParameter")]
    private void InstalledGamesDataGrid_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        var dataGrid = (DataGrid?) sender;
        var styledElementSource = e.Source as StyledElement;
        var parentStyledElement = styledElementSource?.Parent;
        if (dataGrid is null || styledElementSource?.TemplatedParent is CheckBox 
                             || styledElementSource?.TemplatedParent is DataGridColumnHeader 
                             || parentStyledElement?.TemplatedParent is DataGridColumnHeader)
            return;
        var selectedGame = (InstalledGame?) dataGrid.SelectedItem;
        if (selectedGame is null) return;
        // TODO: let user set action in settings?
        //Globals.MainWindowViewModel!.QueueForInstall(selectedGame);
        Globals.MainWindowViewModel!.ShowGameDetails.Execute(selectedGame).Subscribe(_ => { }, _ => { });
        e.Handled = true;
    }

    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        //Log.Debug("Key pressed: {Key}, modifiers: {Modifiers}", e.Key, e.KeyModifiers);
        var dataGrid = InstalledGamesDataGrid;
        var selectedGame = (Game?) dataGrid.SelectedItem;
        if (e.KeyModifiers == KeyModifiers.None)
        {
            switch (e.Key)
            {
                // If Enter or arrow down/up is pressed, focus the data grid
                case Key.Down or Key.Up:
                    var isDataGridFocused = dataGrid.IsFocused;
                    if (!isDataGridFocused)
                    {
                        dataGrid.Focus();
                        dataGrid.SelectedIndex = 0;
                    }

                    break;
                // If Space is pressed, toggle the selected game's selected state
                case Key.Space:
                    if (selectedGame is null) return;
                    selectedGame.IsSelected = !selectedGame.IsSelected;
                    break;
                // If F5 is pressed, refresh the list
                case Key.F5:
                    ViewModel!.Refresh.Execute(true).Subscribe(_ => { }, _ => { });
                    break;
            }
        }
        else
        {
            // LeftAlt or RightAlt - show game details for the highlighted game
            // ReSharper disable once InvertIf
            if (e is {KeyModifiers: KeyModifiers.Alt, Key: Key.LeftAlt or Key.RightAlt})
            {
                if (selectedGame is null) return;
                Globals.MainWindowViewModel!.ShowGameDetails.Execute(selectedGame)
                    .Subscribe(_ => { }, _ => { });
                e.Handled = true;
            }
        }
    }

    // ReSharper disable UnusedParameter.Local
    private void Visual_OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Subscribe to main window key down event
        if (Application.Current is null) return;
        var mainWindow = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        if (mainWindow is null) return;
        mainWindow.KeyDown += MainWindow_OnKeyDown;
    }

    private void Visual_OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Unsubscribe from main window key down event
        if (Application.Current is null) return;
        var mainWindow = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        if (mainWindow is null) return;
        mainWindow.KeyDown -= MainWindow_OnKeyDown;
    }
    // ReSharper restore UnusedParameter.Local

    private void InstalledGamesDataGrid_OnEnterKeyDown(object? sender, RoutedEventArgs e)
    {
        var dataGrid = (CustomDataGrid?) sender;
        var selectedGame = (Game?) dataGrid?.SelectedItem;
        if (selectedGame is null) return;
        Log.Debug("Enter key pressed on game {Game}", selectedGame);
        var viewModel = (InstalledGamesViewModel?) DataContext;
        if (viewModel is null) return;
        viewModel.UpdateSingle.Execute(selectedGame).Subscribe(_ => { }, _ => { });
        e.Handled = true;
    }

    private void InstalledGamesDataGrid_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var dataGrid = (DataGrid?) sender;
        if (dataGrid is null || e.InitialPressMouseButton != MouseButton.Middle) return;
        var source = e.Source as Control;
        if (source?.DataContext is not Game selectedGame) return;
        Globals.MainWindowViewModel!.ShowGameDetails.Execute(selectedGame).Subscribe(_ => { }, _ => { });
        e.Handled = true;
    }
}