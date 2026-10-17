using OpenVideoSmartShare.Infrastructure;
using OpenVideoSmartShare.Library;

namespace OpenVideoSmartShare.Tests;

public class FirewallHelperTests
{
    [Fact]
    public void ReadsFirewallStateWithoutElevation()
    {
        // A path no rule refers to: either the firewall is off or no rule exists.
        var state = FirewallHelper.Check(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"));
        Assert.Contains(state, new[] { FirewallState.Missing, FirewallState.Disabled });
    }
}

public class MediaInfoReaderTests
{
    [Fact]
    public void UnknownForMissingFile()
    {
        var details = MediaInfoReader.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mkv"));
        Assert.Null(details.Duration);
        Assert.Null(details.Resolution);
    }

    /// <summary>Reads a real video when OVSS_SAMPLE_VIDEO points to one (skipped otherwise).</summary>
    [Fact]
    public void ReadsDurationOfSampleVideo()
    {
        var sample = Environment.GetEnvironmentVariable("OVSS_SAMPLE_VIDEO");
        if (string.IsNullOrEmpty(sample) || !File.Exists(sample))
        {
            return;
        }

        var details = MediaInfoReader.Read(sample);
        Assert.NotNull(details.Duration);
        Assert.True(details.Duration > TimeSpan.Zero);
    }
}
