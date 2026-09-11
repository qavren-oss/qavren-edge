using Android.App;
using Android.Content.PM;

namespace Qavren.Edge.DeviceTests;

// Name is NOT decoration. Without it the Android SDK generates a hashed Java class name for the
// launcher activity (crc64<hash>.MainActivity), while the DeviceRunners CLI launches the installed
// app with `am start -W -n <ApplicationId>/<ApplicationId>.MainActivity` - its `--activity` default,
// which DeviceRunners.Testing.Targets exposes no MSBuild property to override. The two never match,
// adb exits non-zero, the CLI saves a logcat and reports "DeviceRunners CLI exited with code 1 and
// no TRX was written". Pinning the Java name to <ApplicationId>.MainActivity is what makes the
// default launch line resolve.
[Activity(Name = "app.qavren.edge.devicetests.MainActivity", Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
