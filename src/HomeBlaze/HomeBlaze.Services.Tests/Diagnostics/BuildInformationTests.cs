using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class BuildInformationTests
{
    private const string Commit = "b1b7c2f4568b7a83baf8d041afaab8f2214fb6cd";

    [Theory]
    [InlineData("0.5.0+" + Commit, "0.5.0", Commit)]
    [InlineData("edge+" + Commit, "edge", Commit)]
    [InlineData("1.0.0", "1.0.0", null)]
    [InlineData("6.1.0+f4da16f15a", "6.1.0", "f4da16f15a")]
    [InlineData("7.3.1+548f69ad40501ec100cef99fe183aca49e95a09a.548f69ad40501ec100cef99fe183aca49e95a09a", "7.3.1", "548f69ad40501ec100cef99fe183aca49e95a09a")]
    [InlineData("1.5.378.145+419fb749ee6680a2177c191b4a964d596855a558", "1.5.378.145", "419fb749ee6680a2177c191b4a964d596855a558")]
    [InlineData("2.0.0+build.5", "2.0.0", null)]
    [InlineData("1.0.0+ABCDEF1234", "1.0.0", "abcdef1234")]
    [InlineData("1.0.0+", "1.0.0", null)]
    public void WhenInformationalVersionIsParsed_ThenVersionAndCommitAreSplit(string informationalVersion, string expectedVersion, string? expectedCommit)
    {
        // Act
        var (version, commit) = BuildInformation.ParseInformationalVersion(informationalVersion);

        // Assert
        Assert.Equal(expectedVersion, version);
        Assert.Equal(expectedCommit, commit);
    }

    [Theory]
    [InlineData("https://github.com/dotnet/MQTTnet.git", "https://github.com/dotnet/MQTTnet")]
    [InlineData("https://github.com/owner/repo/", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo.GIT/", "https://github.com/owner/repo")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    [InlineData(".git", null)]
    public void WhenRepositoryUrlIsNormalized_ThenSuffixesAreRemoved(string? repositoryUrl, string? expected)
    {
        // Act
        var normalized = BuildInformation.NormalizeRepositoryUrl(repositoryUrl);

        // Assert
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void WhenMetadataIsPresent_ThenAllValuesAreRead()
    {
        // Arrange
        var metadata = new Dictionary<string, string?>
        {
            ["RepositoryUrl"] = "https://github.com/owner/repo.git",
            ["Namotion.Build.Tag"] = "v0.5.0",
            ["Namotion.Build.Branch"] = "master",
            ["Namotion.Build.CommitDate"] = "2026-10-14T18:02:00+02:00",
            ["Namotion.Build.Time"] = "2026-10-14T18:20:00Z",
            ["Namotion.Build.RunUrl"] = "https://github.com/owner/repo/actions/runs/1",
            ["Namotion.Build.ImageTags"] = "0.5.0;0.5;latest",
        };

        // Act
        var build = BuildInformation.Create("0.5.0+" + Commit, metadata);

        // Assert
        Assert.Equal("0.5.0", build.Version);
        Assert.Equal(Commit, build.Commit);
        Assert.Equal("https://github.com/owner/repo", build.RepositoryUrl);
        Assert.Equal("v0.5.0", build.Tag);
        Assert.Equal("master", build.Branch);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 16, 2, 0, TimeSpan.Zero), build.CommitDate);
        Assert.Equal(TimeSpan.Zero, build.CommitDate!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 18, 20, 0, TimeSpan.Zero), build.BuildTime);
        Assert.Equal("https://github.com/owner/repo/actions/runs/1", build.RunUrl);
        Assert.Equal("0.5.0;0.5;latest", build.ImageTags);
    }

    [Fact]
    public void WhenMetadataIsMissingOrEmpty_ThenValuesAreNull()
    {
        // Arrange
        var metadata = new Dictionary<string, string?> { ["Namotion.Build.Tag"] = "", ["Namotion.Build.Time"] = "not a date" };

        // Act
        var build = BuildInformation.Create("1.0.0", metadata);

        // Assert
        Assert.Equal("1.0.0", build.Version);
        Assert.Null(build.Commit);
        Assert.Null(build.RepositoryUrl);
        Assert.Null(build.Tag);
        Assert.Null(build.Branch);
        Assert.Null(build.CommitDate);
        Assert.Null(build.BuildTime);
        Assert.Null(build.RunUrl);
        Assert.Null(build.ImageTags);
    }

    [Fact]
    public void WhenReadFromThisAssembly_ThenHasVersionAndRepository()
    {
        // Act
        var build = BuildInformation.FromAssembly(typeof(BuildInformation).Assembly);

        // Assert
        Assert.False(string.IsNullOrEmpty(build.Version));
        Assert.Equal("https://github.com/RicoSuter/Namotion.Interceptor", build.RepositoryUrl);
    }
}
