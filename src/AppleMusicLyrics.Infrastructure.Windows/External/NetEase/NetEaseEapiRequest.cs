// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from MxIris-LyricsX-Project/LyricsKit at
// 6f071990d0c9c6f48d29284db805990bffb912ed,
// Sources/LyricsService/Provider/Services/NetEase.swift (EapiHelper).

using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AppleMusicLyrics.Infrastructure.Windows.External;

internal static class NetEaseEapiRequest
{
    internal const string LyricsUrl = "https://interface3.music.163.com/eapi/song/lyric/v1";
    internal const string LyricsPath = "/api/song/lyric/v1";
    private const string Separator = "-36cd479b6b5-";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static HttpRequestMessage Create(long songId)
    {
        var now = DateTimeOffset.UtcNow;
        var header = new Dictionary<string, string>
        {
            ["__csrf"] = "",
            ["appver"] = "8.0.0",
            ["buildver"] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["channel"] = "",
            ["deviceId"] = "",
            ["mobilename"] = "",
            ["resolution"] = "1920x1080",
            ["os"] = "android",
            ["osver"] = "",
            ["requestId"] = $"{now.ToUnixTimeMilliseconds()}_{Random.Shared.Next(1000):D4}",
            ["versioncode"] = "140",
            ["MUSIC_U"] = "",
        };
        var payload = new Dictionary<string, string>
        {
            ["id"] = songId.ToString(CultureInfo.InvariantCulture),
            ["cp"] = "false",
            ["lv"] = "0",
            ["kv"] = "0",
            ["tv"] = "0",
            ["rv"] = "0",
            ["yv"] = "0",
            ["ytv"] = "0",
            ["yrv"] = "0",
            ["csrf_token"] = "",
            ["header"] = JsonSerializer.Serialize(header, JsonOptions),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, LyricsUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["params"] = Encrypt(LyricsPath, JsonSerializer.Serialize(payload, JsonOptions)),
            }),
        };
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Linux; Android 9; PCT-AL10) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.64 HuaweiBrowser/10.0.3.311 Mobile Safari/537.36");
        request.Headers.Referrer = new Uri("https://music.163.com/");
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", header.Select(pair => $"{pair.Key}={pair.Value}")));
        return request;
    }

    internal static string Encrypt(string apiPath, string payloadJson)
    {
        var digest = Convert.ToHexString(MD5.HashData(
            Encoding.UTF8.GetBytes($"nobody{apiPath}use{payloadJson}md5forencrypt"))).ToLowerInvariant();
        var plaintext = Encoding.UTF8.GetBytes($"{apiPath}{Separator}{payloadJson}{Separator}{digest}");
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes("e82ckenh8dichen8");
        return Convert.ToHexString(aes.EncryptEcb(plaintext, PaddingMode.PKCS7));
    }
}
