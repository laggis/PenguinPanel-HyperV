using Microsoft.JSInterop;

namespace HyperVPanel.Services;

public sealed class UserSession
{
    public const string BrowserTokenKey = "hypervpanel.session";

    public PanelUser? User { get; private set; }
    public string? SessionToken { get; private set; }
    public bool IsLoggedIn => User is not null;
    public bool IsRestoring { get; private set; }
    public bool HasCheckedBrowserSession { get; private set; }

    private Task? browserRestoreTask;

    public event Action? Changed;

    public void SignIn(PanelUser user, string? sessionToken = null)
    {
        User = user;
        SessionToken = sessionToken;
        IsRestoring = false;
        HasCheckedBrowserSession = true;
        NotifyChanged();
    }

    public void UpdateUser(PanelUser user)
    {
        User = user;
        NotifyChanged();
    }

    public void SignOut()
    {
        User = null;
        SessionToken = null;
        IsRestoring = false;
        HasCheckedBrowserSession = true;
        NotifyChanged();
    }

    public Task RestoreFromBrowserAsync(PanelService panel, IJSRuntime js)
    {
        if (HasCheckedBrowserSession || IsLoggedIn)
        {
            return Task.CompletedTask;
        }

        // MainLayout and Login can both request restoration during the first
        // interactive render. Make them await the same operation instead of
        // letting the second caller return while IsRestoring is still true.
        return browserRestoreTask ??= RestoreFromBrowserCoreAsync(panel, js);
    }

    private async Task RestoreFromBrowserCoreAsync(PanelService panel, IJSRuntime js)
    {

        IsRestoring = true;
        NotifyChanged();

        try
        {
            var token = await WithTimeout(js.InvokeAsync<string?>("localStorage.getItem", BrowserTokenKey).AsTask(), TimeSpan.FromSeconds(3));
            if (!string.IsNullOrWhiteSpace(token))
            {
                var user = await WithTimeout(panel.ValidateSessionTokenAsync(token), TimeSpan.FromSeconds(5));
                if (user is not null)
                {
                    SignIn(user, token);
                    return;
                }

                await WithTimeout(js.InvokeVoidAsync("localStorage.removeItem", BrowserTokenKey).AsTask(), TimeSpan.FromSeconds(3));
            }
        }
        catch
        {
            // Never leave the UI stuck on "Restoring your session".
            // If browser storage or database validation fails, show the normal login form.
        }
        finally
        {
            if (!IsLoggedIn)
            {
                IsRestoring = false;
                HasCheckedBrowserSession = true;
                NotifyChanged();
            }
        }
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            throw new TimeoutException("Timed out while restoring browser session.");
        }

        return await task;
    }

    private static async Task WithTimeout(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            throw new TimeoutException("Timed out while restoring browser session.");
        }

        await task;
    }

    private void NotifyChanged() => Changed?.Invoke();
}
