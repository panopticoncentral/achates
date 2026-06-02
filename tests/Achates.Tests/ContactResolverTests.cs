using Achates.Server.Tools;

namespace Achates.Tests;

public class ContactResolverTests
{
    [Fact]
    public void PhoneKey_CollapsesCountryCodeAndFormatting()
    {
        // An iMessage handle carries the country code; contacts often don't.
        // All three must resolve to the same lookup key.
        var e164 = ContactResolver.PhoneKey("+15553411515");
        var bare = ContactResolver.PhoneKey("5553411515");
        var formatted = ContactResolver.PhoneKey("(555) 341-1515");

        Assert.Equal("5553411515", e164);
        Assert.Equal("5553411515", bare);
        Assert.Equal("5553411515", formatted);
    }

    [Fact]
    public void PhoneKey_KeepsShortCodesWhole()
    {
        // Short codes (< 10 digits) must not be truncated.
        Assert.Equal("262966", ContactResolver.PhoneKey("262966"));
        Assert.Equal("911", ContactResolver.PhoneKey("911"));
    }

    [Fact]
    public void PhoneKey_TakesLastTenDigitsForLongNumbers()
    {
        // A number with extra leading digits keys on its last 10.
        Assert.Equal("5553411515", ContactResolver.PhoneKey("+44 1 5553411515"));
    }
}
