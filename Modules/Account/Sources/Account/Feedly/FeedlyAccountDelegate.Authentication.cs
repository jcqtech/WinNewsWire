using System.Diagnostics;
using System.Net;
using System.Text.Json;
using WinNewsWire.Secrets;

namespace WinNewsWire.Feedly;

public sealed partial class FeedlyAccountDelegate
{
    private readonly object _reauthorizationLock = new();
    private Task<bool>? _reauthorizationTask;
    private bool _reauthorizationFailedDefinitively;
    private Credentials? _refreshToken;
    private int _credentialsVersion;
    private readonly CancellationTokenSource _authenticationLifetime = new();
    private bool _disposed;
    private Task? _authenticationShutdown;

    public bool CanDisposeSynchronously
    {
        get
        {
            lock (_reauthorizationLock)
                return _authenticationShutdown?.IsCompleted ?? (_reauthorizationTask is null || _reauthorizationTask.IsCompleted);
        }
    }

    public void CredentialsChanged(Credentials credentials)
    {
        if (credentials.Type == CredentialsType.OAuthRefreshToken) RefreshToken = credentials;
        else if (credentials.Type == CredentialsType.OAuthAccessToken) AccessToken = credentials;
    }

    internal Task<bool> ReauthorizeAsync(CancellationToken ct = default)
    {
        lock (_reauthorizationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_reauthorizationFailedDefinitively) return Task.FromResult(false);
            if (_reauthorizationTask is null || _reauthorizationTask.IsCompleted)
                _reauthorizationTask = ReauthorizeWithLifetimeAsync(ct);
            return _reauthorizationTask.WaitAsync(ct);
        }
    }

    private async Task<bool> ReauthorizeWithLifetimeAsync(CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _authenticationLifetime.Token);
        return await PerformReauthorizationAsync(cancellation.Token);
    }

    public async Task WaitForPendingWorkAsync(CancellationToken ct = default)
    {
        Task<bool>? pending;
        lock (_reauthorizationLock) pending = _reauthorizationTask;
        if (pending is null) return;
        try { await pending.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && pending.IsCanceled) { }
    }

    public void Dispose()
    {
        Task shutdown;
        lock (_reauthorizationLock)
        {
            if (!CanDisposeSynchronously)
                throw new InvalidOperationException("Feedly authentication is still running. Await DisposeAsync.");
            shutdown = DisposeAsync().AsTask();
        }
        shutdown.GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task<bool>? pending;
        lock (_reauthorizationLock)
        {
            if (_authenticationShutdown is not null) return new(_authenticationShutdown);
            _disposed = true;
            _credentialsVersion++;
            pending = _reauthorizationTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _authenticationShutdown = completion.Task;
        }
        _ = StopAuthenticationAsync(pending, completion);
        return new(completion.Task);
    }

    private async Task StopAuthenticationAsync(Task<bool>? pending, TaskCompletionSource completion)
    {
        var errors = new List<Exception>();
        try { _authenticationLifetime.Cancel(); }
        catch (AggregateException error) { errors.Add(error); }
        try { if (pending is not null) await pending.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_authenticationLifetime.IsCancellationRequested) { }
        catch (Exception error) { errors.Add(error); }
        foreach (var resource in new IDisposable[] { _api, _authenticationLifetime })
        {
            try { resource.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        }
        lock (_reauthorizationLock) _refreshToken = null;
        if (errors.Count == 0) completion.TrySetResult();
        else
        {
            foreach (var error in errors) Trace.TraceError($"Could not close Feedly authentication: {error}");
            completion.TrySetException(errors);
        }
    }

    private async Task<bool> PerformReauthorizationAsync(CancellationToken ct)
    {
        int version = _credentialsVersion;
        try
        {
            if (_config is null) throw new FeedlyClientConfigMissingException();
            if (RefreshToken is not { Secret.Length: > 0 } refresh)
                throw new CredentialsException("Sign in to Feedly again; no refresh token is available.");
            var response = await FeedlyBrowserAuth.RefreshTokenAsync(_config, refresh.Secret, _authenticationHttp, ct);
            ct.ThrowIfCancellationRequested();
            lock (_reauthorizationLock)
            {
                if (_disposed) return false;
                if (_credentialsVersion != version) return AccessToken is not null;
                ApplyTokenResponse(response);
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException
            or CredentialsException or FeedlyClientConfigMissingException or OperationCanceledException)
        {
            Trace.TraceError($"Feedly access token refresh failed: {ex.Message}");
            bool definitive = ex is JsonException or InvalidDataException or CredentialsException or FeedlyClientConfigMissingException
                || ex is HttpRequestException { StatusCode: { } status } && (int)status is >= 400 and < 500;
            lock (_reauthorizationLock)
                if (definitive && _credentialsVersion == version) _reauthorizationFailedDefinitively = true;
            return false;
        }
    }
}
