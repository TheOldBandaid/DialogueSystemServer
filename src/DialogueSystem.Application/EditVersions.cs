using System.Security.Cryptography;
using System.Text.Json;

namespace DialogueSystem.Application;

public static class EditVersions
{
    public static string AccountRole(int id, string role, bool mustChangePassword) => Hash(new { kind = "account-role", id, role, mustChangePassword });
    public static string Dialogue(int id, long revision) => Hash(new { kind = "dialogue", id, revision });
    public static string DialogueDeletion(int id, long revision, long activityRevision) => Hash(new { kind = "dialogue-delete", id, revision, activityRevision });
    public static string Character(int id, long revision) => Hash(new { kind = "character", id, revision });
    public static string LineDeletion(int id, string version, long revision) => Hash(new { kind = "line-delete", id, version, revision });
    public static string Line(int speakerId, string text, bool isEnding, bool isStoryBranch) =>
        Hash(new { speakerId, text, isEnding, isStoryBranch });

    public static string Choice(int targetLineId, string text, int position, string? requiredState, string? nextState) =>
        Hash(new { targetLineId, text, position, requiredState, nextState });

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
