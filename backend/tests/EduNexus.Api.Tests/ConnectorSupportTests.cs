using System.Security.Cryptography;
using EduNexus.Api.Connectors;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EduNexus.Api.Tests;

public sealed class SecretBoxTests
{
    private static SecretBox Box(string? key) => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [SecretBox.KeyConfig] = key }).Build());
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Round_Trips_And_Never_Contains_The_Plain_Value()
    {
        var box = Box(NewKey());
        var sealedValue = box.Seal("my-lms-token", "t:c");
        Assert.DoesNotContain("my-lms-token", sealedValue);
        Assert.NotEqual(sealedValue, box.Seal("my-lms-token", "t:c"));   // a fresh nonce each time
        Assert.Equal("my-lms-token", box.Open(sealedValue, "t:c"));
    }

    [Fact]
    public void A_Sealed_Value_Cannot_Be_Moved_Changed_Or_Opened_With_Another_Key()
    {
        var key = NewKey();
        var box = Box(key);
        var sealedValue = box.Seal("secret", "tenant1:connector1");
        Assert.ThrowsAny<CryptographicException>(() => box.Open(sealedValue, "tenant2:connector1"));   // another tenant
        Assert.ThrowsAny<CryptographicException>(() => box.Open(sealedValue, "tenant1:connector2"));   // another connector
        Assert.ThrowsAny<CryptographicException>(() => Box(NewKey()).Open(sealedValue, "tenant1:connector1"));
        var bytes = Convert.FromBase64String(sealedValue[3..]);
        bytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => box.Open("v1." + Convert.ToBase64String(bytes), "tenant1:connector1"));
        Assert.ThrowsAny<CryptographicException>(() => box.Open("plain text", "tenant1:connector1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("AAAA")]   // valid base64 but not 32 bytes
    public void Without_A_Proper_Key_It_Is_Not_Configured_And_Refuses_To_Seal(string? key)
    {
        var box = Box(key);
        Assert.False(box.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => box.Seal("x", "c"));
    }
}

public sealed class CourseCsvTests
{
    [Fact]
    public void Reads_Quoted_Cells_Optional_Columns_And_A_Byte_Order_Mark()
    {
        var csv = "﻿Code,Title,Term,StartsOn,EndsOn,Enrolled,Instructors,ExternalId\r\n" +
                  "ENG101,\"English, Composition\",Fall 2026,2026-09-01,2026-12-20,42,\"Layla Haddad; Omar Saleh\",E-1\r\n" +
                  "BIO200,\"The \"\"Origin\"\" of\nSpecies\",,,,,,\r\n\r\n";
        var r = CourseCsv.Parse(csv);
        Assert.Empty(r.Errors);
        Assert.Equal(2, r.Courses.Count);
        var a = r.Courses[0];
        Assert.Equal(("E-1", "ENG101", "English, Composition", "Fall 2026", 42), (a.ExternalId, a.Code, a.Title, a.Term, a.EnrolledCount));
        Assert.Equal(["Layla Haddad", "Omar Saleh"], a.Instructors);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), a.StartsOn);
        var b = r.Courses[1];
        Assert.Equal("BIO200", b.ExternalId);                       // defaults to the code
        Assert.Equal("The \"Origin\" of\nSpecies", b.Title);
        Assert.Null(b.Term); Assert.Null(b.EnrolledCount); Assert.Empty(b.Instructors);
    }

    [Fact]
    public void One_Bad_Row_Rejects_The_Whole_File_With_Line_Numbers()
    {
        var r = CourseCsv.Parse("code,title,startsOn,enrolled\nA1,Alpha,2026-13-45,5\nA2,,2026-09-01,x\nA1,Again,2026-09-01,3\nA4,Fine,2026-09-01,-2\n");
        Assert.Contains("Row 2: startsOn must be yyyy-MM-dd.", r.Errors);
        Assert.Contains("Row 3: code and title are required.", r.Errors);
        Assert.Contains("Row 4: 'A1' appears twice.", r.Errors);
        Assert.Contains("Row 5: enrolled must be a whole number.", r.Errors);   // negative numbers are not accepted
    }

    [Theory]
    [InlineData("", "The file is empty.")]
    [InlineData("name,description\nx,y\n", "The first row must name the columns, including 'code' and 'title'.")]
    public void A_File_Without_The_Needed_Columns_Is_Rejected(string csv, string message)
    {
        Assert.Equal(message, Assert.Single(CourseCsv.Parse(csv).Errors));
    }
}
