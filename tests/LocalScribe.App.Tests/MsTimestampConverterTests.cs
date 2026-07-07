// tests/LocalScribe.App.Tests/MsTimestampConverterTests.cs
using System.Globalization;
using LocalScribe.App;
using Xunit;

public class MsTimestampConverterTests
{
    [Fact]
    public void Formats_Hundredths()
    {
        var c = new MsTimestampConverter();
        Assert.Equal("00:15.92", c.Convert(15920L, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Parses_BackTo10msGrid()
    {
        var c = new MsTimestampConverter();
        var ms = (long)c.ConvertBack("00:15.92", typeof(long), null!, CultureInfo.InvariantCulture);
        Assert.Equal(15920, ms);
        Assert.Equal(0, ms % 10);
    }
}
