using System.Security.AccessControl;
using System.Security.Principal;

namespace WyvernCenter;

internal static class Program
{
    // Single instance = an exclusive lock on a file in Program Files\Wyvern Center\data\lock, a folder created with an
    // Administrators+SYSTEM-only ACL. Only administrators can create or open files there, so no normal process can hold it to make the elevated
    // instance (and its thermal failsafe) exit, and there is no "run anyway" path that could start a second writer.
    // The named event below is only used to bring up the existing window.
    private const string Id = @"Global\WyvernCenter-3b9d4e71";
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);

    [STAThread]
    private static void Main(string[] args)
    {
        SecureStorage.PrepareDataDir();
        var afterUpdate = args.Contains("--after-update");
        var heldLock = AcquireInstanceLock(out var firstInstance);
        // Started by an update: the old copy is still quitting (putting fans back, finishing writes). Wait for its lock
        // rather than handing over to it.
        for (var i = 0; afterUpdate && !firstInstance && i < 120; i++)
        {
            heldLock?.Dispose();
            Thread.Sleep(500);
            heldLock = AcquireInstanceLock(out firstInstance);
        }
        using var instanceLock = heldLock;
        using var showSignal = OpenShowEvent();
        if (!firstInstance)
        {
            showSignal?.Set(); // already running: bring its window up
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) LogCrash(ex); };

        var app = new AppController(startHidden: args.Contains("--tray"));
        if (afterUpdate) app.Post(app.AnnounceUpdated);
        if (args.Contains("--open=profiles")) app.Post(() => app.ShowProfiles());
        if (args.Contains("--open=settings")) app.Post(app.ShowSettings);
        if (args.Contains("--open=overlay")) app.Post(() => app.ShowOverlay(true));
        if (args.Contains("--open=monitor")) app.Post(app.ShowMonitor);
        if (showSignal is not null)
        {
            var watcher = new Thread(() =>
            {
                while (showSignal.WaitOne()) app.Post(app.ShowMain);
            }) { IsBackground = true };
            watcher.Start();
        }

        Application.Run(app);
    }

    /// <summary>
    /// Holds the instance lock for the process lifetime. Without a usable data folder (not elevated), Wyvern Center is
    /// read-only anyway, so a plain session mutex is enough there.
    /// </summary>
    private static IDisposable? AcquireInstanceLock(out bool first)
    {
        if (SecureStorage.Ready)
        {
            try
            {
                // The lock lives in a folder created admin-only (no Users read), so no normal process can even open
                // the file to block our exclusive open.
                var dir = new DirectoryInfo(Path.Combine(SecureStorage.DataDir, "lock"));
                var security = new DirectorySecurity();
                security.SetOwner(Admins);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var sid in new[] { Admins, System })
                    security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                if (!dir.Exists) dir.Create(security); // ACL applied at creation: no window
                else dir.SetAccessControl(security);
                var lockFile = new FileStream(Path.Combine(dir.FullName, "instance.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                first = true;
                return lockFile;
            }
            catch (IOException)
            {
                first = false; // held by the running instance
                return null;
            }
        }
        var m = new Mutex(true, "WyvernCenter-3b9d4e71-readonly", out first);
        return m;
    }

    private static EventWaitHandle? OpenShowEvent()
    {
        try
        {
            var security = new EventWaitHandleSecurity();
            security.SetOwner(Admins);
            foreach (var sid in new[] { Admins, System })
                security.AddAccessRule(new EventWaitHandleAccessRule(sid, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            var e = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, Id + "-show", out var created, security);
            if (created || OwnedByAdmin(e.GetAccessControl())) return e;
            e.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null; // not elevated: can't create an admin-owned event (read-only mode still runs)
        }
    }

    private static bool OwnedByAdmin(NativeObjectSecurity s) =>
        s.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier o && (o.Equals(Admins) || o.Equals(System));

    private static void LogCrash(Exception ex)
    {
        try
        {
            if (!SecureStorage.Ready) return;
            File.AppendAllText(Path.Combine(AppSettings.Dir, "crash.log"), $"{DateTime.Now:u}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
