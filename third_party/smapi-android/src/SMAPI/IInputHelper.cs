using StardewModdingAPI.Utilities;

namespace StardewModdingAPI;

/// <summary>Provides an API for checking and changing input state.</summary>
public interface IInputHelper : IModLinked
{
    /// <summary>Get the current cursor position.</summary>
    ICursorPosition GetCursorPosition();

    /// <summary>Get whether a button is currently pressed.</summary>
    /// <param name="button">The button.</param>
    bool IsDown(SButton button);

    /// <summary>Get whether a button is currently suppressed, so the game won't see it.</summary>
    /// <param name="button">The button.</param>
    bool IsSuppressed(SButton button);

    /// <summary>Mark a button as pressed.</summary>
    /// <param name="button">The button to press.</param>
    /// <remarks>
    ///   <para>For both mods and the base game, this is equivalent to the button being physically pressed by the player. It will be released on the next input tick by default; it can be pressed again each tick to hold it down.</para>
    ///
    ///   <para>See <see cref="Suppress"/> for the inverse.</para>
    /// </remarks>
    void Press(SButton button);

    /// <summary>Prevent the game from handling a button press. This doesn't prevent other mods from receiving the event.</summary>
    /// <param name="button">The button to suppress.</param>
    /// <remarks>
    ///   <para>If the button is being held, it'll remain suppressed until the player releases it or until <see cref="Press"/> is called for the same button.</para>
    ///
    ///   <para>See <see cref="Press"/> for the inverse.</para>
    /// </remarks>
    void Suppress(SButton button);

    /// <summary>Suppress the immediate change to a scroll wheel value.</summary>
    void SuppressScrollWheel();

    /// <summary>Suppress the keybinds which are currently down.</summary>
    /// <param name="keybindList">The keybind list whose active keybinds to suppress.</param>
    void SuppressActiveKeybinds(KeybindList keybindList);

    /// <summary>Get the state of a button.</summary>
    /// <param name="button">The button to check.</param>
    SButtonState GetState(SButton button);
}
