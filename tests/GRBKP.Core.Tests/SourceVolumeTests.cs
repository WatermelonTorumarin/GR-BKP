using GRBKP.Core.Discovery;

namespace GRBKP.Core.Tests;

/// <summary>
/// O app foi desenhado para ler um SSD ANTIGO desconectado, montado como segunda unidade. Mas na
/// prática o técnico também usa o app na própria máquina em uso, com o Windows rodando — cenário
/// legítimo e mais prático em muitos casos. As duas situações exigem tratamentos diferentes
/// (registro travado, aplicativos abertos), e é esta checagem que distingue uma da outra.
/// </summary>
public sealed class SourceVolumeTests
{
    [Theory]
    [InlineData(@"C:\", @"C:\Windows\System32", true)]
    [InlineData(@"C:", @"C:\Windows\System32", true)]
    [InlineData(@"c:\", @"C:\Windows\System32", true)]
    [InlineData(@"C:\Users\alicev", @"C:\Windows\System32", true)]
    [InlineData(@"F:\", @"C:\Windows\System32", false)]
    [InlineData(@"E:\Users", @"C:\Windows\System32", false)]
    public void IsSameVolumeAs_ComparesOnlyTheVolume(string candidate, string reference, bool expected) =>
        Assert.Equal(expected, SourceVolume.IsSameVolumeAs(candidate, reference));

    [Fact]
    public void IsSameVolumeAs_ReturnsFalse_WhenThePathMakesNoSense()
    {
        Assert.False(SourceVolume.IsSameVolumeAs("   ", @"C:\Windows\System32"));
        Assert.False(SourceVolume.IsSameVolumeAs(@"C:\", "   "));
    }
}
