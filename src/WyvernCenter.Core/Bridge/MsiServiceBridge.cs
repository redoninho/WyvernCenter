using System.Globalization;
using System.IO.Pipes;
using System.ServiceProcess;

namespace WyvernCenter.Core.Bridge;

/// <summary>
/// EC RAM access through MSI's own "MSI Foundation Service" (MSIAPService, installed with Dragon Center), which
/// already ships MSI's signed driver. Wyvern Center installs nothing. Used only when the user switches it on. If the
/// service is stopped (or Disabled), it is started for the operation and stopped again after <see cref="IdleStop"/>
/// of inactivity, and a Disabled service is set back to Disabled: its pipe accepts any local program
/// (CVE-2026-37452/-37453), so it should run for seconds, not all the time.
/// </summary>
public sealed class MsiServiceBridge : IEcBridge, IDisposable
{
    public const string ServiceName = "MSI Foundation Service";
    // The name MSI's service was first handshaken with (before the rename); kept, since a new name is untested there.
    private const string ClientName = "M-Helper";
    public static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly Action<string>? _log;
    private readonly System.Threading.Timer _idleTimer;
    private bool _startedByUs;
    private bool _wasDisabled;
    private bool _handshaken;
    private bool _broken;

    public MsiServiceBridge(Action<string>? log = null)
    {
        _log = log;
        _idleTimer = new System.Threading.Timer(_ => StopIfWeStartedIt(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The user's "Use MSI's Dragon Center service" switch. Off = never touched.</summary>
    public bool Enabled { get; set; }

    /// <summary>MSI's service is installed (cached; no switch involved).</summary>
    public bool Installed => ServiceInstalledCached();

    /// <summary>True when switched on, the MSI service is installed and the bridge hasn't failed this session. Cheap (cached).</summary>
    public bool Available => Enabled && !_broken && DateTime.UtcNow >= _offUntilUtc && ServiceInstalledCached();

    // A service that stops answering is switched off for a while (not for the whole session) so callers don't keep
    // waiting on it; a slow first answer right after starting it doesn't count.
    private static readonly TimeSpan OffAfterTimeouts = TimeSpan.FromMinutes(5);
    private DateTime _offUntilUtc = DateTime.MinValue;
    private int _timeouts;

    /// <summary>Available and running right now, so a request won't have to start the service first.</summary>
    public bool Ready
    {
        get
        {
            if (!Available) return false;
            try
            {
                using var sc = new ServiceController(ServiceName);
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Longest wait for one request: the service has been seen to accept a connection and never answer.</summary>
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(3);
    /// <summary>The first exchange after starting the service (handshake) may be slower.</summary>
    private static readonly TimeSpan FirstIoTimeout = TimeSpan.FromSeconds(8);

    private bool _installed;
    private DateTime _installedCheckedUtc = DateTime.MinValue;
    private bool _disposed;

    private bool ServiceInstalledCached()
    {
        if (DateTime.UtcNow - _installedCheckedUtc > TimeSpan.FromMinutes(1))
        {
            _installed = ServiceInstalled();
            _installedCheckedUtc = DateTime.UtcNow;
        }
        return _installed;
    }

    /// <summary>
    /// Forget that we started the service (call after "Hand back"): from now on it belongs to MSI again and the idle
    /// timer must not stop it.
    /// </summary>
    public void ReleaseOwnership()
    {
        lock (_lock)
        {
            _startedByUs = false;
            if (!_disposed) _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public byte ReadByte(int ecOffset) => (byte)(ReadDword(ecOffset) & 0xFF);

    /// <remarks>
    /// The service writes a whole dword: the three bytes after the offset are written back as read moments earlier
    /// (e.g. a write at 0xEF also rewrites 0xF0–0xF2, where 0xF2 is the SCM shift mode). Safe while every writer of
    /// those neighbours runs on the hardware queue, as they do; keep it that way.
    /// </remarks>
    public void WriteByte(int ecOffset, byte value)
    {
        lock (_lock)
        {
            var dword = ReadDword(ecOffset);
            var merged = PipeProtocol.WithLowByte(dword, value);
            var payload = Exchange(PipeProtocol.WriteRequest(ecOffset, merged));
            if (!PipeProtocol.ParseWrite(payload))
                throw new IOException($"MSI service rejected write at EC 0x{ecOffset:X2}: '{payload}'");
        }
    }

    /// <summary>The 32-bit word at EC base + offset (the low byte is the byte at that offset).</summary>
    public uint ReadDword(int ecOffset)
    {
        lock (_lock)
        {
            var payload = Exchange(PipeProtocol.ReadRequest(ecOffset));
            return PipeProtocol.ParseRead(payload)
                ?? throw new IOException($"MSI service read failed at EC 0x{ecOffset:X2}: '{payload}'");
        }
    }

    private string Exchange(string command)
    {
        var established = _handshaken; // a timeout only counts against a service that was already answering
        try
        {
            EnsureServiceRunning();
            if (!_handshaken) Handshake();

            var date = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var request = PipeProtocol.Encrypt(PipeProtocol.Frame(command), ClientName, date);
            var response = Send(request);
            if (!PipeProtocol.IsHandshakeFrame(response))
                response = PipeProtocol.Decrypt(response, ClientName, date);
            var payload = PipeProtocol.Payload(response);
            _timeouts = 0;

            if (payload.Contains("ApplicationNotHandShake", StringComparison.OrdinalIgnoreCase))
            {
                // The service restarted and forgot us: handshake again once.
                _handshaken = false;
                Handshake();
                response = Send(request);
                if (!PipeProtocol.IsHandshakeFrame(response))
                    response = PipeProtocol.Decrypt(response, ClientName, date);
                payload = PipeProtocol.Payload(response);
            }
            return payload;
        }
        catch (Exception ex) when (ex is System.TimeoutException or System.ServiceProcess.TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            if (ex is System.ServiceProcess.TimeoutException || (ex is System.TimeoutException && established && ++_timeouts >= 2))
            {
                _offUntilUtc = DateTime.UtcNow + OffAfterTimeouts;
                _timeouts = 0;
                _log?.Invoke($"bridge: MSI service not answering; not used for {OffAfterTimeouts.TotalMinutes:F0} min");
            }
            _log?.Invoke($"bridge: {command.Split(':')[1]} failed: {ex.Message}");
            throw new IOException("MSI service bridge unavailable: " + ex.Message, ex);
        }
        finally
        {
            if (_startedByUs && !_disposed) _idleTimer.Change(IdleStop, Timeout.InfiniteTimeSpan);
        }
    }

    private void Handshake()
    {
        var version = typeof(MsiServiceBridge).Assembly.GetName().Version?.ToString() ?? "1.0";
        var payload = PipeProtocol.Payload(Send(PipeProtocol.Frame(PipeProtocol.HandshakeRequest(ClientName, version)), FirstIoTimeout));
        if (!PipeProtocol.HandshakeAccepted(payload))
        {
            _broken = true;
            throw new IOException($"MSI service refused handshake: '{payload}'");
        }
        _handshaken = true;
    }

    private static byte[] Send(byte[] message) => Send(message, IoTimeout);

    private static byte[] Send(byte[] message, TimeSpan timeout)
    {
        // Identification: the server may identify us but never impersonate our (elevated) token.
        // Overlapped I/O so a request can be cancelled: a hung service must never block the caller.
        using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
        pipe.Connect((int)timeout.TotalMilliseconds);
        PipeServerCheck.EnsureTrusted(PipeServerCheck.ServerPid(pipe.SafePipeHandle), PipeServerCheck.ServicePid(ServiceName));
        pipe.ReadMode = PipeTransmissionMode.Message;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            pipe.WriteAsync(message, 0, message.Length, cts.Token).GetAwaiter().GetResult();
            using var ms = new MemoryStream();
            var buffer = new byte[4096];
            do
            {
                var n = pipe.ReadAsync(buffer, 0, buffer.Length, cts.Token).GetAwaiter().GetResult();
                if (n == 0) break;
                ms.Write(buffer, 0, n);
            } while (!pipe.IsMessageComplete);
            return ms.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw new System.TimeoutException($"MSI service didn't answer within {timeout.TotalSeconds:F0} s");
        }
    }

    private void EnsureServiceRunning()
    {
        using var sc = new ServiceController(ServiceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running) return;

        _log?.Invoke("bridge: starting MSI Foundation Service on demand");
        if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
        {
            if (sc.Status == ServiceControllerStatus.StopPending)
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            if (sc.StartType == ServiceStartMode.Disabled)
            {
                // Disabled by the user (recommended): allowed to start for this operation only, disabled again after.
                WyvernCenter.Core.Features.WindowsServiceControl.SetStartMode(ServiceName, ServiceStartMode.Manual);
                _wasDisabled = true;
                sc.Refresh();
            }
            sc.Start();
        }
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        _startedByUs = true;
        _handshaken = false;

        // The service creates its pipe shortly after reporting Running.
        for (var i = 0; i < 20; i++)
        {
            if (File.Exists(@"\\.\pipe\" + PipeProtocol.PipeName)) return;
            Thread.Sleep(250);
        }
    }

    private void StopIfWeStartedIt()
    {
        lock (_lock)
        {
            if (!_startedByUs) return;
            try
            {
                using var sc = new ServiceController(ServiceName);
                // Automatic means MSI owns it again (e.g. after Hand back): never stop it then.
                if (sc.StartType != ServiceStartMode.Automatic && sc.Status == ServiceControllerStatus.Running)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }
                _log?.Invoke("bridge: stopped MSI Foundation Service (idle)");
            }
            catch (Exception ex)
            {
                _log?.Invoke("bridge: could not stop MSI Foundation Service: " + ex.Message);
            }
            if (_wasDisabled)
            {
                try
                {
                    WyvernCenter.Core.Features.WindowsServiceControl.SetStartMode(ServiceName, ServiceStartMode.Disabled);
                    _log?.Invoke("bridge: MSI Foundation Service set back to Disabled");
                    _wasDisabled = false;
                }
                catch (Exception ex)
                {
                    _log?.Invoke("bridge: could not set MSI Foundation Service back to Disabled: " + ex.Message);
                }
            }
            _startedByUs = false;
            _handshaken = false;
        }
    }

    private static bool ServiceInstalled()
    {
        try
        {
            return ServiceController.GetServices().Any(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _idleTimer.Dispose();
        }
        StopIfWeStartedIt();
    }
}
