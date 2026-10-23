namespace Karry.Modules.Trust.Infrastructure;

/// <summary>Configuration section <c>Trust:Anchoring</c>. Anchoring is enabled when <see cref="Bucket"/> is set.</summary>
public sealed class AnchoringOptions
{
    public const string Section = "Trust:Anchoring";

    /// <summary>S3 endpoint (SeaweedFS locally); empty for AWS itself.</summary>
    public string? ServiceUrl { get; set; }

    public string? Bucket { get; set; }

    public string Region { get; set; } = "us-east-1";

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>Compliance retention of each anchor object (nobody, not even the account root, can delete it earlier).</summary>
    public int RetentionDays { get; set; } = 3650;

    /// <summary>PKCS#8 PEM of the ECDSA P-256 signing key (KMS replaces it in P1.17).</summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>File holding the signing key PEM; with <see cref="GenerateKeyIfMissing"/> a key is created there (local only).</summary>
    public string? SigningKeyFile { get; set; }

    public bool GenerateKeyIfMissing { get; set; }

    /// <summary>Create the bucket with Object Lock when it does not exist (local development; IaC creates it elsewhere).</summary>
    public bool CreateBucketIfMissing { get; set; }

    /// <summary>Additional trusted public keys (SPKI PEM), e.g. previous keys after a rotation.</summary>
    public IList<string> TrustedPublicKeys { get; } = [];
}
