using Android.App;
using Android.OS;
using Android.Widget;

namespace JunimoGate.GameHost;

[Activity(Name = "org.junimogate.gamehost.GameHostActivity", Label = "JunimoGate Game Host", Exported = false)]
public sealed class GameHostActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(new TextView(this)
        {
            Text = "GameHost Phase 2 boundary: game loading and SMAPI startup are not implemented.",
        });
    }
}
