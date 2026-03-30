#if UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using System.IO;

public class iOSPostBuild
{
    [PostProcessBuild(999)]
    public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
    {
        if (target != BuildTarget.iOS) return;

        string plistPath = Path.Combine(pathToBuiltProject, "Info.plist");
        PlistDocument plist = new PlistDocument();
        plist.ReadFromFile(plistPath);

        // Local Network permission (required for MQTT broker access on iOS 14+)
        plist.root.SetString("NSLocalNetworkUsageDescription",
            "This app connects to an MQTT broker on your local network for real-time paddle tracking and AI gameplay.");

        // Bonjour service type for MQTT
        PlistElementArray bonjour = plist.root.CreateArray("NSBonjourServices");
        bonjour.AddString("_mqtt._tcp");

        plist.WriteToFile(plistPath);
    }
}
#endif
