namespace AtProto.Crypto;

/// <summary>The elliptic curves AT Protocol uses for signing keys.</summary>
public enum EcKeyType
{
    /// <summary>secp256k1 (k256) — the most common atproto signing curve; multicodec 0xe7.</summary>
    Secp256k1,

    /// <summary>NIST P-256 (secp256r1) — the alternative atproto signing curve; multicodec 0x1200.</summary>
    P256,
}
