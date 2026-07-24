using Android.App;
using Android.OS;
using Android.Widget;

namespace JunimoGate.App;

[Activity(Name = "org.junimogate.app.MainActivity", Label = "JunimoGate", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(new TextView(this)
        {
            Text = "JunimoGate diagnostic scaffold\n\nAndroid workload, SDK, JDK 17, and device validation are still required.",
        });
    }
}
