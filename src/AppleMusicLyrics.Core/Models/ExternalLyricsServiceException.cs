using System.Net.Http;

namespace AppleMusicLyrics.Core.Models;

/// <summary>A successful HTTP response carrying a provider-specific failure code.</summary>
public sealed class ExternalLyricsServiceException(string source, int serviceCode)
    : HttpRequestException($"{source} returned service code {serviceCode}.")
{
    public int ServiceCode { get; } = serviceCode;
}
