namespace OtantikPos.Inventory.Application.Common;

// A photo or PDF someone uploaded, shown in the browser later.
internal static class UploadedFile
{
    // Checked, not taken on trust: it must be the picture or PDF its type says, by its first bytes.
    public static bool LooksLike(string contentType, byte[] content)
    {
        bool StartsWith(params byte[] prefix) => content.Length >= prefix.Length && content.AsSpan(0, prefix.Length).SequenceEqual(prefix);
        return contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => StartsWith(0xFF, 0xD8, 0xFF),
            "image/png" => StartsWith(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
            "image/webp" => content.Length >= 12 && StartsWith((byte)'R', (byte)'I', (byte)'F', (byte)'F')
                && content.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            "application/pdf" => StartsWith((byte)'%', (byte)'P', (byte)'D', (byte)'F'),
            _ => false,
        };
    }
}
