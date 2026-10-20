namespace Lingarr.Server.Services.Translation.OpenAiCompatible;

/// <summary>
/// Response format requested for a batch translation. Gateways degrade in this order: strict JSON
/// schema, plain JSON object, and finally no <c>response_format</c> at all.
/// </summary>
public enum BatchResponseFormatMode
{
    JsonSchema,
    JsonObject,
    None
}

/// <summary>
/// Remembers how one provider/model answered batch requests and downgrades the requested response
/// format only after repeated rejections. One instance belongs to one completion client, which is
/// one provider/model for one translation run, so a new run probes <c>json_schema</c> again instead
/// of inheriting a stale capability decision.
/// </summary>
public sealed class BatchResponseFormatPolicy
{
    /// <summary>
    /// Number of consecutive <c>json_schema</c> rejections before the policy tries
    /// <c>json_object</c>. A single rejection can be a transient upstream routing problem.
    /// </summary>
    public const int JsonSchemaRejectionLimit = 3;

    private readonly object _gate = new();
    private BatchResponseFormatMode _mode = BatchResponseFormatMode.JsonSchema;
    private int _consecutiveRejections;

    /// <summary>Response format the next batch request should use.</summary>
    public BatchResponseFormatMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _mode;
            }
        }
    }

    /// <summary>Clears the consecutive rejection count after a batch succeeded.</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveRejections = 0;
        }
    }

    /// <summary>
    /// Records that <paramref name="attemptedMode"/> was rejected by the provider and the plain
    /// JSON request for the same batch succeeded. Returns the new mode when the policy downgraded,
    /// otherwise <c>null</c>. The plain request succeeding is the evidence that the response format,
    /// not the rest of the request body, caused the rejection.
    /// </summary>
    public BatchResponseFormatMode? RecordFormatRejection(BatchResponseFormatMode attemptedMode)
    {
        lock (_gate)
        {
            // Another request already moved the policy on; do not count an old attempt twice.
            if (attemptedMode != _mode)
            {
                return null;
            }

            if (_mode == BatchResponseFormatMode.JsonSchema)
            {
                _consecutiveRejections++;
                if (_consecutiveRejections < JsonSchemaRejectionLimit)
                {
                    return null;
                }

                _mode = BatchResponseFormatMode.JsonObject;
                _consecutiveRejections = 0;
                return _mode;
            }

            if (_mode == BatchResponseFormatMode.JsonObject)
            {
                _mode = BatchResponseFormatMode.None;
                _consecutiveRejections = 0;
                return _mode;
            }

            return null;
        }
    }
}