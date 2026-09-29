using System.Security.Cryptography;
using Noctis.Services.LyricsStudio;
using Whisper.net;
using Xunit;

namespace Noctis.Tests;

/// <summary>Lyrics Studio has one speech model, Medium (owner, 09-29). Every saved preference,
/// retired or unknown, resolves to it, and the file only counts once it is whole and intact.</summary>
public class WhisperModelCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-model-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void TheOneModel_IsThePublishedMediumFile()
    {
        var m = WhisperModelManager.Medium;
        Assert.Equal(WhisperModelSize.Medium, m.Size);
        Assert.Equal("ggml-medium.bin", m.FileName);
        Assert.Equal("https://huggingface.co/sandrohanea/whisper.net/resolve/v4/classic/ggml-medium.bin", m.Url);
        // Hugging Face LFS values for classic/ggml-medium.bin (same file as ggerganov/whisper.cpp's).
        Assert.Equal(1_533_763_059L, m.Bytes);
        Assert.Equal("6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208", m.Sha256);
        Assert.Equal(WhisperAlignmentHeadsPreset.Medium, m.AlignmentHeads);
        Assert.Equal("1.4 GB", m.SizeText);
    }

    [Theory]
    [InlineData("Tiny")]
    [InlineData("Base")]
    [InlineData("small")]
    [InlineData("Medium")]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    public void EverySavedPreference_ResolvesToMedium(string? saved)
    {
        Assert.Equal(WhisperModelSize.Medium, WhisperModelManager.Parse(saved));
        Assert.Same(WhisperModelManager.Medium, WhisperModelManager.Info(WhisperModelManager.Parse(saved)));
    }

    [Theory]
    [InlineData(WhisperModelSize.Tiny)]
    [InlineData(WhisperModelSize.Base)]
    [InlineData(WhisperModelSize.Small)]
    [InlineData(WhisperModelSize.Medium)]
    public void EverySize_IsTheOneModel(WhisperModelSize size)
    {
        Assert.Same(WhisperModelManager.Medium, WhisperModelManager.Info(size));
        var manager = new WhisperModelManager(_root);
        Assert.Equal(Path.Combine(_root, "models", "whisper", "ggml-medium.bin"), manager.PathFor(size));
    }

    [Fact]
    public void NewSettings_DefaultToMedium()
        => Assert.Equal("Medium", new Noctis.Models.AppSettings().LyricsStudioModel);

    [Fact]
    public void AnOldBaseDownload_IsNotUsed_AndIsLeftOnDisk()
    {
        var manager = new WhisperModelManager(_root);
        Directory.CreateDirectory(manager.Directory);
        var basePath = Path.Combine(manager.Directory, "ggml-base.bin");
        File.WriteAllBytes(basePath, new byte[1024]);

        Assert.Equal(WhisperModelState.Missing, manager.State);
        Assert.False(manager.IsInstalled(WhisperModelSize.Base));
        Assert.True(File.Exists(basePath));
    }

    // ── Integrity ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AFileFromBeforeChecksums_IsUsable_ThenCheckedOnce()
    {
        var manager = StudioTestModel.Create(_root, out var bytes);
        File.WriteAllBytes(manager.ModelPath, bytes);

        Assert.Equal(WhisperModelState.Unverified, manager.State);
        Assert.True(manager.IsInstalled());

        Assert.True(await manager.VerifyAsync(null, CancellationToken.None));
        Assert.Equal(WhisperModelState.Ready, manager.State);
        Assert.True(File.Exists(manager.ModelPath + ".sha256"));
    }

    [Fact]
    public async Task AFileThatFailsItsChecksum_IsDamaged_AndNotInstalled()
    {
        var manager = StudioTestModel.Create(_root, out var bytes);
        bytes[5] ^= 0xFF; // same length, one byte off
        File.WriteAllBytes(manager.ModelPath, bytes);

        Assert.False(await manager.VerifyAsync(null, CancellationToken.None));
        Assert.Equal(WhisperModelState.Damaged, manager.State);
        Assert.False(manager.IsInstalled());
        Assert.True(File.Exists(manager.ModelPath)); // left alone: a fresh download replaces it
    }

    [Fact]
    public void AFileOfTheWrongLength_IsDamaged()
    {
        var manager = StudioTestModel.Create(_root, out var bytes);
        File.WriteAllBytes(manager.ModelPath, bytes.AsSpan(0, bytes.Length - 1).ToArray());

        Assert.Equal(WhisperModelState.Damaged, manager.State);
        Assert.False(manager.IsInstalled());
    }

    [Fact]
    public async Task AFileReplacedAfterItsCheck_IsCheckedAgain()
    {
        var manager = StudioTestModel.Create(_root, out var bytes);
        File.WriteAllBytes(manager.ModelPath, bytes);
        Assert.True(await manager.VerifyAsync(null, CancellationToken.None));

        File.WriteAllBytes(manager.ModelPath, bytes);
        File.SetLastWriteTimeUtc(manager.ModelPath, DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(WhisperModelState.Unverified, manager.State);
    }

    [Fact]
    public void AStoppedDownload_IsPartial_WithItsBytes()
    {
        var manager = StudioTestModel.Create(_root, out var bytes);
        File.WriteAllBytes(manager.ModelPath + ".part", bytes.AsSpan(0, 100).ToArray());

        Assert.Equal(WhisperModelState.Partial, manager.State);
        Assert.Equal(100, manager.PartialBytes);
        Assert.False(manager.IsInstalled());
    }

    [Fact]
    public void Sha256_MatchesTheFrameworkHash()
    {
        var path = Path.Combine(_root, "blob.bin");
        Directory.CreateDirectory(_root);
        var data = Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 7)).ToArray();
        File.WriteAllBytes(path, data);
        var reports = new List<double>();

        var hex = WhisperModelManager.ComputeSha256(path, new InlineProgress<double>(reports.Add), CancellationToken.None);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), hex);
        Assert.Equal(1.0, reports[^1]);
        Assert.True(reports.SequenceEqual(reports.Order()));
    }
}

/// <summary>A small stand-in for the speech model, so tests never allocate a 1.5 GB file.</summary>
internal static class StudioTestModel
{
    public static WhisperModelManager Create(string root, out byte[] bytes, HttpClient? http = null, Noctis.Services.ResumableDownload.Options? options = null)
    {
        bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i * 13 + 1)).ToArray();
        var info = WhisperModelManager.Medium with
        {
            FileName = "test-model.bin",
            Bytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        };
        var manager = new WhisperModelManager(root, http, options, info);
        Directory.CreateDirectory(manager.Directory);
        return manager;
    }

    /// <summary>The stand-in, on disk and whole, as a Studio sees an installed model.</summary>
    public static WhisperModelManager Installed(string root)
    {
        var manager = Create(root, out var bytes);
        File.WriteAllBytes(manager.ModelPath, bytes);
        return manager;
    }
}
