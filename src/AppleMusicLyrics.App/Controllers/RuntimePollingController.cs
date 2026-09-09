using System.Windows.Threading;
using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.App.Controllers;

public sealed class RuntimePollingController : IDisposable
{
    private readonly LyricsRuntimeService _runtimeService;
    private readonly Action<RuntimeSnapshot> _onSnapshot;
    private readonly Action<Exception> _onError;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _isRefreshing;
    private bool _disposed;

    public RuntimePollingController(
        LyricsRuntimeService runtimeService,
        TimeSpan interval,
        Action<RuntimeSnapshot> onSnapshot,
        Action<Exception> onError)
    {
        _runtimeService = runtimeService;
        _onSnapshot = onSnapshot;
        _onError = onError;
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += OnTick;
    }

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await RefreshOnceAsync().ConfigureAwait(true);
        if (!_disposed)
        {
            _timer.Start();
        }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        await RefreshOnceAsync().ConfigureAwait(true);
    }

    private async Task RefreshOnceAsync()
    {
        if (_isRefreshing || _disposed)
        {
            return;
        }

        var lifetimeToken = _lifetimeCancellation.Token;
        _isRefreshing = true;
        try
        {
            var snapshot = await _runtimeService
                .SnapshotAsync(lifetimeToken)
                .ConfigureAwait(true);
            if (!_disposed)
            {
                _onSnapshot(snapshot);
            }
        }
        catch (OperationCanceledException) when (_disposed || lifetimeToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _onError(ex);
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }
}
