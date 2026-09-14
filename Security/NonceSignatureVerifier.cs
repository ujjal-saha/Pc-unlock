using System.Security.Cryptography;

namespace PhoneUnlockService.Security;

/// <summary>
/// Verifies that a signature over a nonce was produced by the private key
/// matching one of this PC's paired phones. This is the crux of the whole
/// trust model - get this wrong and either legitimate unlocks fail, or
/// (much worse) an unauthorized signature gets accepted.
///
/// Matches the Android side's key spec (android_agent_prompt.md):
///   - EC key pair, curve secp256r1 (== NIST P-256)
///   - SHA-256 digest
///   - Standard ECDSA signature (ASN.1 DER encoding, which is what
///     Android's java.security.Signature with "SHA256withECDSA" produces,
///     and what .NET's ECDsa.VerifyData(..., DSASignatureFormat.Rfc3279DerSequence)
///     expects).
/// </summary>
public static class NonceSignatureVerifier
{
    /// <param name="nonceUtf8">
    /// The exact bytes that were signed on the phone. This MUST be defined
    /// identically on both ends - recommend UTF-8 bytes of the nonce's
    /// string form (e.g. a base64url or hex-encoded random value), not a
    /// re-derived hash of anything else, so there's no room for the two
    /// implementations to disagree.
    /// </param>
    /// <param name="signatureDer">ASN.1 DER-encoded ECDSA signature, as sent by the phone.</param>
    /// <param name="publicKeySubjectPublicKeyInfo">
    /// The paired device's public key, stored at pairing time as a
    /// SubjectPublicKeyInfo (X.509) DER blob - this is what Android's
    /// KeyStore.getCertificate(alias).getPublicKey().getEncoded() returns
    /// for an EC key, so no reformatting should be needed on the phone side.
    /// </param>
    public static bool Verify(byte[] nonceUtf8, byte[] signatureDer, byte[] publicKeySubjectPublicKeyInfo)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeySubjectPublicKeyInfo, out _);

            return ecdsa.VerifyData(
                nonceUtf8,
                signatureDer,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            // Malformed key or signature - treat as "not verified", never throw
            // out of this method, since callers use it directly to gate an
            // unlock decision.
            return false;
        }
    }
}
