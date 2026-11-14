using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace Sakin;

/// <summary>
/// Two ways to start with Windows:
///  - normal: HKCU Run key (no prompt, can't touch apps running as administrator)
///  - elevated: a scheduled task with "highest privileges". Creating it asks UAC once;
///    after that ساكن starts elevated at logon, and launching the exe hands off to the
///    task, so there is never another prompt.
/// </summary>
internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string TaskName = "Sakin";

    public static bool IsAdmin
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    static string Exe => Environment.ProcessPath!;

    public static void SetRunKey(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(TaskName, $"\"{Exe}\" --tray");
            else if (k.GetValue(TaskName) != null) k.DeleteValue(TaskName);
        }
        catch { }
    }

    public static bool TaskExists()
    {
        var r = Run("schtasks.exe", $"/Query /TN {TaskName}", elevate: false, wait: true);
        return r == 0;
    }

    /// <summary>Starts the elevated task (no UAC). True if it launched.</summary>
    public static bool RunTask() => TaskExists() && Run("schtasks.exe", $"/Run /TN {TaskName}", false, true) == 0;

    /// <summary>Creates/updates the elevated task. Prompts UAC if we aren't admin. True on success.</summary>
    public static bool InstallTask(bool atLogon)
    {
        string user = WindowsIdentity.GetCurrent().Name;
        string xml = $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>ساكن — يهدّي البرامج اللي في الخلفية</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>{(atLogon ? "true" : "false")}</Enabled><UserId>{System.Security.SecurityElement.Escape(user)}</UserId><Delay>PT5S</Delay></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author"><UserId>{System.Security.SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec><Command>{System.Security.SecurityElement.Escape(Exe)}</Command><Arguments>--tray --elevated-start</Arguments></Exec>
  </Actions>
</Task>
""";
        string path = Path.Combine(Path.GetTempPath(), "sakin-task.xml");
        File.WriteAllText(path, xml, System.Text.Encoding.Unicode);
        try { return Run("schtasks.exe", $"/Create /TN {TaskName} /XML \"{path}\" /F", elevate: !IsAdmin, wait: true) == 0; }
        finally { try { File.Delete(path); } catch { } }
    }

    public static bool RemoveTask()
    {
        if (!TaskExists()) return true;
        return Run("schtasks.exe", $"/Delete /TN {TaskName} /F", elevate: !IsAdmin, wait: true) == 0;
    }

    static int Run(string file, string args, bool elevate, bool wait)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = elevate,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevate) psi.Verb = "runas";
            using var p = Process.Start(psi);
            if (p == null) return -1;
            if (!wait) return 0;
            p.WaitForExit(20_000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; } // UAC declined
    }
}
