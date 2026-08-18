using System.Globalization;
using JunimoGate.Tests;
using StardewModdingAPI.AndroidHost;

internal static class AndroidCulturePolicyTests
{
    public static void AppliesInvariantDataCultureToGameThreads()
    {
        var previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var previousCurrentCulture = Thread.CurrentThread.CurrentCulture;
        var previousCurrentUiCulture = Thread.CurrentThread.CurrentUICulture;
        var indonesianCulture = CultureInfo.GetCultureInfo("id-ID");

        try
        {
            CultureInfo.DefaultThreadCurrentCulture = indonesianCulture;
            Thread.CurrentThread.CurrentCulture = indonesianCulture;
            Thread.CurrentThread.CurrentUICulture = indonesianCulture;

            AndroidCulturePolicy.ApplyInvariantDataCulture();

            TestHarness.Equal(CultureInfo.InvariantCulture, CultureInfo.DefaultThreadCurrentCulture);
            TestHarness.Equal(CultureInfo.InvariantCulture, Thread.CurrentThread.CurrentCulture);
            TestHarness.Equal(indonesianCulture, Thread.CurrentThread.CurrentUICulture);
            TestHarness.Equal(0.45d, double.Parse(".45"));

            CultureInfo? gameThreadCulture = null;
            double? parsedValue = null;
            var gameThread = new Thread(() =>
            {
                gameThreadCulture = Thread.CurrentThread.CurrentCulture;
                parsedValue = double.Parse(".45");
            });
            gameThread.Start();
            gameThread.Join();

            TestHarness.Equal(CultureInfo.InvariantCulture, gameThreadCulture);
            TestHarness.Equal(0.45d, parsedValue);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = previousDefaultCulture;
            Thread.CurrentThread.CurrentCulture = previousCurrentCulture;
            Thread.CurrentThread.CurrentUICulture = previousCurrentUiCulture;
        }
    }
}
