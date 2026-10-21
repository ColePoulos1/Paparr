using Paparr.API.Authentication;

namespace Paparr.UnitTests;

public sealed class OwnerPasswordTests
{
    [Fact]
    public void Salted_hashes_verify_only_the_correct_password()
    {
        const string password = "an owner password for tests";
        var first = OwnerPassword.Hash(password);
        var second = OwnerPassword.Hash(password);
        Assert.NotEqual(first, second);
        Assert.True(OwnerPassword.IsValidHash(first));
        Assert.True(OwnerPassword.Verify(first, password));
        Assert.False(OwnerPassword.Verify(first, "a different owner password"));
        Assert.NotEqual(OwnerPassword.Stamp(first), OwnerPassword.Stamp(second));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(513)]
    public void Password_generation_rejects_invalid_lengths(int length) =>
        Assert.Throws<ArgumentException>(() => OwnerPassword.Hash(new string('a', length)));

    [Theory]
    [InlineData("")]
    [InlineData("not a password hash")]
    [InlineData("AQAAAA==")]
    public void Malformed_hashes_fail_without_throwing(string hash)
    {
        Assert.False(OwnerPassword.IsValidHash(hash));
        Assert.False(OwnerPassword.Verify(hash, "an owner password for tests"));
    }

    [Fact]
    public void Excessive_work_factor_is_rejected_before_password_verification()
    {
        var bytes = Convert.FromBase64String(OwnerPassword.Hash("an owner password for tests"));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(5, 4), uint.MaxValue);
        Assert.False(OwnerPassword.Verify(Convert.ToBase64String(bytes), "an owner password for tests"));
    }
}
