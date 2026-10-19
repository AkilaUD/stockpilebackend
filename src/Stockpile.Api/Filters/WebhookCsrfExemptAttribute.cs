namespace Stockpile.Api.Filters;

/// <summary>
/// Marks a server-to-server webhook action as exempt from the double-submit CSRF
/// check (<see cref="AntiforgeryEnvelopeFilter"/>). A webhook is called by a
/// payment provider, not a browser: there is no session cookie to forge and no
/// antiforgery cookie to echo, and the caller proves itself with an HMAC signature
/// over the raw body, which the use case verifies before touching any state.
/// <para>
/// Reserved for routes under <c>webhooks/</c>; an architecture test fails the build
/// if it appears anywhere else, so it can never become a general-purpose opt-out.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class WebhookCsrfExemptAttribute : Attribute;
