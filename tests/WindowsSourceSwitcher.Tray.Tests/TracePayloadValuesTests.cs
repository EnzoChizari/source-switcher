using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class TracePayloadValuesTests
{
    [Theory]
    [InlineData("AudioClientStart")]
    [InlineData("AudioClientStop")]
    public void StateEventsDecodeOnlyTheirTwoRequiredFields(string name)
    {
        string[] names = ["UnrelatedComplexPayload", "AudioClientGuidId", "UnusedFormat", "HResult"];
        var reads = new List<string>();
        var values = new TracePayloadValues(names, index =>
        {
            reads.Add(names[index]);
            return names[index] switch
            {
                "AudioClientGuidId" => Guid.Parse("6bca88b9-a21c-458d-8904-236f60803a42"),
                "HResult" => (object)0,
                _ => throw new InvalidOperationException("Unrelated data was decoded.")
            };
        });
        Assert.NotNull(WasapiStreamEventParser.Parse(WasapiStreamEventParser.ProviderId,
            name, 42, DateTimeOffset.UtcNow, values));
        Assert.Equal(new[] { "HResult", "AudioClientGuidId" }, reads);
    }

    [Fact]
    public void MissingFieldsDoNotInvokeThePayloadReader()
    {
        var reads = 0;
        var values = new TracePayloadValues(["HResult"], _ => { reads++; return 0; });
        Assert.False(values.TryGetValue("AudioClientGuidId", out var value));
        Assert.Null(value);
        Assert.False(values.ContainsKey("AudioClientGuidId"));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void FailedApiCallCanBeIgnoredWithoutReadingAClientOrFormat()
    {
        var reads = 0;
        var values = new TracePayloadValues(["HResult", "AudioClientGuidId"], index =>
        {
            reads++;
            return index == 0 ? unchecked((int)0x88890004) : throw new InvalidOperationException();
        });
        Assert.True(values.IsFailedCall());
        Assert.Equal(1, reads);
    }

    [Fact]
    public void UnknownResultTypeDoesNotPretendTheCallFailed()
    {
        var values = new TracePayloadValues(["HResult"], _ => "failed");
        Assert.False(values.IsFailedCall());
    }
}
