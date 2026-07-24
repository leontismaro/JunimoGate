using System.Diagnostics.CodeAnalysis;

namespace JunimoGate.Core;

/// <summary>A validated, canonical SHA-256 digest represented by 64 lowercase hexadecimal characters.</summary>
public readonly record struct Sha256Digest
{
    public const int HexLength = 64;

    private Sha256Digest(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public bool IsValid => Value is not null;

    public static Sha256Digest Parse(string value)
    {
        if (!TryParse(value, out var digest))
        {
            throw new FormatException("A SHA-256 digest must contain exactly 64 lowercase hexadecimal characters.");
        }

        return digest;
    }

    public static bool TryParse(string? value, out Sha256Digest digest)
    {
        digest = default;
        if (value is null || value.Length != HexLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsLowerHex(character))
            {
                return false;
            }
        }

        digest = new Sha256Digest(value);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
