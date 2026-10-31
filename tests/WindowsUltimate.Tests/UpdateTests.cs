using WindowsUltimate.Core.Updates;
using Xunit;

namespace WindowsUltimate.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("v1.0.42", 1, 0, 42)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.0.7+abc123", 1, 0, 7)]
    [InlineData("1.0.7-beta", 1, 0, 7)]
    [InlineData("v2.1", 2, 1, 0)]
    public void ParsesReleaseTags(string tag, int major, int minor, int build) =>
        Assert.Equal(new Version(major, minor, build), UpdateService.ParseVersion(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData(null)]
    public void RejectsInvalidTags(string? tag) => Assert.Null(UpdateService.ParseVersion(tag));

    [Fact]
    public void NewerBuildNumberWins() =>
        Assert.True(UpdateService.ParseVersion("v1.0.10") > UpdateService.ParseVersion("1.0.9"));
}
