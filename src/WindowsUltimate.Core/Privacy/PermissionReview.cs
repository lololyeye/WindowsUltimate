using Microsoft.Win32;

namespace WindowsUltimate.Core.Privacy;

public sealed record PermissionStatus(string Capability, string Name, string Description, bool? GloballyAllowed, int AppsAllowed, int AppsDenied, string SettingsUri);

/// <summary>
/// Read-only review of app permissions as stored by the Capability Access Manager. Changes are made in Windows
/// Settings (deep-linked) because those pages also handle per-app consent correctly.
/// </summary>
public static class PermissionReview
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private static readonly (string Cap, string Name, string Desc, string Uri)[] Capabilities =
    [
        ("location", "Location", "Apps that can see where you are.", "ms-settings:privacy-location"),
        ("webcam", "Camera", "Apps that can use your camera.", "ms-settings:privacy-webcam"),
        ("microphone", "Microphone", "Apps that can listen through your microphone.", "ms-settings:privacy-microphone"),
        ("userNotificationListener", "Notifications", "Apps that can read your notifications.", "ms-settings:privacy-notifications"),
        ("userAccountInformation", "Account info", "Apps that can read your name and picture.", "ms-settings:privacy-accountinfo"),
        ("contacts", "Contacts", "Apps that can read your contacts.", "ms-settings:privacy-contacts"),
        ("appointments", "Calendar", "Apps that can read your calendar.", "ms-settings:privacy-calendar"),
        ("phoneCallHistory", "Call history", "Apps that can read your call history.", "ms-settings:privacy-callhistory"),
        ("email", "Email", "Apps that can read your email.", "ms-settings:privacy-email"),
        ("radios", "Radios", "Apps that can turn Bluetooth/Wi-Fi on or off.", "ms-settings:privacy-radios"),
        ("documentsLibrary", "Documents", "Apps that can access your Documents library.", "ms-settings:privacy-documents"),
        ("picturesLibrary", "Pictures", "Apps that can access your Pictures library.", "ms-settings:privacy-pictures"),
        ("videosLibrary", "Videos", "Apps that can access your Videos library.", "ms-settings:privacy-videos"),
        ("broadFileSystemAccess", "File system", "Apps that can access all your files.", "ms-settings:privacy-broadfilesystemaccess"),
        ("screenshot", "Screenshots", "Apps that can take screenshots of other windows.", "ms-settings:privacy-graphicscaptureprogrammatic"),
    ];

    public static List<PermissionStatus> Review()
    {
        var list = new List<PermissionStatus>();
        foreach (var (cap, name, desc, uri) in Capabilities)
        {
            using var user = Registry.CurrentUser.OpenSubKey($@"{Store}\{cap}");
            using var machine = Registry.LocalMachine.OpenSubKey($@"{Store}\{cap}");
            var global = (machine?.GetValue("Value") as string ?? user?.GetValue("Value") as string) switch
            {
                "Allow" => true,
                "Deny" => false,
                _ => (bool?)null,
            };
            int allowed = 0, denied = 0;
            if (user is not null) Count(user, ref allowed, ref denied);
            list.Add(new PermissionStatus(cap, name, desc, global, allowed, denied, uri));
        }
        return list;
    }

    private static void Count(RegistryKey key, ref int allowed, ref int denied)
    {
        foreach (var sub in key.GetSubKeyNames())
        {
            using var k = key.OpenSubKey(sub);
            if (k is null) continue;
            if (sub == "NonPackaged")
            {
                Count(k, ref allowed, ref denied);
                continue;
            }
            switch (k.GetValue("Value") as string)
            {
                case "Allow": allowed++; break;
                case "Deny": denied++; break;
            }
        }
    }
}
