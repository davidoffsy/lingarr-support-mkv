using Lingarr.Server.Services.Translation.OpenAiCompatible;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation.OpenAiCompatible;

public class BatchResponseFormatPolicyTests
{
    [Fact]
    public void Starts_with_json_schema()
    {
        var policy = new BatchResponseFormatPolicy();

        Assert.Equal(BatchResponseFormatMode.JsonSchema, policy.Mode);
    }

    [Fact]
    public void Downgrades_to_json_object_after_three_consecutive_rejections()
    {
        var policy = new BatchResponseFormatPolicy();

        Assert.Null(policy.RecordFormatRejection(BatchResponseFormatMode.JsonSchema));
        Assert.Null(policy.RecordFormatRejection(BatchResponseFormatMode.JsonSchema));
        Assert.Equal(
            BatchResponseFormatMode.JsonObject,
            policy.RecordFormatRejection(BatchResponseFormatMode.JsonSchema));
        Assert.Equal(BatchResponseFormatMode.JsonObject, policy.Mode);
    }

    [Fact]
    public void Success_resets_the_consecutive_rejection_count()
    {
        var policy = new BatchResponseFormatPolicy();
        policy.RecordFormatRejection(BatchResponseFormatMode.JsonSchema);
        policy.RecordFormatRejection(BatchResponseFormatMode.JsonSchema);

        policy.RecordSuccess();

        Assert.Null(policy.RecordFormatRejection(BatchResponseFormatMode.JsonSchema));
        Assert.Null(policy.RecordFormatRejection(BatchResponseFormatMode.JsonSchema));
        Assert.Equal(BatchResponseFormatMode.JsonSchema, policy.Mode);
    }

    [Fact]
    public void Json_object_rejection_disables_response_format()
    {
        var policy = new BatchResponseFormatPolicy();
        MoveToJsonObject(policy);

        Assert.Equal(
            BatchResponseFormatMode.None,
            policy.RecordFormatRejection(BatchResponseFormatMode.JsonObject));
        Assert.Equal(BatchResponseFormatMode.None, policy.Mode);
    }

    [Fact]
    public void Stale_attempt_does_not_advance_the_policy()
    {
        var policy = new BatchResponseFormatPolicy();
        MoveToJsonObject(policy);

        Assert.Null(policy.RecordFormatRejection(BatchResponseFormatMode.JsonSchema));
        Assert.Equal(BatchResponseFormatMode.JsonObject, policy.Mode);
    }

    private static void MoveToJsonObject(BatchResponseFormatPolicy policy)
    {
        policy.RecordFormatRejection(BatchResponseFormatMode.JsonSchema);
        policy.RecordFormatRejection(BatchResponseFormatMode.JsonSchema);
        policy.RecordFormatRejection(BatchResponseFormatMode.JsonSchema);
    }
}