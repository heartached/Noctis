using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;

namespace Noctis.ViewModels;

/// <summary>
/// Settings › Advanced › Data Folder (GitHub #165): where the library, artwork, animated
/// covers, lyrics and caches live. A move copies everything while the app runs, then a restart
/// finishes it before anything opens the data (see <see cref="DataLocation"/>).
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>The folder in use this session.</summary>
    [ObservableProperty] private string _dataFolderPath = string.Empty;
    /// <summary>The data is somewhere other than the default folder (or set to be): Use Default applies.</summary>
    [ObservableProperty] private bool _isDataFolderCustom;
    [ObservableProperty] private string _dataFolderStatus = string.Empty;
    [ObservableProperty] private bool _isDataFolderStatusError;
    [ObservableProperty] private bool _isDataFolderCopying;
    [ObservableProperty] private double _dataFolderCopyPercent;
    [ObservableProperty] private string _dataFolderCopyText = string.Empty;
    /// <summary>A change is chosen and waits for a restart.</summary>
    [ObservableProperty] private bool _isDataFolderRestartNeeded;
    [ObservableProperty] private bool _isDataFolderUseExistingVisible;
    [ObservableProperty] private string _dataFolderUseExistingDetail = string.Empty;

    public bool IsDataFolderInfoVisible => DataFolderStatus.Length > 0 && !IsDataFolderStatusError;
    public bool IsDataFolderErrorVisible => DataFolderStatus.Length > 0 && IsDataFolderStatusError;
    partial void OnDataFolderStatusChanged(string value) => NotifyDataFolderStatusVisibility();
    partial void OnIsDataFolderStatusErrorChanged(bool value) => NotifyDataFolderStatusVisibility();

    private void NotifyDataFolderStatusVisibility()
    {
        OnPropertyChanged(nameof(IsDataFolderInfoVisible));
        OnPropertyChanged(nameof(IsDataFolderErrorVisible));
    }

    /// <summary>The folder Use This Folder switches to.</summary>
    private string? _dataFolderExistingTarget;
    private CancellationTokenSource? _dataFolderCopyCts;

    /// <summary>The profile's home root and the folder in use: <see cref="AppPaths"/>; tests point
    /// them at temp folders so nothing touches a real profile.</summary>
    internal Func<string> DataFolderHomeRoot { get; set; } = () => AppPaths.HomeRoot;
    internal Func<string> DataFolderCurrentRoot { get; set; } = () => AppPaths.DataRoot;

    /// <summary>The folder picker behind <see cref="ChangeDataFolderCommand"/>. Internal for tests.</summary>
    internal Func<Task<string?>> PickDataFolder { get; set; } = PickDataFolderFromMainWindowAsync;

    private static async Task<string?> PickDataFolderFromMainWindowAsync()
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var top = owner is null ? null : Avalonia.Controls.TopLevel.GetTopLevel(owner);
        if (top is null) return null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = Loc.T("Settings.DataFolderPickTitle"),
            AllowMultiple = false
        });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    /// <summary>Reads where the data is and what the last launch did with it.</summary>
    internal void RefreshDataFolderState()
    {
        var home = DataFolderHomeRoot();
        var current = DataFolderCurrentRoot();
        var pointer = DataLocation.ReadPointer(home);
        DataFolderPath = current;
        IsDataFolderCustom = !DataLocation.SamePath(current, home) || pointer?.DataRoot is not null;

        if (IsDataFolderCopying) return;

        if (pointer?.PendingMoveTo is { } pending)
        {
            IsDataFolderRestartNeeded = true;
            SetDataFolderStatus(Loc.T("Settings.DataFolderRestartToFinish", pending), error: false);
            return;
        }

        var configured = DataLocation.ConfiguredRoot(home);
        if (DataLocation.SessionFallbackFrom is { } unreachable && DataLocation.SamePath(configured, unreachable))
        {
            IsDataFolderRestartNeeded = false;
            SetDataFolderStatus(Loc.T("Settings.DataFolderFallback", unreachable), error: true);
            return;
        }

        if (!DataLocation.SamePath(configured, current))
        {
            IsDataFolderRestartNeeded = true;
            SetDataFolderStatus(Loc.T("Settings.DataFolderRestartToSwitch", configured), error: false);
            return;
        }

        IsDataFolderRestartNeeded = false;
        var start = DataLocation.LastStartup;
        if (start?.MoveError is { } err)
            SetDataFolderStatus(Loc.T("Settings.DataFolderMoveFailed", err), error: true);
        else if (start?.MovedFrom is { } from)
            SetDataFolderStatus(Loc.T("Settings.DataFolderMoved", from), error: false);
        else
            SetDataFolderStatus("", error: false);
    }

    private void SetDataFolderStatus(string text, bool error)
    {
        DataFolderStatus = text;
        IsDataFolderStatusError = error;
    }

    [RelayCommand]
    private async Task ChangeDataFolder()
    {
        string? picked;
        try { picked = await PickDataFolder(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Data folder picker failed: {ex.Message}");
            return;
        }
        if (string.IsNullOrWhiteSpace(picked)) return;
        await ApplyDataFolderChoiceAsync(picked);
    }

    [RelayCommand]
    private Task UseDefaultDataFolder() => ApplyDataFolderChoiceAsync(DataFolderHomeRoot());

    /// <summary>Plans a change to <paramref name="picked"/>: copies, offers the data already
    /// there, or says why not. Internal for tests.</summary>
    internal async Task ApplyDataFolderChoiceAsync(string picked)
    {
        if (IsDataFolderCopying) return;
        IsDataFolderUseExistingVisible = false;
        var home = DataFolderHomeRoot();
        var current = DataFolderCurrentRoot();

        // A choice made before the restart replaces the last one.
        var pointer = DataLocation.ReadPointer(home);
        var hadPendingMove = pointer?.PendingMoveTo is not null;

        // Checking and measuring a big data folder takes a while (minutes for a large artwork
        // folder); without this the card shows nothing until the copy starts.
        SetDataFolderStatus(Loc.T("Settings.DataFolderPreparing"), error: false);

        DataLocation.MovePlan plan;
        try { plan = await Task.Run(() => DataLocation.PlanChange(current, picked, home)); }
        catch (Exception ex)
        {
            SetDataFolderStatus(Loc.T("Settings.DataFolderCopyFailed", ex.Message), error: true);
            return;
        }

        switch (plan.Kind)
        {
            case DataLocation.PlanKind.AlreadyHere:
                // Back to the folder in use: drop whatever change was waiting for a restart.
                if (hadPendingMove || !DataLocation.SamePath(DataLocation.ConfiguredRoot(home), current))
                {
                    await CancelDataFolderChangeCoreAsync(keepCurrentForGood: true);
                    return;
                }
                SetDataFolderStatus(Loc.T("Settings.DataFolderErrorSame"), error: true);
                return;
            case DataLocation.PlanKind.InsideDataFolder:
                SetDataFolderStatus(Loc.T("Settings.DataFolderErrorInside"), error: true);
                return;
            case DataLocation.PlanKind.NotEmpty:
                SetDataFolderStatus(Loc.T("Settings.DataFolderErrorNotEmpty"), error: true);
                return;
            case DataLocation.PlanKind.NotWritable:
                SetDataFolderStatus(Loc.T("Settings.DataFolderErrorNotWritable"), error: true);
                return;
            case DataLocation.PlanKind.UseExisting:
                _dataFolderExistingTarget = plan.Target;
                DataFolderUseExistingDetail = Loc.T("Settings.DataFolderExistingDetail", plan.Target, current);
                SetDataFolderStatus("", error: false);
                IsDataFolderUseExistingVisible = true;
                return;
        }

        if (hadPendingMove)
            await Task.Run(() => DataLocation.CancelPendingMove(home, current));
        await CopyDataFolderAsync(home, current, plan.Target);
    }

    private async Task CopyDataFolderAsync(string home, string current, string target)
    {
        // Room for it all, plus headroom (the library grows while it copies).
        var (needed, free) = await Task.Run(() => (DataLocation.MeasureData(current), DataLocation.FreeSpace(target)));
        if (free is { } freeBytes && freeBytes < needed + DataLocation.SpaceMargin)
        {
            SetDataFolderStatus(Loc.T("Settings.DataFolderErrorSpace", FormatBytes(needed), FormatBytes(freeBytes)), error: true);
            return;
        }

        var cts = new CancellationTokenSource();
        _dataFolderCopyCts = cts;
        IsDataFolderCopying = true;
        DataFolderCopyPercent = 0;
        DataFolderCopyText = Loc.T("Settings.DataFolderCopying", FormatBytes(0), FormatBytes(needed));
        SetDataFolderStatus("", error: false);

        // Throttled to the UI: a library of small artwork files reports thousands of times a second.
        long lastTick = 0;
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            var now = Environment.TickCount64;
            if (now - lastTick < 100 && p.Done < p.Total) return;
            lastTick = now;
            DataFolderCopyPercent = p.Total > 0 ? p.Done * 100.0 / p.Total : 100;
            DataFolderCopyText = Loc.T("Settings.DataFolderCopying", FormatBytes(p.Done), FormatBytes(p.Total));
        });

        try
        {
            await Task.Run(() => DataLocation.CopyForMove(home, current, target, progress, cts.Token));
            IsDataFolderCopying = false;
            RefreshDataFolderState();
        }
        catch (OperationCanceledException)
        {
            IsDataFolderCopying = false;
            RefreshDataFolderState();
            SetDataFolderStatus(Loc.T("Settings.DataFolderCancelled"), error: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Data folder copy failed: {ex}");
            IsDataFolderCopying = false;
            RefreshDataFolderState();
            SetDataFolderStatus(Loc.T("Settings.DataFolderCopyFailed", ex.Message), error: true);
        }
        finally
        {
            _dataFolderCopyCts = null;
            cts.Dispose();
        }
    }

    [RelayCommand]
    private void CancelDataFolderCopy() => _dataFolderCopyCts?.Cancel();

    [RelayCommand]
    private async Task ConfirmUseExistingDataFolder()
    {
        IsDataFolderUseExistingVisible = false;
        if (_dataFolderExistingTarget is not { } target) return;
        _dataFolderExistingTarget = null;
        var home = DataFolderHomeRoot();
        var current = DataFolderCurrentRoot();
        try
        {
            await Task.Run(() =>
            {
                DataLocation.CancelPendingMove(home, current);
                DataLocation.UseExisting(home, target);
            });
            RefreshDataFolderState();
        }
        catch (Exception ex)
        {
            SetDataFolderStatus(Loc.T("Settings.DataFolderCopyFailed", ex.Message), error: true);
        }
    }

    [RelayCommand]
    private void CancelUseExistingDataFolder()
    {
        IsDataFolderUseExistingVisible = false;
        _dataFolderExistingTarget = null;
    }

    /// <summary>Takes back a change that waits for a restart: the data stays where it is.</summary>
    [RelayCommand]
    private Task CancelDataFolderChange() => CancelDataFolderChangeCoreAsync(keepCurrentForGood: false);

    /// <param name="keepCurrentForGood">The user picked the folder in use: it becomes the setting
    /// even in a session that only runs on the default folder because the chosen one was unreachable.</param>
    private async Task CancelDataFolderChangeCoreAsync(bool keepCurrentForGood)
    {
        var home = DataFolderHomeRoot();
        var current = DataFolderCurrentRoot();
        try
        {
            await Task.Run(() =>
            {
                DataLocation.CancelPendingMove(home, current);
                // A session on the default folder because the chosen one was unreachable keeps
                // pointing at that one: cancelling a change is not "use the default for good".
                if (keepCurrentForGood || DataLocation.SessionFallbackFrom is null)
                    DataLocation.UseExisting(home, current);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Cancel data folder change failed: {ex.Message}");
        }
        RefreshDataFolderState();
    }

    /// <summary>Starts a new Noctis that waits for this one to exit, then quits gracefully
    /// (the shutdown save runs). The new launch finishes the move before anything opens.</summary>
    [RelayCommand]
    private void RestartForDataFolder()
    {
        if (!StartReplacementInstance())
        {
            SetDataFolderStatus(Loc.T("Settings.DataFolderRestartManually"), error: true);
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.TryShutdown();
        });
    }

    private static bool StartReplacementInstance()
    {
        try
        {
            // An AppImage runs from a mount that goes away with this process: relaunch the file.
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            var exe = !string.IsNullOrEmpty(appImage) && File.Exists(appImage) ? appImage : Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            // `dotnet Noctis.dll`: the host needs the app's path first.
            if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            psi.ArgumentList.Add(Program.WaitForPidArgument);
            psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Restart failed: {ex.Message}");
            return false;
        }
    }
}
