using System.Security.Cryptography;
using System.Text;

namespace MC1.Core.Utilities;

/// <summary>
/// ZephCore's loopback admin "V-contact": a virtual contact (chat CLI over BLE/USB) whose public key is
/// SHA-256("zc-vcontact" ‖ the radio's public key). It uses no real contact slot, so it's left out of config files
/// and never written back (port of Swift <c>VContactIdentity</c>, MeshCore One 1.7).
/// </summary>
public static class VContactIdentity
{
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("zc-vcontact");

    public static byte[]? PublicKeyFor(byte[]? selfPublicKey)
    {
        if (selfPublicKey is not { Length: 32 }) return null;
        return SHA256.HashData([.. Salt, .. selfPublicKey]);
    }

    public static bool IsVContact(byte[] publicKey, byte[]? selfPublicKey) =>
        publicKey.Length == 32 && PublicKeyFor(selfPublicKey) is { } v && v.AsSpan().SequenceEqual(publicKey);

    /// <summary>Whether a hex key is the V-contact of any of these radio keys (either hex case; bad hex is not).</summary>
    public static bool IsVContact(string publicKeyHex, IEnumerable<byte[]> selfPublicKeys)
    {
        byte[] key;
        try { key = Convert.FromHexString(publicKeyHex); } catch (FormatException) { return false; }
        return key.Length == 32 && selfPublicKeys.Any(self => IsVContact(key, self));
    }
}
