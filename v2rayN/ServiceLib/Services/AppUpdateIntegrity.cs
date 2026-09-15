namespace ServiceLib.Services;

public static class AppUpdateIntegrity
{
    public static bool Verify(string file, string? checksum)
    {
        var match = Regex.Match(checksum ?? string.Empty, @"^\s*([a-fA-F0-9]{64})(?:\s|$)");
        if (!match.Success || !File.Exists(file)) return false;
        using var stream = File.OpenRead(file);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(match.Groups[1].Value));
    }
}
