using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace StardewModdingAPI.Framework.Input;

/// <summary>Manages controller state.</summary>
internal class GamePadStateBuilder : IInputStateBuilder<GamePadStateBuilder, GamePadState>
{
    /*********
    ** Fields
    *********/
    /// <summary>The maximum direction to ignore for the left thumbstick.</summary>
    private const float LeftThumbstickDeadZone = 0.2f;

    /// <summary>The maximum direction to ignore for the right thumbstick.</summary>
    private const float RightThumbstickDeadZone = 0.9f;

    /// <summary>The underlying controller state.</summary>
    private GamePadState? State;

    /// <summary>The current button states.</summary>
    private readonly Dictionary<SButton, ButtonState> ButtonStates = [];

    /// <summary>The left trigger value.</summary>
    private float LeftTrigger;

    /// <summary>The right trigger value.</summary>
    private float RightTrigger;

    /// <summary>The left thumbstick position.</summary>
    private Vector2 LeftStickPos;

    /// <summary>The left thumbstick position.</summary>
    private Vector2 RightStickPos;


    /*********
    ** Public methods
    *********/
    /// <inheritdoc />
    public void Reset(GamePadState state)
    {
        this.State = state;

        if (state.IsConnected)
        {
            GamePadDPad pad = state.DPad;
            GamePadButtons buttons = state.Buttons;
            GamePadTriggers triggers = state.Triggers;
            GamePadThumbSticks sticks = state.ThumbSticks;

            var states = this.ButtonStates;
            states.Clear();
            states[SButton.DPadUp] = pad.Up;
            states[SButton.DPadDown] = pad.Down;
            states[SButton.DPadLeft] = pad.Left;
            states[SButton.DPadRight] = pad.Right;
            states[SButton.ControllerA] = buttons.A;
            states[SButton.ControllerB] = buttons.B;
            states[SButton.ControllerX] = buttons.X;
            states[SButton.ControllerY] = buttons.Y;
            states[SButton.LeftStick] = buttons.LeftStick;
            states[SButton.RightStick] = buttons.RightStick;
            states[SButton.LeftShoulder] = buttons.LeftShoulder;
            states[SButton.RightShoulder] = buttons.RightShoulder;
            states[SButton.ControllerBack] = buttons.Back;
            states[SButton.ControllerStart] = buttons.Start;
            states[SButton.BigButton] = buttons.BigButton;

            this.LeftTrigger = triggers.Left;
            this.RightTrigger = triggers.Right;
            this.LeftStickPos = sticks.Left;
            this.RightStickPos = sticks.Right;
        }
        else
        {
            this.ButtonStates.Clear();

            this.LeftTrigger = 0;
            this.RightTrigger = 0;
            this.LeftStickPos = Vector2.Zero;
            this.RightStickPos = Vector2.Zero;
        }
    }

    /// <inheritdoc />
    public GamePadStateBuilder OverrideButtons(IDictionary<SButton, SButtonState> overrides)
    {
        foreach (var pair in overrides)
        {
            bool changed = true;

            bool isDown = pair.Value.IsDown();
            switch (pair.Key)
            {
                // left thumbstick
                case SButton.LeftThumbstickUp:
                    this.LeftStickPos.Y = isDown ? 1 : 0;
                    break;
                case SButton.LeftThumbstickDown:
                    this.LeftStickPos.Y = isDown ? -1 : 0;
                    break;
                case SButton.LeftThumbstickLeft:
                    this.LeftStickPos.X = isDown ? -1 : 0;
                    break;
                case SButton.LeftThumbstickRight:
                    this.LeftStickPos.X = isDown ? 1 : 0;
                    break;

                // right thumbstick
                case SButton.RightThumbstickUp:
                    this.RightStickPos.Y = isDown ? 1 : 0;
                    break;
                case SButton.RightThumbstickDown:
                    this.RightStickPos.Y = isDown ? -1 : 0;
                    break;
                case SButton.RightThumbstickLeft:
                    this.RightStickPos.X = isDown ? -1 : 0;
                    break;
                case SButton.RightThumbstickRight:
                    this.RightStickPos.X = isDown ? 1 : 0;
                    break;

                // triggers
                case SButton.LeftTrigger:
                    this.LeftTrigger = isDown ? 1 : 0;
                    break;
                case SButton.RightTrigger:
                    this.RightTrigger = isDown ? 1 : 0;
                    break;

                // buttons
                default:
                    this.ButtonStates[pair.Key] = isDown ? ButtonState.Pressed : ButtonState.Released;
                    break;
            }

            if (changed)
                this.State = null;
        }

        return this;
    }

    /// <inheritdoc />
    public IEnumerable<SButton> GetPressedButtons()
    {
        // buttons
        foreach (Buttons button in this.GetPressedGamePadButtons())
            yield return button.ToSButton();

        // triggers
        if (this.LeftTrigger > 0.2f)
            yield return SButton.LeftTrigger;
        if (this.RightTrigger > 0.2f)
            yield return SButton.RightTrigger;

        // left thumbstick direction
        if (this.LeftStickPos.Y > GamePadStateBuilder.LeftThumbstickDeadZone)
            yield return SButton.LeftThumbstickUp;
        if (this.LeftStickPos.Y < -GamePadStateBuilder.LeftThumbstickDeadZone)
            yield return SButton.LeftThumbstickDown;
        if (this.LeftStickPos.X > GamePadStateBuilder.LeftThumbstickDeadZone)
            yield return SButton.LeftThumbstickRight;
        if (this.LeftStickPos.X < -GamePadStateBuilder.LeftThumbstickDeadZone)
            yield return SButton.LeftThumbstickLeft;

        // right thumbstick direction
        if (this.RightStickPos.Length() > GamePadStateBuilder.RightThumbstickDeadZone)
        {
            if (this.RightStickPos.Y > 0)
                yield return SButton.RightThumbstickUp;
            if (this.RightStickPos.Y < 0)
                yield return SButton.RightThumbstickDown;
            if (this.RightStickPos.X > 0)
                yield return SButton.RightThumbstickRight;
            if (this.RightStickPos.X < 0)
                yield return SButton.RightThumbstickLeft;
        }
    }

    /// <inheritdoc />
    public GamePadState GetState()
    {
        this.State ??= new GamePadState(
            leftThumbStick: this.LeftStickPos,
            rightThumbStick: this.RightStickPos,
            leftTrigger: this.LeftTrigger,
            rightTrigger: this.RightTrigger,
            buttons: this.GetPressedGamePadButtons().ToArray()
        );

        return this.State.Value;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get the pressed gamepad buttons.</summary>
    private IEnumerable<Buttons> GetPressedGamePadButtons()
    {
        foreach (var pair in this.ButtonStates)
        {
            if (pair.Value == ButtonState.Pressed && pair.Key.TryGetController(out Buttons button))
                yield return button;
        }
    }
}
