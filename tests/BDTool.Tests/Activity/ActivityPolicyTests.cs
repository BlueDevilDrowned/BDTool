using System;
using System.IO;
using BDTool.Activity;
using BDTool.Core.Activity;

namespace BDTool.Tests.Activity;

/// <summary>冻结的 Activity 路径、阈值与脱敏规则。</summary>
public sealed class ActivityPolicyTests
{
    [Fact]
    public void FilePath_UsesUtcDayAndFrozenLayout()
    {
        var path = ActivityPaths.GetActivityFilePath(
            @"C:\project",
            "abc123",
            new DateTimeOffset(2026, 11, 26, 10, 30, 0, TimeSpan.Zero));

        Assert.Equal(
            Path.Combine(@"C:\project", ".bdtool", "state", "activity", "20261126", "abc123.json"),
            path);
    }

    [Fact]
    public void FilePath_ConvertsOffsetToUtcDay()
    {
        // 2026-11-27 02:00 +08:00 等于 UTC 2026-11-26 18:00。
        var path = ActivityPaths.GetActivityFilePath(
            @"C:\project",
            "abc123",
            new DateTimeOffset(2026, 11, 27, 2, 0, 0, TimeSpan.FromHours(8)));

        Assert.Contains(Path.Combine("activity", "20261126"), path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("bad id")]
    [InlineData("a.b")]
    public void InvalidActivityIds_AreRejected(string activityId)
    {
        Assert.False(ActivityPaths.IsValidActivityId(activityId, out _));
        Assert.Throws<ArgumentException>(() =>
            ActivityPaths.GetActivityFilePath(@"C:\project", activityId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void LongActivityId_IsRejected()
    {
        var activityId = new string('a', ActivityPaths.MaxActivityIdLength + 1);

        Assert.False(ActivityPaths.IsValidActivityId(activityId, out _));
    }

    [Fact]
    public void ValidActivityIds_AreAccepted()
    {
        Assert.True(ActivityPaths.IsValidActivityId(Guid.NewGuid().ToString("n"), out _));
        Assert.True(ActivityPaths.IsValidActivityId("act-2026_11", out _));
    }

    [Fact]
    public void PolicyConstants_AreFrozen()
    {
        Assert.Equal(64 * 1024, ActivityPolicy.LargeContentThresholdBytes);
        Assert.Equal(30, ActivityPolicy.DefaultRetentionDays);
        Assert.Equal(".bdtool", ActivityPolicy.StateDirectoryName);
    }

    [Fact]
    public void SensitiveFields_AreRecognizedCaseInsensitively()
    {
        Assert.True(ActivityPolicy.IsSensitiveField("Authorization"));
        Assert.True(ActivityPolicy.IsSensitiveField("api_key"));
        Assert.True(ActivityPolicy.IsSensitiveField("memberToken"));
        Assert.True(ActivityPolicy.IsSensitiveField("clientSecret"));
        Assert.False(ActivityPolicy.IsSensitiveField("projectPath"));
    }

    [Fact]
    public void Summarize_RedactsSensitiveValuesButKeepsLength()
    {
        var summary = ActivityContentPolicy.Summarize(
            """{"token":"abcd1234","nested":{"password":"p"},"keep":"v"}""",
            "input");

        Assert.True(summary.Redacted);
        Assert.False(summary.Truncated);
        Assert.False(summary.ArtifactRequired);
        Assert.DoesNotContain("abcd1234", summary.PreviewJson, StringComparison.Ordinal);
        Assert.Contains("$redacted", summary.PreviewJson, StringComparison.Ordinal);
        Assert.Contains("\"keep\":\"v\"", summary.PreviewJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Summarize_RequiresArtifactAboveThresholdOnly()
    {
        var atLimit = new string('a', ActivityPolicy.LargeContentThresholdBytes);
        var aboveLimit = new string('a', ActivityPolicy.LargeContentThresholdBytes + 1);

        Assert.False(ActivityContentPolicy.Summarize(atLimit, "input").ArtifactRequired);
        var artifact = ActivityContentPolicy.Summarize(aboveLimit, "input");
        Assert.True(artifact.ArtifactRequired);
        Assert.Contains("$artifact", artifact.PreviewJson, StringComparison.Ordinal);
        Assert.Equal(ActivityPolicy.LargeContentThresholdBytes + 1, artifact.OriginalBytes);
    }

    [Fact]
    public void Summarize_ProducesValidJsonForPlainText()
    {
        var summary = ActivityContentPolicy.Summarize("hello world", "output");
        using var document = System.Text.Json.JsonDocument.Parse(summary.PreviewJson);

        Assert.Equal(System.Text.Json.JsonValueKind.String, document.RootElement.ValueKind);
    }
}
