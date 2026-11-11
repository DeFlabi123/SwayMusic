using System.Windows.Input;
using SwayMusic.Core.Networking;
using SwayMusic.Core.Storage;
using SwayMusic.Desktop.Authentication;

namespace SwayMusic.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IBrowserLoginService _browserLogin;
    private CancellationTokenSource? _signInRequest;
    private long _accountGeneration;
    internal long AccountOperationGeneration => _accountGeneration;
    private bool _isSigningIn, _isSignedIn;
    private string _signInProgress = "";

    public ICommand SignInCommand { get; }
    public ICommand CancelSignInCommand { get; }
    public bool IsSigningIn
    {
        get => _isSigningIn;
        private set { if (Set(ref _isSigningIn, value)) OnPropertyChanged(nameof(CanSignIn)); }
    }
    public bool IsSignedIn { get => _isSignedIn; private set => Set(ref _isSignedIn, value); }
    public bool CanSignIn => IsReady && !IsSigningIn;
    public string SignInProgress { get => _signInProgress; private set => Set(ref _signInProgress, value); }

    private void ApplyAccountSession(AccountSession? session)
    {
        _sourcePrefetch.Cancel();
        if (IsReady)
        {
            _pageRequest?.Cancel();
            Busy = false;
            _continuation = null;
            OnPropertyChanged(nameof(HasMore));
            ClearHomeCards();
            if (_pageKind is "home" or "search" or "radio" or "online-playlist")
            {
                SetPage("home", "Home", "Recently played. Select Home to load account recommendations.");
                ShowTracksFor(_library.State.History.Select(entry => entry.Track));
                RefreshHomeCards();
            }
        }
        _music.VisitorData = session?.VisitorData;
        _music.DataSyncId = session?.DataSyncId;
        _music.AuthUser = session?.AuthUser ?? 0;
        _music.SessionCookie = session?.CookieHeader;
        IsSignedIn = !string.IsNullOrWhiteSpace(session?.CookieHeader);
        AccountStatus = !IsSignedIn ? "Not signed in" : !string.IsNullOrWhiteSpace(session?.DisplayName)
            ? "Signed in as " + session.DisplayName
            : "Saved YouTube Music session";
        ResetAccountLibrary(session);
    }

    private async Task SignInWithGoogleAsync()
    {
        if (!IsReady || IsSigningIn) return;
        long generation = ++_accountGeneration;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _signInRequest = request;
        IsSigningIn = true;
        SignInProgress = "Opening Google sign-in…";
        try
        {
            var progress = new Progress<string>(message =>
            {
                if (!_disposed && _signInRequest == request && !request.IsCancellationRequested) SignInProgress = message;
            });
            var result = await _browserLogin.SignInAsync(progress, request.Token);
            request.Token.ThrowIfCancellationRequested();
            SignInProgress = "Verifying your YouTube Music account…";
            // Validate in a separate client; a failed replacement never changes the current account.
            using var validation = new YouTubeMusicClient(_http)
            {
                Country = _music.Country, Language = _music.Language,
                SessionCookie = result.CookieHeader, VisitorData = result.VisitorData,
                DataSyncId = result.DataSyncId, AuthUser = result.AuthUser
            };
            var account = await validation.AccountInfoAsync(request.Token);
            var session = new AccountSession(result.CookieHeader, result.VisitorData, result.DataSyncId,
                result.AuthUser, account.Name, account.Handle, account.ChannelId);
            await _accountOperations.WaitAsync(request.Token);
            try
            {
                request.Token.ThrowIfCancellationRequested();
                if (_disposed || generation != _accountGeneration) return;
                await _credentials.SaveAccountAsync(session, request.Token);
                ApplyAccountSession(session);
                CookieInput = "";
                Status = "Signed in. Select Home to load your account recommendations.";
            }
            finally { _accountOperations.Release(); }
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && generation == _accountGeneration) Status = "Sign-in cancelled.";
        }
        catch (Exception)
        {
            if (!_disposed && generation == _accountGeneration) Status = "Sign-in could not be completed. Try signing in again.";
            throw;
        }
        finally
        {
            if (_signInRequest == request)
            {
                _signInRequest = null;
                IsSigningIn = false;
                SignInProgress = "";
            }
            request.Dispose();
        }
    }
}
