using Lingarr.Contracts.Exceptions;

namespace Lingarr.Server.Exceptions;

/// <summary>
/// Raised when an OpenAI-compatible gateway rejects the requested <c>response_format</c> (HTTP 400
/// or 422). The request shape, not the translation itself, is wrong, so the batch client retries the
/// same batch without that field before the caller sees a failure.
/// </summary>
public class TranslationResponseFormatException : TranslationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TranslationResponseFormatException"/> class.
    /// </summary>
    /// <param name="message">Provider error text describing the rejected response format.</param>
    /// <param name="exception">Inner exception that caused the failure, if any.</param>
    public TranslationResponseFormatException(string message, Exception? exception = null)
        : base(message, exception)
    {
    }
}