using System;
using System.Threading;
using Microsoft.Windows.AppLifecycle;

namespace DevDesk.Services;

/// <summary>
/// One DevDesk per user session: a second double-click hands its activation to the
/// running window instead of opening another copy (roadmap F14).
/// <para>
/// <see cref="AppInstance"/> is the supported path. Registration can fail (a
/// locked-down machine, a broken package identity), and a second window is a far
/// better outcome than not starting at all, so every failure here degrades to
/// "carry on as a new instance" and is logged rather than thrown.
/// </para>
/// </summary>
public static class SingleInstance
{
    public const string InstanceKey = "DevDesk.Main";

    /// <summary>
    /// Returns true when this process is a second launch and has already handed its
    /// activation to the running instance — the caller must exit without creating a
    /// window. Returns false when this process is the first (or the only) one.
    /// </summary>
    public static bool TryRedirectToRunningInstance()
    {
        AppInstance instance;
        try
        {
            instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warning(ex, "Single-instance registration failed; continuing as a new instance");
            return false;
        }

        if (instance.IsCurrent)
        {
            return false;
        }

        try
        {
            // Main has no dispatcher or message loop yet, so the async action's
            // completion is signalled through a wait handle instead of an await.
            // Blocking is correct here: nothing exists to block, and returning
            // before the redirect finishes would drop the activation.
            using var handedOver = new ManualResetEventSlim(false);
            var redirect = instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            redirect.Completed = (_, _) => handedOver.Set();

            if (handedOver.Wait(TimeSpan.FromSeconds(4))) // block-exempt: no UI thread exists yet
            {
                LoggingService.Log.Information("Second launch handed its activation to the running window");
            }
            else
            {
                LoggingService.Log.Warning("Handing activation to the running instance timed out");
            }

            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warning(ex, "Could not hand activation to the running instance; continuing as a new instance");
            return false;
        }
    }

    /// <summary>
    /// Invokes <paramref name="onActivated"/> whenever another launch is redirected
    /// to this instance. The callback arrives on a background thread; marshal it.
    /// </summary>
    public static void WatchForActivations(Action onActivated)
    {
        try
        {
            AppInstance.GetCurrent().Activated += (_, _) => onActivated();
            LoggingService.Log.Debug("Listening for redirected activations");
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warning(ex, "Could not subscribe to redirected activations");
        }
    }
}
