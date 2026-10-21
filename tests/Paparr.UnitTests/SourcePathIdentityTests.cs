using Paparr.API.Domain;

namespace Paparr.UnitTests;

public sealed class SourcePathIdentityTests
{
    [Fact]
    public void Path_keys_follow_platform_case_comparison_and_are_sha256_hex()
    {
        var root = Path.Combine(Path.GetTempPath(), "Paparr", "Books");
        var upper = Path.Combine(root, "Copy.epub");
        var lower = Path.Combine(root, "copy.epub");
        var dotSegment = Path.Combine(root, "subfolder", "..", "Copy.epub");

        Assert.Equal(OperatingSystem.IsWindows(), SourcePathIdentity.SamePath(upper, lower));
        Assert.Equal(OperatingSystem.IsWindows(), SourcePathIdentity.CreateKey(upper) == SourcePathIdentity.CreateKey(lower));
        Assert.True(SourcePathIdentity.SamePath(upper, dotSegment));
        Assert.Equal(SourcePathIdentity.CreateKey(upper), SourcePathIdentity.CreateKey(dotSegment));
        Assert.Equal(64, SourcePathIdentity.CreateKey(upper).Length);
        Assert.Equal(Path.GetFullPath(upper), SourcePathIdentity.Canonicalize(upper));
    }
}
