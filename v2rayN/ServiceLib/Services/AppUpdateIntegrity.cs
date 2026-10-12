namespace ServiceLib.Services;

public static class AppUpdateIntegrity
{
    public static bool Verify(string file, string? checksum)
    {
        var hash = ParseChecksum(checksum);
        if (hash == null || !File.Exists(file)) return false;
        using var stream = File.OpenRead(file);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(hash));
    }

    public static string? ParseChecksum(string? checksum)
    {
        var match = Regex.Match(checksum ?? string.Empty, @"^\s*([a-fA-F0-9]{64})(?:\s|$)");
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    public static async Task<bool> VerifyAsync(string file, string? checksum, CancellationToken cancellationToken = default)
    {
        var hash = ParseChecksum(checksum);
        if (hash == null || !File.Exists(file)) return false;
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return stream.Length > 0 && CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(hash));
    }
}
