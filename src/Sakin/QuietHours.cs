using System.Runtime.InteropServices;

namespace Sakin;

/// <summary>
/// Windows' Do not disturb, through the same settings object the Settings app uses (QuietHoursSettings).
/// Claude mode turns it on so nobody is woken by a notification sound at night, and puts back exactly
/// what you had. If you change it yourself while Claude mode is on, ساكن leaves your choice alone.
/// App-drawn popups (an app that shows its own notification windows) don't go through Windows and aren't affected.
/// </summary>
internal static class QuietHours
{
    public const string Off = "Microsoft.QuietHoursProfile.Unrestricted";
    public const string On = "Microsoft.QuietHoursProfile.PriorityOnly";
    const string AlarmsOnly = "Microsoft.QuietHoursProfile.AlarmsOnly";

    [ComImport, Guid("6bff4732-81ec-4ffb-ae67-b6c1bc29631f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IQuietHoursSettings
    {
        [PreserveSig] int get_UserSelectedProfile(out IntPtr id);
        [PreserveSig] int put_UserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string id);
    }

    /// <summary>The current profile, or null if Windows doesn't offer it.</summary>
    public static string? Get() => Sta(() =>
    {
        var q = Create();
        if (q == null || q.get_UserSelectedProfile(out IntPtr p) != 0 || p == IntPtr.Zero) return null;
        string s = Marshal.PtrToStringUni(p) ?? "";
        Marshal.FreeCoTaskMem(p);
        return s;
    });

    public static bool Set(string profile) => Sta(() => Create()?.put_UserSelectedProfile(profile) == 0);

    public static void Enter(Settings s)
    {
        string? cur = Get();
        if (cur == null) return;
        // after a crash we may already have turned it on ourselves: keep what was there before that
        if (s.ClaudeQuietSet == "") s.ClaudePrevQuiet = cur;
        if (cur is On or AlarmsOnly) return;   // already quiet: nothing to change, nothing to undo
        if (Set(On)) s.ClaudeQuietSet = On;
    }

    public static void Leave(Settings s)
    {
        if (s.ClaudeQuietSet != "" && s.ClaudePrevQuiet != "" && Get() == s.ClaudeQuietSet) Set(s.ClaudePrevQuiet);
        s.ClaudeQuietSet = "";
        s.ClaudePrevQuiet = "";
    }

    static IQuietHoursSettings? Create()
    {
        var t = Type.GetTypeFromCLSID(new Guid("f53321fa-34f8-4b7f-b9a3-361877cb94cf"), false);
        return t == null ? null : Activator.CreateInstance(t) as IQuietHoursSettings;
    }

    /// <summary>The settings object is apartment-threaded: call it from a short-lived STA thread.</summary>
    static T Sta<T>(Func<T> f)
    {
        T result = default!;
        var th = new Thread(() => { try { result = f(); } catch { } });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join(5000);
        return result;
    }
}
