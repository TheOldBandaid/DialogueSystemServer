using System.Security.Cryptography;
using System.Text.Json;

namespace DialogueSystem.Application;

public static class EditVersions
{
    public static string Line(int speakerId, string text, bool isEnding, bool isStoryBranch) =>
        Hash(new { speakerId, text, isEnding, isStoryBranch });

    public static string Choice(int targetLineId, string text, int position, string? requiredState, string? nextState) =>
        Hash(new { targetLineId, text, position, requiredState, nextState });

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
