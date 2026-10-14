using System.Net.Security;
using Namotion.Devices.FritzBox.Client;
using Namotion.Devices.FritzBox.Tests.Testing;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class PinnedCertificateValidatorTests
{
    [Fact]
    public void WhenCreatedWithoutAPin_ThenTheFirstKeyIsTrustedForLaterHandshakes()
    {
        // Arrange
        using var first = FakeFritzBox.CreateCertificate();
        var validator = new PinnedCertificateValidator(null);

        // Act
        var isFirstAccepted = validator.Validate(this, first, null, SslPolicyErrors.RemoteCertificateChainErrors);
        var isRepeatedAccepted = validator.Validate(this, first, null, SslPolicyErrors.RemoteCertificateChainErrors);

        // Assert
        Assert.True(isFirstAccepted);
        Assert.True(isRepeatedAccepted);
        Assert.False(validator.IsRejected);
    }

    [Fact]
    public void WhenCreatedWithoutAPinAndALaterHandshakePresentsAnotherKey_ThenItIsRejected()
    {
        // Arrange
        using var first = FakeFritzBox.CreateCertificate();
        using var other = FakeFritzBox.CreateCertificate();
        var validator = new PinnedCertificateValidator(null);
        validator.Validate(this, first, null, SslPolicyErrors.None);

        // Act
        var isAccepted = validator.Validate(this, other, null, SslPolicyErrors.None);

        // Assert
        Assert.False(isAccepted);
        Assert.True(validator.IsRejected);
        Assert.Equal(CertificateInfo.Create(other), validator.Presented);
    }

    [Fact]
    public void WhenCreatedWithAPin_ThenOnlyThatKeyIsAccepted()
    {
        // Arrange
        using var pinned = FakeFritzBox.CreateCertificate();
        using var other = FakeFritzBox.CreateCertificate();
        var validator = new PinnedCertificateValidator(CertificateInfo.Create(pinned).Pin);

        // Act
        var isOtherAccepted = validator.Validate(this, other, null, SslPolicyErrors.None);
        var isPinnedAccepted = validator.Validate(this, pinned, null, SslPolicyErrors.None);

        // Assert
        Assert.False(isOtherAccepted);
        Assert.True(isPinnedAccepted);
    }
}
