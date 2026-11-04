using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Sign in by QR code: the payload's strict format, the one-time secret (single use, expiry,
/// one live at a time, hash-only storage), noctisPairSignIn over real Kestrel (device key, the
/// login brake, no oracle, no secret in URLs), the phone's pinned exchange (a wrong certificate
/// never receives the secret) and the Account page's Scan QR Code.
/// </summary>
public class QrPairingTests : IAsyncLifetime
{
    private const string Fp = "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89";
    private const string FpHex = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
    private const string Secret = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJ0123-_Q"; // 43 base64url chars

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "qr-pair-" + Guid.NewGuid().ToString("N"));
    private ServerUserStore _users = null!;
    private NoctisServer _server = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);
        _server = new NoctisServer(new LyricsDesk(new List<Track>()), _users, "test") { PrivateClientsOnly = true };
        await _server.StartAsync(0, certificate: null, bindAddress: IPAddress.Loopback);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.StopAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Payload ──────────────────────────────────────────────────────────

    [Fact]
    public void Payload_BuildsAndParses_TheExactFormat()
    {
        var text = PairingPayload.Build(new[] { "192.168.1.20", "10.0.0.5" }, 4747, Fp, Secret);

        Assert.Equal($"noctis-pair:1?h=192.168.1.20,10.0.0.5&p=4747&f={FpHex}&s={Secret}", text);
        Assert.True(PairingPayload.TryParse(text, out var p));
        Assert.Equal(new[] { "192.168.1.20", "10.0.0.5" }, p!.Hosts);
        Assert.Equal(4747, p.ServerPort);
        Assert.Equal(Fp, p.Fingerprint);
        Assert.Equal(Secret, p.Secret);
        Assert.Equal(new[] { "https://192.168.1.20:4747", "https://10.0.0.5:4747" }, p.ServerUrls);
        Assert.True(PairingPayload.TryParse(text + "\n", out _));      // a scanner's trailing newline
        Assert.DoesNotContain(Secret, p.ToString());                    // never in a log line
    }

    public static TheoryData<string?> Junk() => new()
    {
        null,
        "",
        "https://192.168.1.20:4747",
        $"NOCTIS-PAIR:1?h=192.168.1.20&p=4747&f={FpHex}&s={Secret}",            // scheme is exact
        $"noctis-pair:2?h=192.168.1.20&p=4747&f={FpHex}&s={Secret}",            // unknown version
        $"noctis-pair:1?p=4747&h=192.168.1.20&f={FpHex}&s={Secret}",            // out of order
        $"noctis-pair:1?h=192.168.1.20&p=4747&f={FpHex}",                       // no secret
        $"noctis-pair:1?h=192.168.1.20&p=4747&f={FpHex}&s={Secret}&x=1",        // extra field
        $"noctis-pair:1?h=192.168.1.20&h=10.0.0.1&f={FpHex}&s={Secret}",        // repeated field
        $"noctis-pair:1?h=&p=4747&f={FpHex}&s={Secret}",
        $"noctis-pair:1?h=8.8.8.8&p=4747&f={FpHex}&s={Secret}",                 // public address
        $"noctis-pair:1?h=evil.example&p=4747&f={FpHex}&s={Secret}",            // a name, not an address
        $"noctis-pair:1?h=192.168.1.20:80&p=4747&f={FpHex}&s={Secret}",
        $"noctis-pair:1?h=192.168.001.20&p=4747&f={FpHex}&s={Secret}",          // leading zeros
        $"noctis-pair:1?h=192.168.1.256&p=4747&f={FpHex}&s={Secret}",
        $"noctis-pair:1?h=192.168.1.20,192.168.1.20&p=4747&f={FpHex}&s={Secret}",
        $"noctis-pair:1?h=10.0.0.1,10.0.0.2,10.0.0.3,10.0.0.4,10.0.0.5&p=4747&f={FpHex}&s={Secret}",
        $"noctis-pair:1?h=[fe80::1]&p=4747&f={FpHex}&s={Secret}",
        $"noctis-pair:1?h=192.168.1.20&p=0&f={FpHex}&s={Secret}",
        $"noctis-pair:1?h=192.168.1.20&p=65536&f={FpHex}&s={Secret}",
        $"noctis-pair:1?h=192.168.1.20&p=04747&f={FpHex}&s={Secret}",
        $"noctis-pair:1?h=192.168.1.20&p=+4747&f={FpHex}&s={Secret}",
        $"noctis-pair:1?h=192.168.1.20&p=4747&f={FpHex[..62]}&s={Secret}",      // short pin
        $"noctis-pair:1?h=192.168.1.20&p=4747&f={FpHex.ToLowerInvariant()}&s={Secret}",
        $"noctis-pair:1?h=192.168.1.20&p=4747&f={Fp}&s={Secret}",                // colons are not the format
        $"noctis-pair:1?h=192.168.1.20&p=4747&f={FpHex}&s={Secret[..42]}",       // short secret
        $"noctis-pair:1?h=192.168.1.20&p=4747&f={FpHex}&s={Secret}A",            // long secret
        $"noctis-pair:1?h=192.168.1.20&p=4747&f={FpHex}&s={Secret[..42]}+",      // not base64url
        $"noctis-pair:1?h=192.168.1.20&p=4747&f={FpHex}&s={Secret[..42]}%",
        $"noctis-pair:1?h=192.168.1.20&p=4747&f={FpHex}&s={Secret} junk",
        $"noctis-pair:1?h=192.168.1.20&p=4747&f={FpHex}&s={Secret}" + new string(' ', 10) + "x",
        "noctis-pair:1?" + new string('a', 500),
    };

    [Theory]
    [MemberData(nameof(Junk))]
    public void Payload_RefusesAnythingThatIsNotExactlyOurs(string? text)
    {
        Assert.False(PairingPayload.TryParse(text, out var p));
        Assert.Null(p);
    }

    [Fact]
    public void Payload_Build_RefusesFieldsThePhoneWouldRefuse()
    {
        Assert.Throws<ArgumentException>(() => PairingPayload.Build(new[] { "8.8.8.8" }, 4747, Fp, Secret));
        Assert.Throws<ArgumentException>(() => PairingPayload.Build(Array.Empty<string>(), 4747, Fp, Secret));
        Assert.Throws<ArgumentException>(() => PairingPayload.Build(new[] { "192.168.1.20" }, 4747, Fp[..20], Secret));
        Assert.Throws<ArgumentException>(() => PairingPayload.Build(new[] { "192.168.1.20" }, 4747, Fp, "short"));
    }

    [Fact]
    public void LanAddresses_PrimaryFirst_ThenGatewayAdaptersOnly_PrivateIPv4_AtMostFour()
    {
        var order = LanAddresses.Order("192.168.1.20", new (IPAddress, bool)[]
        {
            (IPAddress.Parse("172.20.0.1"), false),     // Hyper-V/WSL: no gateway
            (IPAddress.Parse("192.168.1.20"), true),    // the primary again
            (IPAddress.Parse("10.0.0.7"), true),
            (IPAddress.Parse("8.8.4.4"), true),         // public
            (IPAddress.Parse("fe80::1"), true),         // IPv6
            (IPAddress.Parse("127.0.0.1"), true),
            (IPAddress.Parse("10.0.0.8"), true),
            (IPAddress.Parse("10.0.0.9"), true),
            (IPAddress.Parse("10.0.0.10"), true),
        });
        Assert.Equal(new[] { "192.168.1.20", "10.0.0.7", "10.0.0.8", "10.0.0.9" }, order);
        Assert.Empty(LanAddresses.Order(null, Array.Empty<(IPAddress, bool)>()));
    }

    // ── The one-time secret ──────────────────────────────────────────────

    [Fact]
    public void Code_Is256RandomBits_WorksOnce_ForItsAccount()
    {
        var codes = new PairingCodes();
        var secret = codes.Issue("alice", out _);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", secret);
        Assert.NotEqual(secret, new PairingCodes().Issue("alice", out _));

        Assert.True(codes.TryRedeem(secret, out var user));
        Assert.Equal("alice", user);
        Assert.False(codes.TryRedeem(secret, out _));      // used up
        Assert.False(codes.IsLive);
    }

    [Fact]
    public void Code_Expires_AfterItsLifetime()
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var codes = new PairingCodes(() => now);
        var secret = codes.Issue("alice", out var expires);
        Assert.Equal(now + PairingCodes.Lifetime, expires);
        Assert.Equal(TimeSpan.FromMinutes(2), PairingCodes.Lifetime);

        now += PairingCodes.Lifetime - TimeSpan.FromSeconds(1);
        Assert.True(codes.IsLive);
        now += TimeSpan.FromSeconds(1);
        Assert.False(codes.IsLive);
        Assert.False(codes.TryRedeem(secret, out _));
    }

    [Fact]
    public void Code_OnlyOneLiveAtATime_AndRevokeKillsIt()
    {
        var codes = new PairingCodes();
        var first = codes.Issue("alice", out _);
        var second = codes.Issue("alice", out _);
        Assert.False(codes.TryRedeem(first, out _));
        Assert.True(codes.TryRedeem(second, out _));

        var third = codes.Issue("alice", out _);
        codes.Revoke();
        Assert.False(codes.TryRedeem(third, out _));
    }

    [Fact]
    public void Code_WrongGuesses_DoNotBurnTheLiveCode_AndOnlyItsHashIsKept()
    {
        var codes = new PairingCodes();
        var secret = codes.Issue("alice", out _);
        var oneOff = (secret[0] == 'A' ? 'B' : 'A') + secret[1..];
        Assert.False(codes.TryRedeem(oneOff, out _));
        Assert.False(codes.TryRedeem(secret[..42], out _));
        Assert.False(codes.TryRedeem(null, out _));
        Assert.False(codes.TryRedeem("", out _));

        // Nothing in the object is the secret itself: a 32-byte SHA-256 only.
        foreach (var field in typeof(PairingCodes).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            var value = field.GetValue(codes);
            Assert.NotEqual(secret, value as string);
            if (value is byte[] bytes)
            {
                Assert.Equal(32, bytes.Length);
                Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(secret)), bytes);
            }
        }
        Assert.True(codes.TryRedeem(secret, out _));
    }

    // ── noctisPairSignIn over Kestrel ────────────────────────────────────

    private static JsonElement Envelope(string json) => JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response");

    private async Task<(JsonElement Body, HttpResponseMessage Response)> Pair(string? code, string deviceId = "phone-qr-01",
        string deviceName = "Galaxy S23", IDictionary<string, string>? extra = null, string query = "")
    {
        var fields = new Dictionary<string, string> { ["deviceId"] = deviceId, ["deviceName"] = deviceName, ["f"] = "json" };
        if (code is not null) fields["code"] = code;
        if (extra is not null) foreach (var (k, v) in extra) fields[k] = v;
        var res = await _http.PostAsync("rest/noctisPairSignIn.view" + query, new FormUrlEncodedContent(fields), Ct);
        return (Envelope(await res.Content.ReadAsStringAsync(Ct)), res);
    }

    private static int ErrorCode(JsonElement r) => r.GetProperty("error").GetProperty("code").GetInt32();

    [Fact]
    public async Task Endpoint_IssuesADeviceKey_ThatWorks_AndThePhoneIsListed_AndTheCodeIsUsedUp()
    {
        var used = 0;
        _server.PairingCodeUsed += (_, _) => Interlocked.Increment(ref used);
        var secret = _server.IssuePairingCode("alice", out _);

        var (r, res) = await Pair(secret);

        Assert.Equal("ok", r.GetProperty("status").GetString());
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        var signIn = r.GetProperty("noctisSignIn");
        Assert.Equal("alice", signIn.GetProperty("user").GetString());
        var key = signIn.GetProperty("apiKey").GetString()!;
        Assert.StartsWith("nk_", key);
        Assert.NotEqual(secret, key);
        var device = Assert.Single(_users.Devices());
        Assert.Equal(("alice", "phone-qr-01", "Galaxy S23"), (device.User, device.DeviceId, device.DeviceName));
        Assert.Equal(1, used);
        Assert.False(_server.HasLivePairingCode);

        // The key is an ordinary device key: it authenticates, and noctisSignOut revokes it.
        using (var req = new HttpRequestMessage(HttpMethod.Get, "rest/getUser.view?f=json"))
        {
            req.Headers.Add(NoctisServer.KeyHeader, key);
            var user = Envelope(await (await _http.SendAsync(req, Ct)).Content.ReadAsStringAsync(Ct));
            Assert.Equal("ok", user.GetProperty("status").GetString());
        }
        Assert.True(_users.RevokeDevice("alice", "phone-qr-01"));

        // Second use: refused, nothing issued.
        var (again, _) = await Pair(secret, deviceId: "phone-qr-02");
        Assert.Equal(40, ErrorCode(again));
        Assert.Empty(_users.Devices());
    }

    [Fact]
    public async Task Endpoint_WrongUsedAndRevokedCodes_AllAnswerTheSame()
    {
        var secret = _server.IssuePairingCode("alice", out _);
        var (wrong, _) = await Pair(new string('A', 43));
        Assert.True(_server.HasLivePairingCode);                         // a guess does not burn it
        Assert.Equal("ok", (await Pair(secret)).Body.GetProperty("status").GetString());
        var (used, _) = await Pair(secret, deviceId: "phone-qr-02");
        var revokedSecret = _server.IssuePairingCode("alice", out _);
        _server.RevokePairingCode();
        var (revoked, _) = await Pair(revokedSecret, deviceId: "phone-qr-03");

        foreach (var r in new[] { wrong, used, revoked })
        {
            Assert.Equal(40, ErrorCode(r));
            Assert.Equal(wrong.GetProperty("error").GetProperty("message").GetString(), r.GetProperty("error").GetProperty("message").GetString());
        }
    }

    [Fact]
    public async Task Endpoint_RefusesTheCodeInTheUrl_OrBesideAnotherCredential_WithoutUsingItUp()
    {
        var secret = _server.IssuePairingCode("alice", out _);

        var (inUrl, _) = await Pair(null, query: "?code=" + secret);
        Assert.Equal("failed", inUrl.GetProperty("status").GetString());
        var (withPassword, _) = await Pair(secret, extra: new Dictionary<string, string> { ["u"] = "alice", ["p"] = "correct horse" });
        Assert.Equal(42, ErrorCode(withPassword));
        var (withKey, _) = await Pair(secret, extra: new Dictionary<string, string> { ["apiKey"] = "nk_whatever" });
        Assert.Equal(42, ErrorCode(withKey));
        var (badDevice, _) = await Pair(secret, deviceId: "x");
        Assert.Equal(10, ErrorCode(badDevice));
        var get = Envelope(await _http.GetStringAsync($"rest/noctisPairSignIn.view?f=json&deviceId=phone-qr-01&deviceName=P", Ct));
        Assert.Equal("failed", get.GetProperty("status").GetString());

        Assert.True(_server.HasLivePairingCode);
        Assert.Empty(_users.Devices());
        Assert.Equal("ok", (await Pair(secret)).Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Endpoint_FailedAttempts_HitTheLoginBrake_EvenForTheRightCode()
    {
        var secret = _server.IssuePairingCode("alice", out _);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            Assert.Equal(40, ErrorCode((await Pair(new string((char)('A' + i), 43))).Body));

        var (locked, res) = await Pair(secret);
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.NotNull(res.Headers.RetryAfter);
        Assert.Equal(40, ErrorCode(locked));
        Assert.Empty(_users.Devices());
        Assert.True(_server.HasLivePairingCode);                         // refused before it was checked

        // The QR brake is its own bucket: the password sign-in from the same address still works.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["u"] = "alice", ["p"] = "correct horse", ["deviceId"] = "phone-pw-01", ["deviceName"] = "Pixel", ["f"] = "json",
        });
        var pw = Envelope(await (await _http.PostAsync("rest/noctisSignIn.view", form, Ct)).Content.ReadAsStringAsync(Ct));
        Assert.Equal("ok", pw.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Endpoint_StoppingTheServer_KillsTheCode_AndParkedSignInRefusesIt()
    {
        var secret = _server.IssuePairingCode("alice", out _);
        await _server.StopAsync();
        Assert.False(_server.HasLivePairingCode);
        Assert.Throws<InvalidOperationException>(() => _server.IssuePairingCode("alice", out _));
        await _server.StartAsync(0, certificate: null, bindAddress: IPAddress.Loopback);
        _http.Dispose();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
        Assert.Equal(40, ErrorCode((await Pair(secret)).Body));

        await _server.StopAsync();
        _server = new NoctisServer(new LyricsDesk(new List<Track>()), _users, "test") { PrivateClientsOnly = true, DeviceSignInEnabled = false };
        await _server.StartAsync(0, certificate: null, bindAddress: IPAddress.Loopback);
        _http.Dispose();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
        Assert.Throws<InvalidOperationException>(() => _server.IssuePairingCode("alice", out _));
        var (parked, _) = await Pair(Secret);
        Assert.Contains("Unknown method", parked.GetProperty("error").GetProperty("message").GetString());
    }

    // ── The phone: pinned exchange ───────────────────────────────────────

    [Fact]
    public async Task Phone_RefusesAMismatchedFingerprint_BeforeTheSecretLeaves_ThenSignsInWithTheRightOne()
    {
        // Schannel refuses ephemeral private keys for a server credential (see NoctisServerTests).
        if (!OperatingSystem.IsWindows()) return;
        using var cert = ServerCertificate.LoadOrCreate(Path.Combine(_dir, "tls"));
        using var other = ServerCertificate.Create();
        await using var https = new NoctisServer(new LyricsDesk(new List<Track>()), _users, "test") { PrivateClientsOnly = true };
        var authenticated = 0;
        https.ClientAuthenticated += (_, _) => Interlocked.Increment(ref authenticated);
        await https.StartAsync(0, cert, Ct, IPAddress.Loopback);
        var secret = https.IssuePairingCode("alice", out _);

        var phonePersistence = new PersistenceService(Path.Combine(_dir, "phone-files"));
        var phoneLibrary = new LibraryService(new MetadataService(), phonePersistence, new SqliteLibraryIndexService(phonePersistence),
            new NoctisAccountLibraryTests.NoOpAudit());
        var svc = new NoctisAccountService(phoneLibrary, phonePersistence, NoctisHandlers.Sockets,
            Path.Combine(_dir, "phone-nobackup", "account"), Path.Combine(_dir, "phone-nobackup", "offline"), "Test Phone", marshal: a => a());

        // A code whose pin is another certificate: the TLS handshake fails, nothing is sent.
        Assert.True(PairingPayload.TryParse(PairingPayload.Build(new[] { "127.0.0.1" }, https.Port, ServerCertificate.Fingerprint(other), secret), out var forged));
        var ex = await Assert.ThrowsAsync<NoctisServerException>(() => svc.SignInWithPairingAsync(forged!, Ct));
        Assert.Equal(NoctisErrorKind.CertificateChanged, ex.Kind);
        Assert.Equal(0, authenticated);
        Assert.True(https.HasLivePairingCode);                           // the secret never arrived
        Assert.False(svc.IsSignedIn);
        Assert.Empty(_users.Devices());

        // The real code: signed in, the pin stored, the phone listed under its own name.
        Assert.True(PairingPayload.TryParse(PairingPayload.Build(new[] { "127.0.0.1" }, https.Port, ServerCertificate.Fingerprint(cert), secret), out var real));
        await svc.SignInWithPairingAsync(real!, Ct);
        Assert.True(svc.IsSignedIn);
        Assert.Equal("alice", svc.Account!.UserName);
        Assert.Equal(ServerCertificate.Fingerprint(cert), svc.Account.Fingerprint);
        Assert.Equal($"https://127.0.0.1:{https.Port}", svc.Account.ServerUrl);
        var device = Assert.Single(_users.Devices());
        Assert.Equal((svc.DeviceId, "Test Phone"), (device.DeviceId, device.DeviceName));
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(_dir, "phone-nobackup", "account", "account.json")));

        // Scanned again: the code is used up, which the phone words as expired.
        var reuse = await Assert.ThrowsAsync<NoctisServerException>(() => svc.SignInWithPairingAsync(real!, Ct));
        Assert.Equal(NoctisErrorKind.PairingExpired, reuse.Kind);

    }

    [Fact]
    public void Phone_MapsTheServersAnswers_ForAQrSignIn()
    {
        Assert.Equal(NoctisErrorKind.PairingExpired, NoctisServerClient.MapError(40, signingIn: true, "noctisPairSignIn").Kind);
        Assert.Equal(NoctisErrorKind.Outdated, NoctisServerClient.MapError(0, signingIn: true, "noctisPairSignIn").Kind);
        Assert.Equal(NoctisErrorKind.BadCredentials, NoctisServerClient.MapError(40, signingIn: true, "noctisSignIn").Kind);
    }

    // ── The Account page: Scan QR Code ───────────────────────────────────

    private sealed class FakeScanner(params QrScanResult[] results) : IQrScanner
    {
        private int _next;
        public int Scans { get; private set; }
        public Task<QrScanResult> ScanAsync(CancellationToken ct = default)
        {
            Scans++;
            return Task.FromResult(results[Math.Min(_next++, results.Length - 1)]);
        }
    }

    private static AccountPageViewModel PageWith(MobileFixtures.Rig rig, MobileAccountTests.FakeAccount account, IQrScanner? scanner)
    {
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = account,
            Marshal = a => a(),
            TintFactory = rig.Shell.TintFactory,
            QrScanner = scanner,
        };
        shell.OpenAccountCommand.Execute(null);
        return Assert.IsType<AccountPageViewModel>(shell.CurrentPage);
    }

    private static string Code => PairingPayload.Build(new[] { "192.168.1.20" }, 4747, Fp, Secret);

    [Fact]
    public async Task Page_ScanQrCode_SignsInWithNoTyping_ThenSyncs()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new MobileAccountTests.FakeAccount();
        var page = PageWith(rig, account, new FakeScanner(new QrScanResult(QrScanOutcome.Scanned, Code)));
        Assert.True(page.CanScanQr);

        await page.ScanQrCodeCommand.ExecuteAsync(null);

        var pairing = Assert.Single(account.PairingSignIns);
        Assert.Equal(Secret, pairing.Secret);
        Assert.Equal(Fp, pairing.Fingerprint);
        Assert.Empty(account.Probes);                    // no address typed, no code to compare
        Assert.Empty(account.SignIns);
        Assert.True(page.IsSignedIn);
        Assert.Equal(1, account.Syncs);
        Assert.False(page.HasError);
        Assert.False(page.IsSigningInByQr);
    }

    [Fact]
    public async Task Page_ScanQrCode_JunkCancelAndNoScanner_SendNothing()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new MobileAccountTests.FakeAccount();
        var scanner = new FakeScanner(
            new QrScanResult(QrScanOutcome.Scanned, "https://192.168.1.20:4747"),
            new QrScanResult(QrScanOutcome.Cancelled),
            new QrScanResult(QrScanOutcome.Unavailable));
        var page = PageWith(rig, account, scanner);

        await page.ScanQrCodeCommand.ExecuteAsync(null);
        Assert.Equal(AccountPageViewModel.NotAPairingCodeText, page.ErrorText);
        Assert.True(page.HasGeneralError);

        await page.ScanQrCodeCommand.ExecuteAsync(null);
        Assert.False(page.HasError);

        await page.ScanQrCodeCommand.ExecuteAsync(null);
        Assert.Equal(AccountPageViewModel.ScannerUnavailableText, page.ErrorText);

        Assert.Empty(account.PairingSignIns);
        Assert.False(page.IsSignedIn);
        Assert.False(PageWith(rig, new MobileAccountTests.FakeAccount(), scanner: null).CanScanQr);
    }

    [Theory]
    [InlineData(NoctisErrorKind.PairingExpired, "This code has expired or was already used. Scan the new code your computer shows now.")]
    [InlineData(NoctisErrorKind.CertificateChanged, "The computer at that address isn't the one showing the code, so nothing was sent. Check that your phone is on the same Wi-Fi, then scan again.")]
    [InlineData(NoctisErrorKind.Unreachable, "Can't reach your computer. Check that your phone is on the same Wi-Fi as your computer, then scan again.")]
    [InlineData(NoctisErrorKind.LockedOut, "Too many failed sign-ins. Wait a few minutes, then try again.")]
    public async Task Page_ScanQrCode_Failures_AreWordedForTheCode_NotUnderTheTypedFields(NoctisErrorKind kind, string expected)
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new MobileAccountTests.FakeAccount { SignInError = new NoctisServerException(kind, "x") };
        var page = PageWith(rig, account, new FakeScanner(new QrScanResult(QrScanOutcome.Scanned, Code)));

        await page.ScanQrCodeCommand.ExecuteAsync(null);

        Assert.Equal(expected, page.ErrorText);
        Assert.True(page.HasGeneralError);
        Assert.False(page.HasAddressError);
        Assert.False(page.IsSignedIn);
        Assert.Equal(0, account.Syncs);
    }
}
