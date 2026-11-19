using System.Security.Cryptography;
using System.Text;

namespace WyvernCenter.Core.Update;

/// <summary>
/// The maintainer's release signature. GitHub builds each release; the maintainer then signs it on their own PC with a key
/// that never goes to GitHub (tools/sign-release.ps1), and uploads WyvernCenter.exe.sig. The checksum files only catch a
/// broken download, because they come from the same place as the zip. This signature is what keeps someone who took over
/// the GitHub account from shipping an update: without the key they can't make one.
/// </summary>
public static class UpdateSignature
{
    public const string SigName = "WyvernCenter.exe.sig";

    /// <summary>ECDSA P-256 public keys (X, Y) allowed to sign releases. A new key is added here before an old one is retired.</summary>
    public static readonly IReadOnlyList<ECParameters> ReleaseKeys = new[]
    {
        Key("3ee03dec516023c8bd909d16e5c33072d9ac5ec8565d4bfe3e1eaeb8d29868a2",
            "eefb60f60aa57d55349419cf474a927ca543d3935d5390b02adba4d6c092a814"),
    };

    public static ECParameters Key(string xHex, string yHex) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = Convert.FromHexString(xHex), Y = Convert.FromHexString(yHex) },
    };

    /// <summary>
    /// What gets signed: the tag and the exe's SHA-256 together, so a signature can't be moved to another exe, and an old
    /// signed exe can't be passed off as a newer release.
    /// </summary>
    public static byte[] Message(string tag, string exeSha256) =>
        Encoding.ASCII.GetBytes($"WyvernCenter release\n{tag}\n{exeSha256.ToLowerInvariant()}\n");

    /// <summary>True when <paramref name="sigText"/> (base64 of the 64-byte r‖s signature) is valid for any of the keys.</summary>
    public static bool Verify(string tag, string exeSha256, string sigText, IEnumerable<ECParameters> keys)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String(sigText.Trim('﻿', ' ', '\r', '\n', '\t')); }
        catch (FormatException) { return false; }
        if (sig.Length != 64) return false;
        var message = Message(tag, exeSha256);
        foreach (var key in keys)
        {
            try
            {
                using var ecdsa = ECDsa.Create(key);
                if (ecdsa.VerifyData(message, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return true;
            }
            catch (CryptographicException) { }
        }
        return false;
    }
}
