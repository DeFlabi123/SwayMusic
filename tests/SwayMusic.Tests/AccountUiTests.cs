using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using SwayMusic.Core.Networking;
using SwayMusic.Core.Storage;
using SwayMusic.Desktop;
using SwayMusic.Desktop.Authentication;
using SwayMusic.Desktop.ViewModels;
using static SwayMusic.Tests.HeadlessTests;

namespace SwayMusic.Tests;

public static class AccountUiTests
{
    private static readonly BrowserLoginResult Login = new("SAPISID=ui-fixture; SID=fixture", "ui-visitor", "ui-channel", 2, "Fixture browser");
    private static readonly AccountSession Previous = new("SAPISID=previous-fixture", "previous-visitor", "previous-channel", 1, "Previous account", "@previous", "UC_previous");
    private const string Profile = """
        {"actions":[{"openPopupAction":{"popup":{"multiPageMenuRenderer":{"header":{"activeAccountHeaderRenderer":{
          "accountName":{"runs":[{"text":"UI fixture account"}]},
          "channelHandle":{"runs":[{"text":"@ui-fixture"}]},
          "channelId":"UC_ui_fixture"
        }}}}}}]}
        """;

    public static void Run(string directory)
    {
        TestSignInAndRestart(Path.Combine(directory, "success"));
        TestCancellation(Path.Combine(directory, "cancel"));
        TestSignOutDuringLogin(Path.Combine(directory, "signout"));
        TestSignOutDuringValidation(Path.Combine(directory, "validation-race"));
        TestFailedReplacement(Path.Combine(directory, "failure"));
        TestWindowClose(Path.Combine(directory, "close"));
        Console.WriteLine("PASS account UI: Google button bindings, verified sign-in/restart, cancellation, replacement preservation, sign-out races and closing");
    }

    private static void TestSignInAndRestart(string directory)
    {
        using (var view = new Scenario(directory))
        {
            Check(!view.Model.IsSignedIn && view.Model.CookieInput.Length == 0,
                "A fresh account should not require manually pasted credentials.");
            var advanced = view.Window.GetVisualDescendants().OfType<Expander>()
                .Single(control => Equals(control.Header, "Advanced: import a session"));
            Check(!advanced.IsExpanded, "Manual session import should remain an optional collapsed action.");
            var button = view.Window.FindControl<Button>("GoogleSignInButton")!;
            Check(ReferenceEquals(button.Command, view.Model.SignInCommand) && button.IsEffectivelyEnabled,
                "The primary Google button must bind to the sign-in command.");
            Check(!FindAction(view, view.Model.SignOutCommand).IsEffectivelyVisible,
                "Sign out should appear only when an account is connected.");
            Start(view);
            Check(view.Model.IsSigningIn && !button.IsEffectivelyEnabled
                && FindAction(view, view.Model.CancelSignInCommand).IsEffectivelyVisible,
                "Pending sign-in must disable duplicate starts and expose Cancel.");
            view.Model.SignInCommand.Execute(null); Flush();
            Check(view.Browser.Calls == 1, "Repeated sign-in activation must not open multiple browser sessions.");
            view.Browser.Complete(); Await(SignInTask(view));
            Check(view.Model.IsSignedIn && !view.Model.IsSigningIn
                && view.Model.AccountStatus == "Signed in as UI fixture account" && view.Model.CookieInput.Length == 0,
                "Verified browser sign-in should update the account without copying credentials into the UI.");
            Check(FindAction(view, view.Model.SignOutCommand).IsEffectivelyVisible
                && !FindAction(view, view.Model.CancelSignInCommand).IsEffectivelyVisible,
                "Account button visibility must reflect completed sign-in.");
            var saved = ReadAccount(directory);
            Check(saved is { DisplayName: "UI fixture account", Handle: "@ui-fixture", ChannelId: "UC_ui_fixture", AuthUser: 2 }
                && saved.CookieHeader == Login.CookieHeader && saved.VisitorData == Login.VisitorData && saved.DataSyncId == Login.DataSyncId,
                "The validated profile and browser session must commit together to encrypted storage.");
            Check(view.Http.Requests == 1 && view.Http.LastUri?.AbsolutePath == "/youtubei/v1/account/account_menu",
                "Completing sign-in should validate the account without loading recommendations implicitly.");
        }
        using var restarted = new Scenario(directory);
        Check(restarted.Model.IsSignedIn && restarted.Model.AccountStatus == "Signed in as UI fixture account"
            && restarted.Browser.Calls == 0 && restarted.Http.Requests == 0,
            "Restarting should restore the connected profile without browser interaction or network validation.");
    }

    private static void TestCancellation(string directory)
    {
        using var view = new Scenario(directory);
        Start(view);
        Activate(view, view.Model.CancelSignInCommand); Await(SignInTask(view));
        Check(view.Browser.LastToken.IsCancellationRequested && !view.Model.IsSigningIn && !view.Model.IsSignedIn
            && view.Model.Status == "Sign-in cancelled." && ReadAccount(directory) is null && view.Http.Requests == 0,
            "Cancel must stop pending browser sign-in without saving credentials or starting validation.");
    }

    private static void TestSignOutDuringLogin(string directory)
    {
        Seed(directory);
        using var view = new Scenario(directory, ignoreBrowserCancellation: true);
        Start(view);
        Activate(view, view.Model.SignOutCommand); Await(((AsyncCommand)view.Model.SignOutCommand).RunningTask);
        Check(view.Browser.LastToken.IsCancellationRequested && !view.Model.IsSignedIn && ReadAccount(directory) is null,
            "Sign out should cancel a pending replacement and remove the prior credential.");
        view.Browser.Complete(); Await(SignInTask(view));
        Check(!view.Model.IsSignedIn && ReadAccount(directory) is null && view.Model.Status == "Signed out." && view.Http.Requests == 0,
            "A late browser result must not resurrect an account after sign out.");
    }

    private static void TestSignOutDuringValidation(string directory)
    {
        Seed(directory);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var view = new Scenario(directory, http: new AccountHttp { PendingResponse = response.Task });
        Start(view); view.Browser.Complete(); Await(view.Http.Requested.Task);
        Activate(view, view.Model.SignOutCommand); Await(((AsyncCommand)view.Model.SignOutCommand).RunningTask);
        response.SetResult(AccountHttp.Success());
        Await(SignInTask(view));
        Check(!view.Model.IsSignedIn && ReadAccount(directory) is null && view.Model.Status == "Signed out.",
            "A late account validation response must not commit credentials after sign out.");
    }

    private static void TestFailedReplacement(string directory)
    {
        Seed(directory);
        using var view = new Scenario(directory, http: new AccountHttp { Status = HttpStatusCode.Unauthorized });
        Start(view); view.Browser.Complete();
        try { Await(SignInTask(view)); throw new InvalidOperationException("An expired browser session was accepted."); }
        catch (MusicServiceException) { Flush(); }
        Check(view.Model.IsSignedIn && view.Model.AccountStatus == "Signed in as Previous account"
            && ReadAccount(directory) == Previous && !view.Model.IsSigningIn,
            "A failed replacement must preserve the prior live account and durable credentials.");
        Check(view.Model.Status.Length > 0 && !view.Model.Status.Contains(Login.CookieHeader, StringComparison.Ordinal),
            "Validation failure should produce a status without exposing credentials.");
    }

    private static void TestWindowClose(string directory)
    {
        using var view = new Scenario(directory);
        Start(view);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.Window.Closed += (_, _) => closed.TrySetResult();
        view.Window.Close(); Await(closed.Task);
        Check(view.Browser.LastToken.IsCancellationRequested && !view.Model.IsSigningIn && !view.Window.IsVisible
            && ReadAccount(directory) is null && view.Http.Requests == 0,
            "Closing the native window must cancel browser sign-in and finish without saving a pending session.");
    }

    private static void Seed(string directory)
    { using var store = new CredentialStore(directory); Await(store.SaveAccountAsync(Previous)); }
    private static AccountSession? ReadAccount(string directory)
    {
        using var store = new CredentialStore(directory);
        var load = store.LoadAccountAsync(); Await(load); return load.Result;
    }
    private static Task SignInTask(Scenario view) => ((AsyncCommand)view.Model.SignInCommand).RunningTask;
    private static void Start(Scenario view)
    { Activate(view, view.Model.SignInCommand); Check(view.Browser.Calls == 1, "The Google button must start the browser service."); }
    private static Button FindAction(Scenario view, System.Windows.Input.ICommand command)
        => view.Window.GetVisualDescendants().OfType<Button>().First(button => ReferenceEquals(button.Command, command));
    private static void Activate(Scenario view, System.Windows.Input.ICommand command)
    {
        var button = FindAction(view, command); button.BringIntoView(); Flush();
        Check(button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.Focus(), "Account actions must accept keyboard input.");
        view.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        view.Window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None); Flush();
    }

    private sealed class Scenario : IDisposable
    {
        public ControlledBrowser Browser { get; }
        public AccountHttp Http { get; }
        public MainViewModel Model { get; }
        public MainWindow Window { get; }
        public Scenario(string directory, bool ignoreBrowserCancellation = false, AccountHttp? http = null)
        {
            Browser = new(ignoreBrowserCancellation); Http = http ?? new();
            Model = new(directory, Browser, Http); Window = new(Model, integrateSystem: false);
            Window.Show(); Await(Model.InitializeAsync());
            Model.SettingsCommand.Execute(null); Flush();
        }
        public void Dispose()
        {
            Browser.Complete();
            Await(Model.DisposeAsync().AsTask()); Window.Close(); Flush();
        }
    }

    private sealed class ControlledBrowser(bool ignoreCancellation) : IBrowserLoginService
    {
        private readonly TaskCompletionSource<BrowserLoginResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Task<BrowserLoginResult> SignInAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++; LastToken = cancellationToken; progress?.Report("Fixture browser is waiting for sign-in.");
            return ignoreCancellation ? _result.Task : _result.Task.WaitAsync(cancellationToken);
        }
        public void Complete() => _result.TrySetResult(Login);
    }

    private sealed class AccountHttp : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public Task<HttpResponseMessage>? PendingResponse { get; init; }
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests { get; private set; }
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++; LastUri = request.RequestUri; Requested.TrySetResult();
            return PendingResponse ?? Task.FromResult(Status == HttpStatusCode.OK ? Success() : new(Status) { Content = new StringContent("fixture session expired") });
        }
        public static HttpResponseMessage Success() => new(HttpStatusCode.OK) { Content = new StringContent(Profile, Encoding.UTF8, "application/json") };
    }
}
