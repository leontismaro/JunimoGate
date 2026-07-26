using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace JunimoGate.SmapiSmokeMod;

public sealed class ModEntry : Mod
{
    private Color color = Color.LimeGreen;
    private int clickCount;
    private bool gameLaunched;
    private bool renderedAfterLaunch;

    public override void Entry(IModHelper helper)
    {
        if (helper.ReadConfig<SmokeConfig>().ThrowInEntry)
            throw new InvalidOperationException("Requested JunimoGate Smoke Mod Entry failure.");
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.Display.Rendered += OnRendered;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        gameLaunched = true;
        Monitor.Log(
            $"JunimoGate SMAPI smoke event active. menu={Game1.activeClickableMenu?.GetType().FullName ?? "<null>"}; " +
            $"smallFont={(Game1.smallFont is null ? "null" : "ready")}; titleTexture={(Game1.titleButtonsTexture is null ? "null" : "ready")}; " +
            $"viewport={Game1.graphics.GraphicsDevice.Viewport.Width}x{Game1.graphics.GraphicsDevice.Viewport.Height}",
            LogLevel.Info);
    }

    private void OnRendered(object? sender, RenderedEventArgs e)
    {
        if (!gameLaunched)
            return;

        if (!renderedAfterLaunch)
        {
            renderedAfterLaunch = true;
            Monitor.Log("JunimoGate SMAPI rendered after GameLaunched.", LogLevel.Info);
        }

        if (Game1.fadeToBlackRect is not null)
            e.SpriteBatch.Draw(Game1.fadeToBlackRect, new Rectangle(24, 24, 720, 96), color);
        if (Game1.smallFont is not null)
            e.SpriteBatch.DrawString(Game1.smallFont, $"JunimoGate SMAPI active  clicks: {clickCount}", new Vector2(36, 42), Color.Black);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button != SButton.MouseLeft && e.Button != SButton.ControllerA) return;
        clickCount++;
        color = color == Color.LimeGreen ? Color.Orange : Color.LimeGreen;
    }

    private sealed class SmokeConfig { public bool ThrowInEntry { get; set; } }
}
