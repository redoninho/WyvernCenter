using System.Security;

namespace WyvernCenter.Core.Safety;

/// <summary>
/// The logon task definition. "schtasks /Create /SC ONLOGON" can't change Task Scheduler's laptop defaults, which
/// made startup unreliable: no start when logging on on battery, killed when unplugged, killed after 72 hours, and
/// below-normal CPU priority. This XML turns all of those off.
/// </summary>
public static class StartupTaskXml
{
    public static string Build(string exePath, string userName, string userSid) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts Wyvern Center in the tray when you sign in.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(userName)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userSid)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>4</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>"{SecurityElement.Escape(exePath)}"</Command>
              <Arguments>--tray</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>
    /// True when a task (as "schtasks /Query /XML" prints it) was made by an older version with the old defaults and
    /// needs rebuilding. Only tasks that start Wyvern Center's own installed exe are rebuilt.
    /// </summary>
    public static bool NeedsRepair(string queriedXml) =>
        !queriedXml.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", StringComparison.Ordinal)
        || !queriedXml.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", StringComparison.Ordinal)
        || !queriedXml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", StringComparison.Ordinal)
        || !queriedXml.Contains("<Priority>4</Priority>", StringComparison.Ordinal);
}
