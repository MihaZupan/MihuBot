using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Time.Testing;
using MihuBot.Molly;
using MihuBot.Molly.Api;
using MihuBot.Molly.Attestation;

namespace MihuBot.Tests.Molly;

public sealed class MollyAttestationTests : IClassFixture<MollyServiceFixture>
{
    private readonly MollyServiceFixture _fixture;
    private MollyService Molly => _fixture.Service;

    public MollyAttestationTests(MollyServiceFixture fixture) => _fixture = fixture;

    private Task<MollyLoginResult> LoginAsync(string keyHash, MollyTestDevice device, MollyTestProofOptions? options = null, MollyService? service = null) =>
        _fixture.LoginAsync(keyHash, service, device, options);

    private static MollyTestProofOptions WithoutEnrollment => new() { IncludeEnrollmentEvidence = false };

    private static void AssertOk(MollyLoginResult result) =>
        Assert.True(result.Status == MollyResultStatus.Ok, $"Expected ok, got {result.Status}.");

    private static void AssertRejected(MollyLoginResult result, MollyAttestationFailure failure)
    {
        Assert.Equal(MollyResultStatus.InvalidRequest, result.Status);
        Assert.Equal(failure, result.AttestationFailure);
        Assert.Null(result.ServerHmac);
        Assert.Null(result.ProtectedId);
    }

    /// <summary>An enrolled entry failing attestation for good is locked, and the device is told right away.</summary>
    private static void AssertLocked(MollyLoginResult result, MollyAttestationFailure failure)
    {
        Assert.Equal(MollyResultStatus.Command, result.Status);
        Assert.Equal(MollyCommand.Lock, result.Command);
        Assert.Equal(failure, result.AttestationFailure);
        Assert.Null(result.ServerHmac);
        Assert.Null(result.ProtectedId);
    }

    /// <summary>Lifts an automatic lock, so the next failure is evaluated rather than answered with the pending lock.</summary>
    private Task UnlockAsync(Guid id) => Molly.SetLockRequestedAsync(id, lockRequested: false);

    /// <summary>A service over the shared database, with its own clock and revocation data.</summary>
    private (MollyService Service, FakeTimeProvider Time, MollyAttestationRevocations Revocations) CreateIsolatedService(TimeSpan? revocationAge = null, MollyAttestationTrustAnchors? anchors = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        MollyAttestationRevocations revocations = MollyServiceFixture.CreateRevocations(Path.Combine(Path.GetTempPath(), $"molly-revocations-{Guid.NewGuid():N}.json"), time);
        Assert.True(revocations.Apply("""{"entries":{}}"""u8, time.GetUtcNow() - (revocationAge ?? TimeSpan.Zero)));

        var attestation = new MollyAttestationService([MollyTestPki.App], anchors ?? MollyTestPki.Instance.TrustAnchors, revocations, Microsoft.Extensions.Logging.Abstractions.NullLogger<MollyAttestationService>.Instance, time);

        return (_fixture.CreateService(MollyTestKeys.DatabaseKey, MollyTestKeys.TransportPrivateKey, attestation), time, revocations);
    }

    [Theory]
    [InlineData(MollyAttestationMode.Strict, AndroidSecurityLevel.TrustedEnvironment)]
    [InlineData(MollyAttestationMode.Strict, AndroidSecurityLevel.StrongBox)]
    [InlineData(MollyAttestationMode.Fallback, AndroidSecurityLevel.TrustedEnvironment)]
    [InlineData(MollyAttestationMode.Fallback, AndroidSecurityLevel.StrongBox)]
    public async Task Enrollment_PinsTheDevice_AndLaterLoginsReleaseTheSameSecret(MollyAttestationMode mode, AndroidSecurityLevel securityLevel)
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice(mode, securityLevel);

        MollyLoginResult enrolled = await LoginAsync(keyHash, device);
        MollyLoginResult login = await LoginAsync(keyHash, device, WithoutEnrollment);
        MollyLoginResult retried = await LoginAsync(keyHash, device);

        AssertOk(enrolled);
        AssertOk(login);
        AssertOk(retried);
        Assert.Equal(enrolled.ServerHmac, login.ServerHmac);
        Assert.Equal(enrolled.ServerHmac, retried.ServerHmac);

        await Molly.AssociateAsync(retried.ProtectedId, $"attested-{mode}", default);
        MollyUserInfo user = Assert.Single(await Molly.GetRegisteredUsersAsync(), u => u.Id == _fixture.Unprotect(retried.ProtectedId));
        Assert.Equal(mode, user.Attestation?.Mode);
    }

    [Fact]
    public async Task Login_WithoutAttestation_RegistersAndReleasesNothing()
    {
        int entries = await _fixture.CountEntriesAsync();

        MollyLoginResult result = await Molly.LoginAsync(new MollyLoginRequest { KeyHash = MollyTestKeys.NewKeyHash() }, MollyTestDevice.NewExchange(), default);

        AssertRejected(result, MollyAttestationFailure.MissingProof);
        Assert.Equal(entries, await _fixture.CountEntriesAsync());
    }

    [Fact]
    public async Task Enrollment_WithoutThePersistentSigningKeyChain_Fails()
    {
        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), new MollyTestDevice(), WithoutEnrollment), MollyAttestationFailure.MissingSigningKeyChain);
    }

    [Fact]
    public async Task Logins_OnTransportKeysOlderThanTheLimit_AreRetryable()
    {
        // The attestation challenge comes from the exchange, so the key's age bounds the attestation's.
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice();
        int entries = await _fixture.CountEntriesAsync();

        AssertRejected(await _fixture.LoginAsync(keyHash, device: device, exchange: MollyTestDevice.NewExchange(MollyAttestationService.MaxTransportKeyAge + TimeSpan.FromSeconds(1))), MollyAttestationFailure.TransportKeyTooOld);
        Assert.Equal(entries, await _fixture.CountEntriesAsync());

        AssertOk(await _fixture.LoginAsync(keyHash, device: device, exchange: MollyTestDevice.NewExchange(MollyAttestationService.MaxTransportKeyAge)));
    }

    [Fact]
    public async Task Proofs_AttestedForAnotherRequestNonce_AreRejected()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyExchangeContext exchange = MollyTestDevice.NewExchange();
        MollyAttestationProof proof = new MollyTestDevice().CreateProof(Convert.FromBase64String(keyHash), exchange);

        MollyExchangeContext sent = exchange with { RequestNonce = RandomNumberGenerator.GetBytes(MollyRequestProtector.RequestNonceLength) };

        AssertRejected(await Molly.LoginAsync(new MollyLoginRequest { KeyHash = keyHash, Attestation = proof }, sent, default), MollyAttestationFailure.ChallengeMismatch);
        AssertOk(await Molly.LoginAsync(new MollyLoginRequest { KeyHash = keyHash, Attestation = proof }, exchange, default));
    }

    [Fact]
    public async Task EnrolledDevice_ProofBoundToAnotherExchange_IsRejectedWithoutLocking()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice();
        Guid id = _fixture.Unprotect((await LoginAsync(keyHash, device)).ProtectedId);

        MollyExchangeContext exchange = MollyTestDevice.NewExchange();
        MollyAttestationProof proof = device.CreateProof(Convert.FromBase64String(keyHash), exchange, WithoutEnrollment);
        MollyExchangeContext resealed = exchange with { RequestNonce = RandomNumberGenerator.GetBytes(MollyRequestProtector.RequestNonceLength) };

        // E.g. a transport-level retry re-sealed the signed request, which says nothing about the device.
        AssertRejected(await Molly.LoginAsync(new MollyLoginRequest { KeyHash = keyHash, Attestation = proof }, resealed, default), MollyAttestationFailure.ChallengeMismatch);
        Assert.False((await _fixture.GetEntryAsync(id)).LockRequested);

        AssertOk(await Molly.LoginAsync(new MollyLoginRequest { KeyHash = keyHash, Attestation = proof }, exchange, default));
    }

    [Fact]
    public async Task Chains_RepeatingTheRoot_AreRejected()
    {
        // A repeated root would otherwise pose as the intermediate below it, which decides whether issuer expiry is enforced.
        var options = new MollyTestProofOptions { TamperFreshChain = genuine => [.. genuine, genuine[^1]] };

        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), new MollyTestDevice(MollyAttestationMode.Fallback), options), MollyAttestationFailure.InvalidChain);
    }

    public static TheoryData<string> TamperedProofs => ["keyHash", "exchange", "signer", "chain"];

    [Theory]
    [MemberData(nameof(TamperedProofs))]
    public async Task Proofs_ThatDontMatchTheRequest_AreRejected(string tampered)
    {
        var device = new MollyTestDevice(MollyAttestationMode.Fallback);
        using ECDsa otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        MollyTestProofOptions options = tampered switch
        {
            "keyHash" => new() { SignedKeyHash = RandomNumberGenerator.GetBytes(32) },
            "exchange" => new() { SignedExchange = MollyTestDevice.NewExchange() },
            "signer" => new() { SignWith = otherKey },
            _ => new() { TamperFreshChain = chain => [[.. chain[0][..^1], (byte)(chain[0][^1] ^ 1)], .. chain[1..]] },
        };

        // A proof bound to something else may just have been re-sent by a transport-level retry, so the
        // client is told to sign again. Invalid evidence is final.
        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), device, options), tampered == "chain" ? MollyAttestationFailure.InvalidChain : MollyAttestationFailure.InvalidSignature);
    }

    [Fact]
    public async Task Proofs_TamperedAfterSigning_AreRejected()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice(MollyAttestationMode.Fallback);
        MollyExchangeContext exchange = MollyTestDevice.NewExchange();
        byte[] challenge = MollyAttestationTranscript.Challenge(exchange);
        MollyAttestationProof proof = device.CreateProof(Convert.FromBase64String(keyHash), exchange);

        // A different, equally valid chain for another key with the same challenge.
        proof.FreshChain = [.. MollyTestPki.Instance.IssueManufacturerChain(ECDsa.Create(ECCurve.NamedCurves.nistP256), device.Describe(challenge)).Select(Convert.ToBase64String)];

        AssertRejected(await Molly.LoginAsync(new MollyLoginRequest { KeyHash = keyHash, Attestation = proof }, exchange, default), MollyAttestationFailure.InvalidSignature);
    }

    [Fact]
    public async Task UntrustedRoots_AreRejected()
    {
        using ECDsa otherRoot = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        (MollyService service, _, _) = CreateIsolatedService(anchors: new MollyAttestationTrustAnchors([otherRoot.ExportSubjectPublicKeyInfo()]));

        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), new MollyTestDevice(), service: service), MollyAttestationFailure.UntrustedRoot);
        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), new MollyTestDevice(MollyAttestationMode.Fallback), service: service), MollyAttestationFailure.UntrustedRoot);
    }

    public static TheoryData<string> UnacceptableFreshEvidence =>
    [
        "software", "mixedSecurityLevels", "unlocked", "unverified", "failedBoot", "zeroBootKey", "imported", "oldVersion",
        "noRootOfTrust", "noOsVersion", "wrongChallenge", "otherSecurityLevel", "otherPackage", "otherSigner", "extraSigner", "sharedUid", "noApplicationId",
    ];

    [Theory]
    [MemberData(nameof(UnacceptableFreshEvidence))]
    public async Task Enrollment_WithUnacceptableEvidence_IsRejected(string variant)
    {
        Func<MollyTestKeyDescription, MollyTestKeyDescription> modify = variant switch
        {
            "software" => d => d with { SecurityLevel = AndroidSecurityLevel.Software },
            "mixedSecurityLevels" => d => d with { KeyMintSecurityLevel = AndroidSecurityLevel.Software },
            "unlocked" => d => d with { DeviceLocked = false },
            "unverified" => d => d with { VerifiedBootState = AndroidVerifiedBootState.Unverified },
            "failedBoot" => d => d with { VerifiedBootState = AndroidVerifiedBootState.Failed },
            "zeroBootKey" => d => d with { VerifiedBootKey = new byte[32] },
            "imported" => d => d with { Origin = 2 },
            "oldVersion" => d => d with { AttestationVersion = 2 },
            "noRootOfTrust" => d => d with { IncludeRootOfTrust = false },
            "noOsVersion" => d => d with { OsPatchLevel = null },
            "wrongChallenge" => d => d with { Challenge = RandomNumberGenerator.GetBytes(32) },
            "otherSecurityLevel" => d => d with { SecurityLevel = AndroidSecurityLevel.StrongBox },
            "otherPackage" => d => d with { PackageNames = ["org.example.other"] },
            "otherSigner" => d => d with { SignatureDigests = [SHA256.HashData("other"u8)] },
            "extraSigner" => d => d with { SignatureDigests = [MollyTestPki.SignatureDigest, SHA256.HashData("other"u8)] },
            "sharedUid" => d => d with { PackageNames = [MollyTestPki.PackageName, "org.example.other"] },
            _ => d => d with { IncludeApplicationId = false },
        };

        MollyAttestationFailure expected = variant switch
        {
            "software" or "mixedSecurityLevels" => MollyAttestationFailure.NotHardwareBacked,
            "unlocked" or "unverified" or "failedBoot" or "zeroBootKey" => MollyAttestationFailure.BootloaderUnlocked,
            "imported" => MollyAttestationFailure.KeyNotGeneratedOnDevice,
            "oldVersion" => MollyAttestationFailure.AttestationVersionTooOld,
            "noRootOfTrust" => MollyAttestationFailure.MissingRootOfTrust,
            "noOsVersion" => MollyAttestationFailure.MissingOsVersion,
            "wrongChallenge" => MollyAttestationFailure.ChallengeMismatch,
            "otherSecurityLevel" => MollyAttestationFailure.SecurityLevelMismatch,
            _ => MollyAttestationFailure.UnexpectedApplication,
        };

        foreach (MollyAttestationMode mode in (MollyAttestationMode[])[MollyAttestationMode.Strict, MollyAttestationMode.Fallback])
        {
            AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), new MollyTestDevice(mode), new MollyTestProofOptions { Fresh = modify }), expected);
        }
    }

    [Fact]
    public async Task SelfSignedVerifiedBoot_IsAccepted()
    {
        // Alternative OSes like GrapheneOS boot with their own verified boot key.
        var device = new MollyTestDevice(verifiedBootState: AndroidVerifiedBootState.SelfSigned);

        AssertOk(await LoginAsync(MollyTestKeys.NewKeyHash(), device));
    }

    [Fact]
    public async Task StrictEnrollment_RequiresAnAttestOnlyKey()
    {
        var device = new MollyTestDevice();
        device.AttestKeyChain = MollyTestPki.Instance.IssueManufacturerChain(device.AttestKey!, device.Describe([1]) with { Purposes = [AndroidKeyDescription.PurposeSign, AndroidKeyDescription.PurposeAttestKey] });

        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), device), MollyAttestationFailure.AttestKeyUnexpectedProperties);
    }

    [Fact]
    public async Task StrictEnrollment_RequiresAP256AttestKey()
    {
        var device = new MollyTestDevice();
        device.AttestKeyChain = MollyTestPki.Instance.IssueManufacturerChain(device.AttestKey!, device.Describe([1]) with { Purposes = [AndroidKeyDescription.PurposeAttestKey], EcCurve = 2 });

        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), device), MollyAttestationFailure.AttestKeyUnexpectedProperties);
    }

    [Fact]
    public async Task StrictEnrollment_RequiresTheSigningKeyToBeAttestedByTheAttestKey()
    {
        var device = new MollyTestDevice();
        device.SigningKeyChain = MollyTestPki.Instance.IssueManufacturerChain(device.SigningKey, device.Describe([1]));

        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), device), MollyAttestationFailure.NotAttestedByAttestKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictLogins_RequireFreshKeysAttestedByThePinnedAttestKey(bool afterEnrollment)
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice();
        using ECDsa otherAttestKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Guid? id = afterEnrollment ? _fixture.Unprotect((await LoginAsync(keyHash, device)).ProtectedId) : null;

        // Genuine manufacturer attestation doesn't prove it came from the enrolled hardware.
        await AssertRejectedOrLockedAsync(await LoginAsync(keyHash, device, new MollyTestProofOptions { FreshFromManufacturer = true }));
        await AssertRejectedOrLockedAsync(await LoginAsync(keyHash, device, new MollyTestProofOptions { FreshIssuer = otherAttestKey }));

        async Task AssertRejectedOrLockedAsync(MollyLoginResult result)
        {
            if (id is { } enrolled)
            {
                AssertLocked(result, MollyAttestationFailure.NotAttestedByAttestKey);
                await UnlockAsync(enrolled);
            }
            else
            {
                AssertRejected(result, MollyAttestationFailure.NotAttestedByAttestKey);
            }
        }
    }

    [Fact]
    public async Task CertificatesIssuedByAnAttestedSigningKey_AreNotHardwareEvidence()
    {
        // A hardware key with the signing purpose can sign anything, including a certificate with a
        // forged attestation extension. Only the leaf of a manufacturer chain may carry one.
        var device = new MollyTestDevice(MollyAttestationMode.Fallback);

        var options = new MollyTestProofOptions
        {
            TamperFreshChain = genuine =>
            {
                using ECDsa attackerSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                using ECDsa forgedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

                // The attacker's own genuinely attested key, from an unlocked device...
                byte[][] attackerChain = MollyTestPki.Instance.IssueManufacturerChain(attackerSigningKey, device.Describe([]) with { DeviceLocked = false });

                // ...signs a certificate claiming everything the server wants to see.
                byte[] forged = MollyTestPki.IssueCertificate(forgedKey, new X500DistinguishedName("CN=Forged"), new X500DistinguishedName("CN=Android Keystore Key"), attackerSigningKey, ReadKeyDescription(genuine[0]));

                return [forged, .. attackerChain];
            },
        };

        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), device, options), MollyAttestationFailure.AttestationExtensionOutsideLeaf);
    }

    private static byte[] ReadKeyDescription(byte[] certificate)
    {
        using X509Certificate2 parsed = X509CertificateLoader.LoadCertificate(certificate);
        return parsed.Extensions[AndroidKeyDescription.ExtensionOid]!.RawData;
    }

    [Fact]
    public async Task AnotherDevice_CannotUseAnEnrolledKeyHash()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice();
        AssertOk(await LoginAsync(keyHash, device));
        Guid id = _fixture.Unprotect((await LoginAsync(keyHash, device)).ProtectedId);
        MollyDbEntry before = await _fixture.GetEntryAsync(id);

        // Same boot identity and app, but different keys: e.g. a cloned key hash used with other keys.
        AssertLocked(await LoginAsync(keyHash, new MollyTestDevice(verifiedBootKey: device.VerifiedBootKey)), MollyAttestationFailure.EnrollmentEvidenceMismatch);
        Assert.True((await _fixture.GetEntryAsync(id)).LockRequested);
        await UnlockAsync(id);
        AssertLocked(await LoginAsync(keyHash, new MollyTestDevice(verifiedBootKey: device.VerifiedBootKey), WithoutEnrollment), MollyAttestationFailure.NotAttestedByAttestKey);

        MollyDbEntry after = await _fixture.GetEntryAsync(id);
        Assert.Equal(before.EncryptedAttestationState, after.EncryptedAttestationState);
        Assert.Equal(before.LastLoginAt, after.LastLoginAt);
    }

    [Fact]
    public async Task StrictEnrollments_CannotBeDowngraded()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice();
        AssertOk(await LoginAsync(keyHash, device));

        AssertLocked(await LoginAsync(keyHash, device, new MollyTestProofOptions { ClaimedMode = MollyAttestationMode.Fallback, FreshFromManufacturer = true, IncludeEnrollmentEvidence = false }), MollyAttestationFailure.ModeMismatch);
    }

    [Fact]
    public async Task ForwardUpdates_AreAllowed_AndRaiseTheHighWaterMarks()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice();
        AssertOk(await LoginAsync(keyHash, device));

        device.AppVersion++;
        device.OsVersion += 10000;
        device.OsPatchLevel = 202611;
        device.VendorPatchLevel = 20261105;
        device.BootPatchLevel = 202611;
        AssertOk(await LoginAsync(keyHash, device));

        // Going back to the previous (still otherwise valid) versions is now a rollback.
        device.AppVersion--;
        AssertLocked(await LoginAsync(keyHash, device), MollyAttestationFailure.VersionRollback);
    }

    public static TheoryData<string> Rollbacks => ["app", "os", "osPatch", "vendorPatch", "bootPatch", "missingVendorPatch", "missingBootPatch"];

    [Theory]
    [MemberData(nameof(Rollbacks))]
    public async Task Rollbacks_AreRejected(string rollback)
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice(MollyAttestationMode.Fallback);
        AssertOk(await LoginAsync(keyHash, device));

        switch (rollback)
        {
            case "app": device.AppVersion--; break;
            case "os": device.OsVersion--; break;
            case "osPatch": device.OsPatchLevel = 202609; break;
            case "vendorPatch": device.VendorPatchLevel = 20261004; break;
            case "bootPatch": device.BootPatchLevel = 20261004; break;
            case "missingVendorPatch": device.VendorPatchLevel = null; break;
            default: device.BootPatchLevel = null; break;
        }

        AssertLocked(await LoginAsync(keyHash, device), MollyAttestationFailure.VersionRollback);
    }

    public static TheoryData<string> IdentityChanges => ["bootKey", "bootState", "securityLevel", "signer"];

    [Theory]
    [MemberData(nameof(IdentityChanges))]
    public async Task PinnedIdentityChanges_AreRejected(string change)
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice(MollyAttestationMode.Fallback);
        AssertOk(await LoginAsync(keyHash, device));

        Func<MollyTestKeyDescription, MollyTestKeyDescription> modify = change switch
        {
            "bootKey" => d => d with { VerifiedBootKey = RandomNumberGenerator.GetBytes(32) },
            "bootState" => d => d with { VerifiedBootState = AndroidVerifiedBootState.SelfSigned },
            "securityLevel" => d => d with { SecurityLevel = AndroidSecurityLevel.StrongBox },
            _ => d => d with { SignatureDigests = [SHA256.HashData("rotated"u8)] },
        };

        MollyAttestationFailure expected = change switch
        {
            "bootKey" or "bootState" => MollyAttestationFailure.VerifiedBootChanged,
            "securityLevel" => MollyAttestationFailure.SecurityLevelChanged,
            _ => MollyAttestationFailure.ApplicationChanged,
        };

        // Both signers are allowed, so only the pin can reject the change.
        MollyService service = _fixture.CreateService(MollyTestKeys.DatabaseKey, MollyTestKeys.TransportPrivateKey,
            _fixture.CreateAttestationService(apps: [MollyTestPki.App, new MollyAttestationApp(MollyTestPki.PackageName, SHA256.HashData("rotated"u8))]));

        AssertLocked(await LoginAsync(keyHash, device, new MollyTestProofOptions { Fresh = modify }, service), expected);
    }

    [Theory]
    [InlineData("org.example.other", 32, true)]
    [InlineData("org.Example_2.app", 32, true)]
    [InlineData("nodots", 32, false)]
    [InlineData("org..example", 32, false)]
    [InlineData("org.1example", 32, false)]
    [InlineData("org.exa mple", 32, false)]
    [InlineData("org.example.", 32, false)]
    [InlineData("org.example.other", 20, false)]
    public async Task WithoutAnAllowList_OnlyTheAppIdentityFormatIsChecked(string packageName, int digestLength, bool accepted)
    {
        MollyService service = _fixture.CreateService(MollyTestKeys.DatabaseKey, MollyTestKeys.TransportPrivateKey, _fixture.CreateAttestationService(apps: []));
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice(packageName: packageName, signatureDigest: RandomNumberGenerator.GetBytes(digestLength));

        MollyLoginResult result = await LoginAsync(keyHash, device, service: service);

        if (accepted)
        {
            AssertOk(result);

            // The identity is still pinned at enrollment.
            AssertLocked(await LoginAsync(keyHash, device, new MollyTestProofOptions { Fresh = d => d with { SignatureDigests = [RandomNumberGenerator.GetBytes(32)] } }, service), MollyAttestationFailure.ApplicationChanged);
        }
        else
        {
            AssertRejected(result, MollyAttestationFailure.UnexpectedApplication);
        }
    }

    [Fact]
    public async Task PatchLevels_InEitherFormat_AreComparable()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice { VendorPatchLevel = 202610 };
        AssertOk(await LoginAsync(keyHash, device));

        device.VendorPatchLevel = 20261001;
        AssertOk(await LoginAsync(keyHash, device));

        device.VendorPatchLevel = 202609;
        AssertLocked(await LoginAsync(keyHash, device), MollyAttestationFailure.VersionRollback);

        Assert.Equal(20261000, MollyAttestationVerifier.NormalizePatchLevel(202610));
        Assert.Null(MollyAttestationVerifier.NormalizePatchLevel(202613));
        Assert.Null(MollyAttestationVerifier.NormalizePatchLevel(0));
    }

    [Fact]
    public async Task LegacyEntries_EnrollOnTheirNextLogin_KeepingTheirSecret()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice();
        MollyLoginResult original = await LoginAsync(keyHash, device);
        Guid id = _fixture.Unprotect(original.ProtectedId);
        await Molly.AssociateAsync(original.ProtectedId, "legacy-user", default);
        await SetAttestationStateAsync(id, null);

        // An old client can't attest, and nothing about the entry changes.
        AssertRejected(await Molly.LoginAsync(new MollyLoginRequest { KeyHash = keyHash }, MollyTestDevice.NewExchange(), default), MollyAttestationFailure.MissingProof);
        Assert.Null((await _fixture.GetEntryAsync(id)).EncryptedAttestationState);
        Assert.Equal(MollyResultStatus.Ok, (await Molly.PingAsync(original.ProtectedId, null, null, null, default)).Status);

        MollyLoginResult enrolled = await LoginAsync(keyHash, device);

        AssertOk(enrolled);
        Assert.Equal(id, _fixture.Unprotect(enrolled.ProtectedId));
        Assert.Equal(original.ServerHmac, enrolled.ServerHmac);
        Assert.NotNull((await _fixture.GetEntryAsync(id)).EncryptedAttestationState);
        Assert.Equal(MollyAttestationMode.Strict, Assert.Single(await Molly.GetRegisteredUsersAsync(), u => u.Id == id).Attestation?.Mode);
    }

    [Fact]
    public async Task UnreadableAttestationState_DeniesRatherThanReEnrolling()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice();
        Guid id = _fixture.Unprotect((await LoginAsync(keyHash, device)).ProtectedId);

        byte[] state = (await _fixture.GetEntryAsync(id)).EncryptedAttestationState!;
        state[^1] ^= 1;
        await SetAttestationStateAsync(id, state);

        AssertLocked(await LoginAsync(keyHash, device), MollyAttestationFailure.UnreadablePinnedState);
        await UnlockAsync(id);
        AssertLocked(await LoginAsync(keyHash, new MollyTestDevice()), MollyAttestationFailure.UnreadablePinnedState);
    }

    [Fact]
    public async Task BlockingCommands_AreDeliveredWithoutAttestation()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult login = await LoginAsync(keyHash, new MollyTestDevice());
        await Molly.SetLockRequestedAsync(_fixture.Unprotect(login.ProtectedId), lockRequested: true);

        MollyLoginResult result = await Molly.LoginAsync(new MollyLoginRequest { KeyHash = keyHash }, MollyTestDevice.NewExchange(), default);

        Assert.Equal(MollyResultStatus.Command, result.Status);
        Assert.Equal(MollyCommand.Lock, result.Command);
    }

    [Fact]
    public async Task RacingFirstLogins_PinASingleDevice()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyTestDevice[] devices = [.. Enumerable.Range(0, 4).Select(_ => new MollyTestDevice())];

        MollyLoginResult[] results = await Task.WhenAll(devices.Select(device => Task.Run(() => LoginAsync(keyHash, device))));

        // Losers either lost the race to register, or verified against the winner's pins.
        MollyLoginResult winner = Assert.Single(results, r => r.Status == MollyResultStatus.Ok);
        Assert.All(results.Where(r => r != winner), r => Assert.Null(r.ServerHmac));

        // Every other device stays locked out, and its attempts lock the entry.
        MollyTestDevice pinned = devices[Array.IndexOf(results, winner)];
        Guid id = _fixture.Unprotect(winner.ProtectedId);

        foreach (MollyTestDevice loser in devices.Where(d => d != pinned))
        {
            await UnlockAsync(id);
            AssertLocked(await LoginAsync(keyHash, loser), MollyAttestationFailure.EnrollmentEvidenceMismatch);
        }

        await UnlockAsync(id);

        // The same device racing itself either logs in or is told to retry - never with a different secret.
        MollyLoginResult[] again = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => LoginAsync(keyHash, pinned))));
        Assert.Contains(again, r => r.Status == MollyResultStatus.Ok);
        Assert.All(again, r => Assert.True(r.AttestationFailure == MollyAttestationFailure.ConcurrentLogin || r.ServerHmac.AsSpan().SequenceEqual(winner.ServerHmac)));
        AssertOk(await LoginAsync(keyHash, pinned));
    }

    [Fact]
    public async Task StaleRevocationData_OnlyAllowsEnrolledStrictDevices()
    {
        string strictKeyHash = MollyTestKeys.NewKeyHash();
        string fallbackKeyHash = MollyTestKeys.NewKeyHash();
        var strict = new MollyTestDevice();
        var fallback = new MollyTestDevice(MollyAttestationMode.Fallback);
        (MollyService service, FakeTimeProvider time, _) = CreateIsolatedService();

        AssertOk(await LoginAsync(strictKeyHash, strict, service: service));
        AssertOk(await LoginAsync(fallbackKeyHash, fallback, service: service));

        time.Advance(MollyAttestationRevocations.MaxAge + TimeSpan.FromMinutes(1));

        AssertOk(await LoginAsync(strictKeyHash, strict, service: service));
        AssertRejected(await LoginAsync(fallbackKeyHash, fallback, service: service), MollyAttestationFailure.RevocationDataStale);
        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), new MollyTestDevice(), service: service), MollyAttestationFailure.RevocationDataStale);
    }

    [Fact]
    public async Task MissingRevocationData_AllowsNothing()
    {
        var revocations = MollyServiceFixture.CreateRevocations(Path.Combine(Path.GetTempPath(), $"molly-revocations-{Guid.NewGuid():N}.json"));
        MollyService service = _fixture.CreateService(MollyTestKeys.DatabaseKey, MollyTestKeys.TransportPrivateKey, _fixture.CreateAttestationService(revocations: revocations));
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice();
        Guid id = _fixture.Unprotect((await LoginAsync(keyHash, device)).ProtectedId);

        Assert.Equal(MollyRevocationStatus.Unavailable, service.AttestationRevocationStatus);
        AssertRejected(await LoginAsync(keyHash, device, service: service), MollyAttestationFailure.RevocationDataUnavailable);

        // Not the device's fault, so the entry isn't locked.
        Assert.False((await _fixture.GetEntryAsync(id)).LockRequested);
    }

    [Fact]
    public async Task RevokedCertificates_AreRejected_EvenAfterEnrollment()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice();
        (MollyService service, FakeTimeProvider time, MollyAttestationRevocations revocations) = CreateIsolatedService();
        Guid id = _fixture.Unprotect((await LoginAsync(keyHash, device, service: service)).ProtectedId);

        // Most of the live list spells serials in decimal, despite Google documenting hex.
        string revoked = System.Numerics.BigInteger.Parse("0" + MollyTestPki.RevocationSerial(device.AttestKeyChain[1]), System.Globalization.NumberStyles.HexNumber).ToString();
        Assert.True(revocations.Apply(RevocationList(revoked, "REVOKED"), time.GetUtcNow()));

        AssertLocked(await LoginAsync(keyHash, device, service: service), MollyAttestationFailure.PinnedCertificateRevoked);
        await UnlockAsync(id);

        // Google drops expired certificates from the list, but a known revocation is never forgotten.
        Assert.True(revocations.Apply("""{"entries":{}}"""u8, time.GetUtcNow()));

        AssertLocked(await LoginAsync(keyHash, device, service: service), MollyAttestationFailure.PinnedCertificateRevoked);
        await UnlockAsync(id);

        // Known revocations are enforced from stale data too.
        time.Advance(MollyAttestationRevocations.MaxAge * 2);
        AssertLocked(await LoginAsync(keyHash, device, service: service), MollyAttestationFailure.PinnedCertificateRevoked);
    }

    [Fact]
    public async Task RevokedIntermediates_BlockEnrollment()
    {
        (MollyService service, FakeTimeProvider time, MollyAttestationRevocations revocations) = CreateIsolatedService();
        string revoked = MollyTestPki.RevocationSerial(MollyTestPki.Instance.Intermediate);
        Assert.True(revocations.Apply(RevocationList(revoked, "SUSPENDED"), time.GetUtcNow()));

        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), new MollyTestDevice(), service: service), MollyAttestationFailure.CertificateRevoked);
        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), new MollyTestDevice(MollyAttestationMode.Fallback), service: service), MollyAttestationFailure.CertificateRevoked);
    }

    [Fact]
    public async Task StrictLogins_SurviveExpiryOfTheEnrollmentCertificates()
    {
        // RKP-provisioned attestation certificates are short-lived. The pairing is with the attest key,
        // so its chain expiring later doesn't matter - it only has to be valid at enrollment.
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice();
        MollyTestKeyDescription attestDescription = device.Describe([1]) with { Purposes = [AndroidKeyDescription.PurposeAttestKey] };
        device.AttestKeyChain = MollyTestPki.Instance.IssueChainThroughNewIntermediate(device.AttestKey!, attestDescription.Encode(), new X500DistinguishedName("CN=Droid CA3, O=Google LLC"), intermediateNotAfter: DateTimeOffset.UtcNow.AddHours(1));
        (MollyService service, FakeTimeProvider time, MollyAttestationRevocations revocations) = CreateIsolatedService();
        AssertOk(await LoginAsync(keyHash, device, service: service));

        time.Advance(TimeSpan.FromDays(2));
        Assert.True(revocations.Apply("""{"entries":{}}"""u8, time.GetUtcNow()));

        AssertOk(await LoginAsync(keyHash, device, service: service));

        // But enrollment itself requires valid certificates.
        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), device, service: service), MollyAttestationFailure.CertificateNotValidNow);
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)] // Older RKP chains have no provisioning info, but no serial number either.
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, false)]
    public async Task ExpiredIntermediates_AreOnlyTrustedForFactoryChainsUnderTheRsaRoot(bool factoryRoot, bool serialNumberInName, bool provisioningInfo, bool accepted)
    {
        // Google: expired factory-provisioned certificates stay trustworthy unless revoked, while RKP
        // certificates must have their validity checked. Classified like Google's own verifier.
        MollyTestPki pki = MollyTestPki.Instance;
        byte[] root = pki.RootKey.ExportSubjectPublicKeyInfo();
        (MollyService service, _, _) = CreateIsolatedService(anchors: new MollyAttestationTrustAnchors([root], factoryRoot ? [root] : []));
        var device = new MollyTestDevice(MollyAttestationMode.Fallback);
        var name = new X500DistinguishedName(serialNumberInName ? "SERIALNUMBER=c6047571d8f0d17c, CN=Expired Intermediate" : "CN=Droid CA2, O=Google LLC");
        X509Extension? extension = provisioningInfo ? new X509Extension("1.3.6.1.4.1.11129.2.1.30", [0xA0], critical: false) : null;

        var options = new MollyTestProofOptions
        {
            TamperFreshChain = genuine => pki.IssueChainThroughNewIntermediate(ECDsa.Create(ECCurve.NamedCurves.nistP256), ReadKeyDescription(genuine[0]), name, DateTimeOffset.UtcNow.AddHours(-1), extension),
        };

        MollyLoginResult result = await LoginAsync(MollyTestKeys.NewKeyHash(), device, options, service);

        if (accepted)
        {
            AssertOk(result);
        }
        else
        {
            AssertRejected(result, MollyAttestationFailure.CertificateNotValidNow);
        }
    }

    [Fact]
    public async Task FallbackLogins_WithCertificatesNotValidNow_AreRejectedWithoutLocking()
    {
        // Clock skew or a lagging RKP refresh, not tampering: the device can log in again once it's fixed.
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice(MollyAttestationMode.Fallback);
        (MollyService service, _, _) = CreateIsolatedService();
        AssertOk(await LoginAsync(keyHash, device, service: service));

        var options = new MollyTestProofOptions
        {
            TamperFreshChain = genuine => MollyTestPki.Instance.IssueChainThroughNewIntermediate(ECDsa.Create(ECCurve.NamedCurves.nistP256), ReadKeyDescription(genuine[0]), new X500DistinguishedName("CN=Droid CA2, O=Google LLC"), DateTimeOffset.UtcNow.AddHours(-1)),
        };

        AssertRejected(await LoginAsync(keyHash, device, options, service), MollyAttestationFailure.CertificateNotValidNow);
        AssertOk(await LoginAsync(keyHash, device, service: service));
    }

    [Fact]
    public async Task DeviceGeneratedCertificateValidity_IsIgnored()
    {
        // Set on the device: often clamped to an expired factory batch certificate, and subject to clock skew.
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice(MollyAttestationMode.Fallback);
        var options = new MollyTestProofOptions
        {
            TamperFreshChain = genuine =>
                [MollyTestPki.IssueCertificate(ECDsa.Create(ECCurve.NamedCurves.nistP256), MollyTestPki.LeafName, MollyTestPki.Instance.IntermediateName, MollyTestPki.Instance.IntermediateKey, ReadKeyDescription(genuine[0]), notAfter: DateTimeOffset.UtcNow.AddHours(-1)), .. genuine[1..]],
        };

        AssertOk(await LoginAsync(keyHash, device, options));
    }

    [Fact]
    public async Task ChainsWithMismatchedNames_AreRejected()
    {
        MollyTestPki pki = MollyTestPki.Instance;
        var device = new MollyTestDevice(MollyAttestationMode.Fallback);
        var options = new MollyTestProofOptions
        {
            TamperFreshChain = genuine =>
                [MollyTestPki.IssueCertificate(ECDsa.Create(ECCurve.NamedCurves.nistP256), MollyTestPki.LeafName, new X500DistinguishedName("CN=Someone Else"), pki.IntermediateKey, ReadKeyDescription(genuine[0])), .. genuine[1..]],
        };

        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), device, options), MollyAttestationFailure.InvalidChain);
    }

    [Fact]
    public async Task UnusedFieldsInAnyEncoding_AreIgnored()
    {
        var device = new MollyTestDevice(MollyAttestationMode.Fallback);

        AssertOk(await LoginAsync(MollyTestKeys.NewKeyHash(), device, new MollyTestProofOptions { Fresh = d => d with { IncludeUnusedPrimitiveField = true } }));
    }
    [Fact]
    public async Task IssuerKeysOutsideTheExpectedSizes_AreRejected()
    {
        // Bounds the work a client can cause with keys it chose. Here: a weak RSA key, properly issued.
        MollyTestPki pki = MollyTestPki.Instance;
        var device = new MollyTestDevice(MollyAttestationMode.Fallback);

        var options = new MollyTestProofOptions
        {
            TamperFreshChain = genuine =>
            {
                using RSA intermediateKey = RSA.Create(1024);
                using ECDsa leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var name = new X500DistinguishedName("CN=Weak Intermediate");

                byte[] intermediate = MollyTestPki.IssueCertificate(new CertificateRequest(name, intermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                    pki.IntermediateName, X509SignatureGenerator.CreateForECDsa(pki.IntermediateKey), null, ca: true);
                byte[] leaf = MollyTestPki.IssueCertificate(new CertificateRequest(new X500DistinguishedName("CN=Android Keystore Key"), leafKey, HashAlgorithmName.SHA256),
                    name, X509SignatureGenerator.CreateForRSA(intermediateKey, RSASignaturePadding.Pkcs1), ReadKeyDescription(genuine[0]));

                return [leaf, intermediate, pki.Intermediate, pki.Root];
            },
        };

        AssertRejected(await LoginAsync(MollyTestKeys.NewKeyHash(), device, options), MollyAttestationFailure.InvalidChain);
    }

    [Theory]
    [InlineData("rsa")]
    [InlineData("p384")]
    public void Certificates_VerifyAgainstRsaAndEcIssuers(string issuerKeyType)
    {
        using AsymmetricAlgorithm issuerKey = issuerKeyType == "rsa" ? RSA.Create(2048) : ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using ECDsa leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuerName = new X500DistinguishedName("CN=Issuer");

        CertificateRequest issuerRequest = issuerKey is RSA rsa ? new CertificateRequest(issuerName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) : new CertificateRequest(issuerName, (ECDsa)issuerKey, HashAlgorithmName.SHA384);
        X509SignatureGenerator signer = issuerKey is RSA rsaSigner ? X509SignatureGenerator.CreateForRSA(rsaSigner, RSASignaturePadding.Pkcs1) : X509SignatureGenerator.CreateForECDsa((ECDsa)issuerKey);
        byte[] issuer = MollyTestPki.IssueCertificate(issuerRequest, issuerName, signer, null, ca: true);
        byte[] leaf = MollyTestPki.IssueCertificate(new CertificateRequest(MollyTestPki.LeafName, leafKey, HashAlgorithmName.SHA256), issuerName, signer, null);

        Assert.True(MollyAttestationCertificate.TryParse(issuer, out MollyAttestationCertificate? parsedIssuer));
        Assert.True(MollyAttestationCertificate.TryParse(leaf, out MollyAttestationCertificate? parsedLeaf));
        Assert.True(parsedLeaf.IsIssuedBy(parsedIssuer));
        Assert.True(parsedLeaf.IsSignedBy(parsedIssuer.SubjectPublicKeyInfo));
        Assert.True(parsedIssuer.IsSignedBy(parsedIssuer.SubjectPublicKeyInfo));
        Assert.False(parsedLeaf.IsSignedBy(parsedLeaf.SubjectPublicKeyInfo));
    }

    [Fact]
    public void IssuerNames_MatchIgnoringStringTypeCaseAndWhitespace()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedName("CN=Droid CA2, O=Google LLC");
        byte[] issuer = MollyTestPki.IssueCertificate(key, subject, subject, key, null, ca: true);

        // UTF8String instead of PrintableString, with different case and spacing.
        var matchingName = new X500DistinguishedNameBuilder();
        matchingName.AddCommonName("droid  CA2");
        matchingName.AddOrganizationName("GOOGLE LLC");

        byte[] matching = MollyTestPki.IssueCertificate(key, MollyTestPki.LeafName, matchingName.Build(), key, null);
        byte[] reordered = MollyTestPki.IssueCertificate(key, MollyTestPki.LeafName, new X500DistinguishedName("O=Google LLC, CN=Droid CA2"), key, null);

        Assert.True(MollyAttestationCertificate.TryParse(issuer, out MollyAttestationCertificate? parsedIssuer));
        Assert.True(MollyAttestationCertificate.TryParse(matching, out MollyAttestationCertificate? parsedMatching));
        Assert.True(MollyAttestationCertificate.TryParse(reordered, out MollyAttestationCertificate? parsedReordered));
        Assert.True(parsedMatching.IsIssuedBy(parsedIssuer));
        Assert.False(parsedReordered.IsIssuedBy(parsedIssuer));
    }

    [Fact]
    public void KeysWithoutAlgorithmParameters_AreParsed()
    {
        // E.g. ML-DSA keys, which Android 17 attests with ECDSA-signed certificates.
        using ECDsa issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = new PublicKey(new Oid("2.16.840.1.101.3.4.3.18"), null, new AsnEncodedData(new byte[1952]));
        var request = new CertificateRequest(MollyTestPki.LeafName, publicKey, HashAlgorithmName.SHA256);

        byte[] leaf = MollyTestPki.IssueCertificate(request, MollyTestPki.Instance.IntermediateName, X509SignatureGenerator.CreateForECDsa(issuerKey), null);

        Assert.True(MollyAttestationCertificate.TryParse(leaf, out MollyAttestationCertificate? parsed));
        Assert.Equal(publicKey.ExportSubjectPublicKeyInfo(), parsed.SubjectPublicKeyInfo);
    }

    [Fact]
    public async Task UnsetPatchLevelsReportedAsZero_AreTreatedAsAbsent()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice(MollyAttestationMode.Fallback) { VendorPatchLevel = 0, BootPatchLevel = 0 };
        AssertOk(await LoginAsync(keyHash, device));

        device.VendorPatchLevel = 20261005;
        AssertOk(await LoginAsync(keyHash, device));

        // Once reported, going back to 0 is a rollback.
        device.VendorPatchLevel = 0;
        AssertLocked(await LoginAsync(keyHash, device), MollyAttestationFailure.VersionRollback);
    }

    [Fact]
    public async Task ResentEnrollmentChains_MustBeWellFormed()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        var device = new MollyTestDevice();
        AssertOk(await LoginAsync(keyHash, device));

        device.AttestKeyChain = [device.AttestKeyChain[0], [1, 2, 3]];

        AssertLocked(await LoginAsync(keyHash, device), MollyAttestationFailure.MalformedCertificate);
    }

    [Fact]
    public void Revocations_MatchSerialsInHexOrDecimal()
    {
        var snapshot = new MollyRevocationSnapshot(DateTimeOffset.UtcNow, ["2c8cdddfd5e03bfc", "14453862972562017658"]);

        Assert.True(snapshot.IsRevoked("2c8cdddfd5e03bfc"));
        Assert.True(snapshot.IsRevoked("c8966fcb2fbb0d7a")); // 14453862972562017658 in hex.
        Assert.False(snapshot.IsRevoked("1"));
    }

    [Fact]
    public async Task RevocationData_IsPersistedWithItsFetchTime()
    {
        string path = Path.Combine(Path.GetTempPath(), $"molly-revocations-{Guid.NewGuid():N}.json");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        try
        {
            MollyAttestationRevocations original = MollyServiceFixture.CreateRevocations(path, time);
            Assert.True(original.Apply("""{"entries":{"00ABC":{"status":"REVOKED"}}}"""u8, time.GetUtcNow() - TimeSpan.FromHours(23)));

            MollyAttestationRevocations restarted = MollyServiceFixture.CreateRevocations(path, time);
            Assert.Equal(MollyRevocationStatus.Fresh, restarted.GetStatus(out MollyRevocationSnapshot? snapshot));
            Assert.True(snapshot!.IsRevoked("abc"));

            // A restart doesn't make the data look any fresher than it is.
            time.Advance(TimeSpan.FromHours(2));
            Assert.Equal(MollyRevocationStatus.Stale, MollyServiceFixture.CreateRevocations(path, time).GetStatus(out _));

            // Failed refreshes and malformed lists keep the previous data.
            Assert.False(await restarted.RefreshAsync(default));
            Assert.False(restarted.Apply("""{"entries":[]}"""u8, time.GetUtcNow()));
            Assert.True(restarted.Snapshot!.IsRevoked("abc"));

            // A single unexpected entry is skipped rather than rejecting the whole list.
            Assert.True(restarted.Apply("""{"entries":{"not-hex":{},"def":{"status":"REVOKED"}}}"""u8, time.GetUtcNow()));
            Assert.True(restarted.Snapshot!.IsRevoked("abc"));
            Assert.True(restarted.Snapshot!.IsRevoked("def"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RevocationRefresh_DownloadsTheOfficialList()
    {
        string path = Path.Combine(Path.GetTempPath(), $"molly-revocations-{Guid.NewGuid():N}.json");

        try
        {
            using var http = new HttpClient(new StaticHandler("""{"entries":{"8350192447815228107":{"status":"REVOKED","reason":"KEY_COMPROMISE"}}}"""));
            var revocations = new MollyAttestationRevocations(path, http, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<MollyAttestationRevocations>.Instance);

            Assert.True(await revocations.RefreshAsync(default));
            Assert.Equal(MollyRevocationStatus.Fresh, revocations.GetStatus(out MollyRevocationSnapshot? snapshot));
            Assert.True(snapshot!.IsRevoked("8350192447815228107"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Transcript_MatchesTheDocumentedVector()
    {
        // Mirrors the example in Attestation.md, so client implementations can check their encoding.
        var exchange = new MollyExchangeContext(Enumerable.Repeat((byte)0x11, 16).ToArray(), Enumerable.Repeat((byte)0x22, 32).ToArray(), Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), TimeSpan.Zero);

        byte[] transcript = MollyAttestationTranscript.Build(
            MollyAttestationMode.Strict,
            exchange,
            keyHash: Enumerable.Repeat((byte)0x33, 32).ToArray(),
            signingPublicKey: [0x44, 0x44],
            attestPublicKey: [0x55],
            freshChain: [[0x66, 0x66, 0x66]],
            signingKeyChain: [],
            attestKeyChain: [[0x77], [0x88, 0x88]]);

        Assert.Equal(DocumentedChallenge, Convert.ToHexString(MollyAttestationTranscript.Challenge(exchange)));
        Assert.Equal(Convert.ToHexString("MihuBot.Molly.Attestation"u8), Convert.ToHexString(transcript.AsSpan(4, 25)));
        Assert.Equal(4 * 11 + 25 + 1 + 6 + 16 + 32 + 32 + 2 * 64 + 3 * 64, transcript.Length);
        Assert.Equal(DocumentedTranscriptSha512, Convert.ToHexString(SHA512.HashData(transcript)));
    }

    private const string DocumentedChallenge = "CFBD28E629A395A7F871D59FD09A584662543AC1DFB25208B5B65611C5623CD1B5D93817A73BA1FF3636442AD4056101EF2193036B21DAAA4B6A4CC68CD20A2E";

    private const string DocumentedTranscriptSha512 = "51CC23F02A83400EF21A1E8984E573DD091AF68DF35ED531CA7721381AEC4DC09D0C7EE39B1D651562901F1793D252293D7C2C526B52AD7CC0518BF209A34891";

    private static byte[] RevocationList(string serial, string status) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { entries = new Dictionary<string, object> { [serial] = new { status } } });

    private async Task SetAttestationStateAsync(Guid id, byte[]? state)
    {
        await using var db = _fixture.DbFactory.CreateDbContext();
        (await db.Entries.FindAsync(id))!.EncryptedAttestationState = state;
        await db.SaveChangesAsync();
    }

    private sealed class StaticHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(MollyAttestationRevocations.ListUrl, request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
