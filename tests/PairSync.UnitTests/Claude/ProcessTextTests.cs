using System.Text;
using PairSync.Application.Claude;

namespace PairSync.UnitTests.Claude;

public sealed class ProcessTextTests
{
    private static string AsLatin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    [Fact]
    public void Utf8_output_keeps_its_umlauts()
    {
        Assert.Equal("Grüße aus Köln ✓", ProcessText.Line(AsLatin1(Encoding.UTF8.GetBytes("Grüße aus Köln ✓"))));
    }

    [Fact]
    public void Oem_output_of_windows_tools_is_decoded_with_the_oem_code_page()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "OEM code pages exist on Windows only");
        var oem = CodePagesEncodingProvider.Instance.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage)!;

        Assert.Equal("Installation erfolgreich: Größe geprüft", ProcessText.Line(AsLatin1(oem.GetBytes("Installation erfolgreich: Größe geprüft"))));
    }

    [Theory]
    [InlineData("\u001b[32mDone\u001b[0m", "Done")]
    [InlineData("  10%\r  55%\r 100% fertig", " 100% fertig")]
    [InlineData("abc\b\bX", "aX")]
    [InlineData("   ", null)]
    [InlineData(" \\ ", null)]
    [InlineData("  ██████▒▒▒▒  1.00 MB / 2.00 MB", null)]
    public void Terminal_only_output_is_cleaned(string input, string? expected) => Assert.Equal(expected, ProcessText.Clean(input));
}
