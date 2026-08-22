using System.IO;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

namespace AudioPilot.Services.Configuration;

/// <summary>
/// Defines the interactive, normal-priority logon task used for earlier startup.
/// </summary>
internal static class StartupTaskDefinition
{
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static string Create(string executablePath, string userSid, bool enabled = true)
    {
        XNamespace ns = TaskNamespace;
        return new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo", new XElement(ns + "Description", "Starts AudioPilot shortly after this user signs in.")),
            new XElement(ns + "Triggers", new XElement(ns + "LogonTrigger",
                new XElement(ns + "Enabled", true), new XElement(ns + "UserId", userSid), new XElement(ns + "Delay", "PT3S"))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"),
                new XElement(ns + "UserId", userSid), new XElement(ns + "LogonType", "InteractiveToken"), new XElement(ns + "RunLevel", "LeastPrivilege"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", false), new XElement(ns + "StopIfGoingOnBatteries", false),
                new XElement(ns + "StartWhenAvailable", false), new XElement(ns + "RunOnlyIfNetworkAvailable", false),
                new XElement(ns + "Enabled", enabled), new XElement(ns + "Hidden", false),
                new XElement(ns + "RunOnlyIfIdle", false), new XElement(ns + "WakeToRun", false),
                new XElement(ns + "ExecutionTimeLimit", "PT0S"), new XElement(ns + "Priority", 4)),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"), new XElement(ns + "Exec",
                new XElement(ns + "Command", executablePath), new XElement(ns + "Arguments", "-startup"),
                new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(executablePath)))))
            .ToString(SaveOptions.DisableFormatting);
    }

    internal static bool IsEnabled(string xml) => IsEnabled(Parse(xml));

    private static bool IsEnabled(XElement task) =>
        task.Element(TaskNamespace + "Settings")?.Element(TaskNamespace + "Enabled")?.Value != "false"
        && task.Element(TaskNamespace + "Triggers")?.Element(TaskNamespace + "LogonTrigger")?.Element(TaskNamespace + "Enabled")?.Value != "false";

    internal static bool Matches(string? xml, string executablePath, string userSid, bool includeDisabled)
    {
        if (xml == null)
        {
            return false;
        }

        XElement actual = Parse(xml);
        XElement expected = Parse(Create(executablePath, userSid));
        XNamespace ns = TaskNamespace;
        if (actual.Name != expected.Name || (!includeDisabled && !IsEnabled(actual)))
        {
            return false;
        }

        foreach (string section in new[] { "Triggers", "Principals", "Actions" })
        {
            XElement? actualSection = actual.Element(ns + section);
            XElement expectedSection = expected.Element(ns + section)!;
            if (actualSection == null || actualSection.Elements().Count() != 1)
            {
                return false;
            }

            XElement actualEntry = actualSection.Elements().Single();
            XElement expectedEntry = expectedSection.Elements().Single();
            if (actualEntry.Name != expectedEntry.Name)
            {
                return false;
            }

            foreach (XElement value in expectedEntry.Elements())
            {
                string name = value.Name.LocalName;
                string? actualValue = actualEntry.Element(value.Name)?.Value;
                if (name == "Enabled" && includeDisabled)
                {
                    continue;
                }
                if (name == "UserId")
                {
                    if (!IsSameUser(actualValue, userSid)) return false;
                    continue;
                }
                actualValue ??= name switch
                {
                    "Enabled" => "true",
                    "RunLevel" => "LeastPrivilege",
                    _ => null,
                };
                StringComparison comparison = value.Name.LocalName is "Command" or "WorkingDirectory"
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!string.Equals(actualValue, value.Value, comparison))
                {
                    return false;
                }
            }

            if (section == "Triggers" && actualEntry.Elements().Any(element => element.Name.LocalName is "Repetition" or "StartBoundary" or "EndBoundary"))
            {
                return false;
            }
        }

        XElement? actualSettings = actual.Element(ns + "Settings");
        return expected.Element(ns + "Settings")!.Elements()
            .Where(element => element.Name.LocalName != "Enabled")
            .All(element => (actualSettings?.Element(element.Name)?.Value ?? DefaultSettingValue(element.Name.LocalName)) == element.Value);
    }

    /// <summary>
    /// Task Scheduler omits schema defaults and resolves the logon trigger's SID to an account name on registration.
    /// Compare the effective values so a valid task does not get rewritten at every launch.
    /// </summary>
    private static string? DefaultSettingValue(string name) => name switch
    {
        "StartWhenAvailable" or "RunOnlyIfNetworkAvailable" or "Hidden" or "RunOnlyIfIdle" or "WakeToRun" => "false",
        "MultipleInstancesPolicy" => "IgnoreNew",
        _ => null,
    };

    private static bool IsSameUser(string? value, string userSid)
    {
        if (string.Equals(value, userSid, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return new NTAccount(value).Translate(typeof(SecurityIdentifier)).Value == userSid;
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
    }

    private static XElement Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 128 * 1024,
        });
        return XElement.Load(reader);
    }
}
