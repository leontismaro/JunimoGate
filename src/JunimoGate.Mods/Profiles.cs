namespace JunimoGate.Mods;

public enum ModAssemblyBindingPolicy
{
    Strict,
    FirstLoaded,
    HighestCompatible,
}

public readonly record struct ProfileId
{
    private ProfileId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ProfileId Parse(string value)
    {
        if (!TryParse(value, out var profileId))
        {
            throw new FormatException("A profile ID must match [a-z0-9][a-z0-9-]{0,63}.");
        }

        return profileId;
    }

    public static bool TryParse(string? value, out ProfileId profileId)
    {
        profileId = default;
        if (string.IsNullOrEmpty(value) || value.Length > 64 || !IsLowerAlphaNumeric(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!IsLowerAlphaNumeric(value[index]) && value[index] != '-')
            {
                return false;
            }
        }

        profileId = new ProfileId(value);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;

    private static bool IsLowerAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
