using Epsilon.Video;
using Xunit;

namespace Epsilon.Core.Tests;

public class RestreamUrlTests
{
    [Fact]
    public void CustomUrl_UsesPortAndPath()
    {
        Assert.True(RtspRestreamer.TryParseServeUrl("rtsp://127.0.0.1:9554/live/cam1", 8554, out var ep, out _));
        Assert.Equal("127.0.0.1", ep.Host);
        Assert.Equal(9554, ep.Port);
        Assert.Equal("live/cam1", ep.Path);
        Assert.Equal("rtsp://127.0.0.1:9554/live/cam1", ep.Url);
    }

    [Fact]
    public void CustomUrl_DefaultsPortAndAcceptsMissingScheme()
    {
        Assert.True(RtspRestreamer.TryParseServeUrl("127.0.0.1/video", 8554, out var ep, out _));
        Assert.Equal(8554, ep.Port);
        Assert.Equal("video", ep.Path);
    }

    [Fact]
    public void CustomUrl_HostNameIsAccepted()
    {
        Assert.True(RtspRestreamer.TryParseServeUrl("rtsp://gcs-laptop:8554/epsilon", 8554, out var ep, out _));
        Assert.Equal("gcs-laptop", ep.Host);
    }

    [Fact]
    public void CustomUrl_RejectsAddressOfAnotherComputer()
    {
        Assert.False(RtspRestreamer.TryParseServeUrl("rtsp://203.0.113.77:8554/epsilon", 8554, out _, out var error));
        Assert.Contains("Push mode", error);
    }

    [Fact]
    public void CustomUrl_RejectsOtherSchemesAndBadPaths()
    {
        Assert.False(RtspRestreamer.TryParseServeUrl("http://127.0.0.1:8554/epsilon", 8554, out _, out _));
        Assert.False(RtspRestreamer.TryParseServeUrl("rtsp://127.0.0.1:8554/bad path!", 8554, out _, out _));
    }

    [Fact]
    public void AutomaticAddress_IsNotLinkLocalWhenAnotherAddressExists()
    {
        string best = RtspRestreamer.BestLocalIp();
        bool anyRoutable = RtspRestreamer.LocalIPv4Addresses().Any(a => !a.StartsWith("169.254."));
        if (anyRoutable) Assert.False(best.StartsWith("169.254."));
    }
}
