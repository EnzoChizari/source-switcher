using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

namespace WindowsSourceSwitcher.Tray;

internal interface IStartupRegistration
{
    bool ReadEnabled();
    void SetEnabled(bool enabled);
}

// Task Scheduler is queried only when the menu opens or the user changes the option.
// Existing task elevation/permissions are retained; new tasks use the current user's
// interactive token, without a password, elevation or an additional background process.
internal sealed class WindowsStartupRegistration(string executable, string taskName = "Source Switcher") : IStartupRegistration
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    public bool ReadEnabled() => WithTask((folder, task, lease, sid) =>
    {
        if (task is null) return false;
        ValidateTask((string)task.Xml, executable, sid);
        return (bool)task.Enabled;
    });

    public void SetEnabled(bool enabled) => WithTask((folder, task, lease, sid) =>
    {
        if (task is not null)
        {
            ValidateTask((string)task.Xml, executable, sid);
            task.Enabled = enabled;
        }
        else if (enabled)
        {
            // TASK_CREATE (2), TASK_LOGON_INTERACTIVE_TOKEN (3). Creating, rather
            // than upserting, also protects a task registered concurrently by someone else.
            var created = lease.Own((object)folder.RegisterTask(taskName, CreateDefinition(executable, sid), 2, sid, null, 3, null));
            if (!(bool)((dynamic)created).Enabled) throw new InvalidOperationException("Windows did not enable the login task.");
        }
        return true;
    });

    private T WithTask<T>(Func<dynamic, dynamic?, ComLease, string, T> action)
    {
        ValidateLauncher(executable);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Windows user identity is unavailable.");
        using var lease = new ComLease();
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = lease.Own(Activator.CreateInstance(type)!);
        service.Connect();
        dynamic folder = lease.Own((object)service.GetFolder(@"\"));
        dynamic? task = null;
        try { task = lease.Own((object)folder.GetTask(taskName)); }
        catch (Exception ex) when (ex is COMException or FileNotFoundException && ex.HResult == unchecked((int)0x80070002)) { }
        return action(folder, task, lease, sid);
    }

    internal static string CreateDefinition(string executable, string sid)
    {
        ValidateLauncher(executable);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new InvalidOperationException("The startup executable must exist at its installed location.");
        XElement E(string name, object? value) => new(Ns + name, value);
        return new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", "Source Switcher: start minimized at sign-in.")),
            E("Triggers", E("LogonTrigger", new[] { E("Enabled", "true"), E("UserId", sid) })),
            E("Principals", new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"), E("ExecutionTimeLimit", "PT0S"), E("Enabled", "true") }),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"), E("Exec", new[] {
                E("Command", executable), E("Arguments", "--background"), E("WorkingDirectory", Path.GetDirectoryName(executable)) })))
            .ToString(SaveOptions.DisableFormatting);
    }

    private static void ValidateLauncher(string executable)
    {
        if (Path.GetFileName(executable) is not ("SourceSwitcher.exe" or "WindowsSourceSwitcher.Tray.exe"))
            throw new InvalidOperationException("Run SourceSwitcher.exe to configure startup.");
    }

    internal static void ValidateTask(string xml, string executable, string userSid)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var task = XDocument.Load(reader).Root ?? throw new InvalidOperationException("Invalid startup task.");
        var actions = task.Element(Ns + "Actions")?.Elements().ToArray() ?? [];
        var principal = task.Element(Ns + "Principals")?.Elements().SingleOrDefault();
        var triggers = task.Element(Ns + "Triggers")?.Elements().ToArray() ?? [];
        var command = actions.Length == 1 ? (string?)actions[0].Element(Ns + "Command") : null;
        if (actions.Length != 1 || actions[0].Name != Ns + "Exec" || command is null || !Path.IsPathFullyQualified(command)
            || !string.Equals(Path.GetFullPath(command), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
            || ((string?)actions[0].Element(Ns + "Arguments"))?.Trim() != "--background"
            || (string?)principal?.Element(Ns + "LogonType") != "InteractiveToken"
            || !SameUser((string?)principal?.Element(Ns + "UserId"), userSid)
            || triggers.Length != 1 || triggers[0].Name != Ns + "LogonTrigger"
            || !SameUser((string?)triggers[0].Element(Ns + "UserId"), userSid)
            || (string?)triggers[0].Element(Ns + "Enabled") == "false")
            throw new InvalidOperationException("The existing Source Switcher login task belongs to another installation, user or startup configuration. It was left unchanged.");
    }

    private static bool SameUser(string? account, string sid)
    {
        if (string.IsNullOrWhiteSpace(account)) return false;
        if (account.Equals(sid, StringComparison.OrdinalIgnoreCase)) return true;
        try { return ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value == sid; }
        catch (IdentityNotMappedException) { return false; }
    }

    private sealed class ComLease : IDisposable
    {
        private readonly List<object> values = [];
        public object Own(object value) { values.Add(value); return value; }
        public void Dispose()
        {
            for (var i = values.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(values[i])) Marshal.FinalReleaseComObject(values[i]);
        }
    }
}
