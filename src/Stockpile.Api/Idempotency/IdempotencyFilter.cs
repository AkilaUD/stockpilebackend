using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Stockpile.Api.Errors;
using Stockpile.Application.Idempotency;
using Stockpile.Contracts.Errors;

namespace Stockpile.Api.Idempotency;

/// <summary>
/// Task 1.27 — resource filter implementing api.md §1.7 and architecture §9.4
/// on every action marked <c>[Idempotent]</c>:
/// <list type="number">
/// <item>No <c>Idempotency-Key</c> header → <c>400 IDEMPOTENCY_KEY_REQUIRED</c>;
/// a non-GUID value → <c>400 MALFORMED_REQUEST</c>.</item>
/// <item>A stored key with the same request hash → the stored status and
/// reference are replayed with <c>Idempotency-Replayed: true</c> — the action
/// never runs again.</item>
/// <item>A stored key with a different hash → <c>409 IDEMPOTENCY_KEY_REUSE</c>.</item>
/// <item>Fresh key → the store runs the action inside the key row's database
/// transaction and stores the response reference + status on success.</item>
/// </list>
/// The action publishes its replay reference (normally the created entity's
/// public id) into <see cref="ReferenceItemsKey"/>; that short value is what
/// replays return, matching the <c>nvarchar(400)</c> reference column and the
/// "persist keys and resulting response references" contract (spec §14).
/// </summary>
public sealed class IdempotencyFilter(
    IIdempotencyStore store,
    IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> jsonOptions) : IAsyncResourceFilter
{
    /// <summary>HttpContext.Items key an [Idempotent] action sets with the short response reference on success.</summary>
    public const string ReferenceItemsKey = "Idempotency.Reference";

    private const string HeaderName = "Idempotency-Key";

    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!context.ActionDescriptor.EndpointMetadata.OfType<IdempotentAttribute>().Any())
        {
            await next();
            return;
        }

        var httpContext = context.HttpContext;
        var cancellationToken = httpContext.RequestAborted;

        if (!TryGetKey(httpContext.Request, out var key, out var missing))
        {
            context.Result = new EmptyResult();
            await WriteEnvelopeAsync(httpContext,
                missing ? ErrorCodes.IdempotencyKeyRequired : ErrorCodes.MalformedRequest, cancellationToken);
            return;
        }

        var ownerId = OwnerOf(httpContext);
        var requestHash = await ComputeRequestHashAsync(httpContext.Request, cancellationToken);

        var outcome = await store.ExecuteAsync(ownerId, key!, requestHash, async token =>
        {
            // The action writes its replay reference while it runs; the status
            // is whatever it set when the result executed.
            httpContext.Items.Remove(ReferenceItemsKey);
            await next();
            return (httpContext.Response.StatusCode, httpContext.Items[ReferenceItemsKey] as string);
        }, cancellationToken);

        switch (outcome.Kind)
        {
            case IdempotencyResultKind.Processed:
                // The action already wrote the response; the key row committed with it.
                break;

            case IdempotencyResultKind.Replayed:
                context.Result = new EmptyResult();
                await WriteReplayAsync(httpContext, outcome, cancellationToken);
                break;

            case IdempotencyResultKind.KeyConflict:
                context.Result = new EmptyResult();
                await WriteEnvelopeAsync(httpContext, ErrorCodes.IdempotencyKeyReuse, cancellationToken);
                break;

            default:
                throw new InvalidOperationException($"Unknown idempotency outcome '{outcome.Kind}'.");
        }
    }

    private async Task WriteReplayAsync(HttpContext httpContext, IdempotencyResult outcome, CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = outcome.Status ?? StatusCodes.Status200OK;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        httpContext.Response.Headers["Idempotency-Replayed"] = "true";

        await httpContext.Response.WriteAsync(
            JsonSerializer.Serialize(new { reference = outcome.Reference, replayed = true }, SerializerOptions(httpContext)),
            cancellationToken);
    }

    private async Task WriteEnvelopeAsync(HttpContext httpContext, string code, CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = ErrorEnvelopeFactory.StatusCodeOf(code);
        httpContext.Response.ContentType = "application/json; charset=utf-8";

        var envelope = ErrorEnvelopeFactory.Create(httpContext, code);
        await httpContext.Response.WriteAsync(
            JsonSerializer.Serialize(envelope, SerializerOptions(httpContext)),
            cancellationToken);
    }

    private System.Text.Json.JsonSerializerOptions SerializerOptions(HttpContext httpContext) =>
        jsonOptions.Value.JsonSerializerOptions;

    /// <summary>
    /// Parses the header: absent or blank behaves as missing (<c>400
    /// IDEMPOTENCY_KEY_REQUIRED</c>); anything not a GUID is malformed
    /// (<c>400 MALFORMED_REQUEST</c>), matching the api.md §1.7 example format.
    /// </summary>
    private static bool TryGetKey(HttpRequest request, out string? key, out bool missing)
    {
        if (!request.Headers.TryGetValue(HeaderName, out var values))
        {
            key = null;
            missing = true;
            return false;
        }

        var value = values.ToString().Trim();
        if (value.Length == 0)
        {
            key = null;
            missing = true;
            return false;
        }

        if (!Guid.TryParse(value, out _))
        {
            key = null;
            missing = false;
            return false;
        }

        key = value;
        missing = false;
        return true;
    }

    /// <summary>
    /// Key scope: the authenticated subject when present, else a stable
    /// per-caller identity derived from the client address. Two callers can
    /// never share a key's replay (architecture §9.4 — "scopes keys per
    /// caller"). SHA-256 truncated to 16 bytes only makes an identity Guid —
    /// never a secret, so the digest choice is not a security primitive.
    /// </summary>
    private static Guid OwnerOf(HttpContext httpContext)
    {
        if (httpContext.User.FindFirst("sub") is { } subject && Guid.TryParse(subject.Value, out var subjectId))
        {
            return subjectId;
        }

        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(ip));
        return new Guid(digest.AsSpan(0, 16));
    }

    /// <summary>
    /// Request hash: SHA-256 of method + path + raw body bytes. The raw (pre-
    /// model-binding) bytes are the contract — clients change the body byte-for-
    /// byte even when the same JSON values deserialize, so binding order and
    /// formatting never affect replay identity. The body is buffered and
    /// rewound so the action's model binding still sees it.
    /// </summary>
    private static async Task<string> ComputeRequestHashAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(request.Method));
        hash.AppendData(Encoding.UTF8.GetBytes("|"));
        hash.AppendData(Encoding.UTF8.GetBytes(request.Path.ToString()));
        hash.AppendData(Encoding.UTF8.GetBytes("|"));

        await using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, cancellationToken);
        request.Body.Position = 0;
        hash.AppendData(body.ToArray());

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
