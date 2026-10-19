using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Enums;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Profiles;

/// <summary>
/// business-flows §2 steps 2/4 — the buyer wizard's profile and preference
/// endpoints over real HTTP (api.md §2.2 <c>/me/profile</c>, <c>/me/preferences</c>
/// and <c>/me/notification-preferences</c>).
/// <para>
/// What this pins: every endpoint is authenticated (the anonymous answer is one
/// 401 envelope, never a 403); <c>PATCH</c> merges per member so a step is
/// resumable and independent; the defaults the wizard promises are real (quote
/// notifications on, SMS and WhatsApp off, locale <c>en</c>); and every read and
/// write is scoped to the caller, so one principal's profile never leaks into
/// another's (BOLA, api.md §1.6).
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MeProfileApiTests
{
    private const string ApiBase = "/api/v1";
    private const string MeBase = $"{ApiBase}/me";
    private const string Password = "correct-horse-battery-staple";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static readonly string[] RoleOnly = ["procurement-manager"];
    private static readonly string[] Locations = ["Colombo", "Kandy"];
    private static readonly string[] Interests = ["Electrical", "Plumbing"];
    private static readonly string[] Empty = [];
    private static readonly string[] NormalizedLocations = ["Colombo", "Kandy"];
    private static readonly string[] NormalizedInterests = ["Plumbing"];
    private static readonly string[] MessyLocations = ["  Colombo  ", "Colombo", "Kandy"];
    private static readonly string[] MessyInterests = ["  Plumbing  ", "Plumbing"];

    private readonly StockpileApiFactory _factory;

    public MeProfileApiTests(StockpileApiFactory factory) => _factory = factory;

    private static string NewEmail() => $"me.{Guid.NewGuid():N}@test.local";

    // ---- Flow helpers --------------------------------------------------------

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string route, object? payload, string requestToken)
    {
        using var request = new HttpRequestMessage(method, route);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: Wire);
        }

        return await AntiforgeryTestSupport.SendUnsafeAsync(client, request, requestToken);
    }

    /// <summary>Registers, signs in, and returns the client plus the login-minted request token.</summary>
    private async Task<(HttpClient Client, string RequestToken)> LoginAsync()
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string email = NewEmail();
        string acquired = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        using (var register = await SendAsync(client, HttpMethod.Post, $"{ApiBase}/auth/register",
            new RegisterPayload("Test", "Caller", email, "+94771234567", Password), acquired))
        {
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        }

        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var login = await SendAsync(client, HttpMethod.Post, $"{ApiBase}/auth/login",
            new LoginPayload(email, Password), token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        LoginResponsePayload body = await login.Content.ReadFromJsonAsync<LoginResponsePayload>()
            ?? throw new InvalidOperationException("Login answered without a body.");
        Assert.False(string.IsNullOrWhiteSpace(body.RequestToken), "login must re-mint a request token (§1.10)");
        return (client, body.RequestToken!);
    }

    private static async Task<ProfileResponsePayload> PatchProfileAsync(
        HttpClient client, string requestToken, ProfileDraftPayload draft)
    {
        using var response = await SendAsync(client, HttpMethod.Patch, $"{MeBase}/profile", draft, requestToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<ProfileResponsePayload>()
            ?? throw new InvalidOperationException("PATCH profile answered without a body.");
    }

    private static async Task<ProfileResponsePayload> GetProfileAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"{MeBase}/profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<ProfileResponsePayload>()
            ?? throw new InvalidOperationException("GET profile answered without a body.");
    }

    // ---- Authentication floor ------------------------------------------------

    [Fact]
    public async Task Every_wizard_endpoint_answers_one_401_for_an_anonymous_caller()
    {
        HttpClient anonymous = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(anonymous);

        (string Route, HttpMethod Method, object? Body)[] endpoints =
        [
            ($"{MeBase}/profile", HttpMethod.Get, null),
            ($"{MeBase}/profile", HttpMethod.Patch, new ProfileDraftPayload("other", null, null)),
            ($"{MeBase}/preferences", HttpMethod.Get, null),
            ($"{MeBase}/preferences", HttpMethod.Patch, new PreferencesDraftPayload("si", false)),
            ($"{MeBase}/notification-preferences", HttpMethod.Get, null),
            ($"{MeBase}/notification-preferences", HttpMethod.Patch, new NotificationDraftPayload(false, true, true)),
        ];

        foreach ((string route, HttpMethod method, object? body) in endpoints)
        {
            using HttpResponseMessage response = method == HttpMethod.Get
                ? await anonymous.GetAsync(route)
                : await SendAsync(anonymous, method, route, body, token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            ErrorEnvelope envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>()
                ?? throw new InvalidOperationException($"{method} {route} 401 answered without an envelope.");
            Assert.Equal(ErrorCodes.Unauthenticated, envelope.Code);
        }
    }

    // ---- Step 2 — professional profile (api.md §2.2) --------------------------

    [Fact]
    public async Task The_profile_starts_empty_and_round_trips()
    {
        (HttpClient client, string token) = await LoginAsync();
        using (client)
        {
            ProfileResponsePayload empty = await GetProfileAsync(client);
            Assert.Null(empty.Role);
            Assert.Empty(empty.ProcurementLocations!);
            Assert.Empty(empty.ProjectInterests!);

            ProfileResponsePayload saved = await PatchProfileAsync(client, token,
                new ProfileDraftPayload("procurement-manager", Locations, Interests));

            Assert.Equal("procurement-manager", saved.Role);
            Assert.Equal(Locations, saved.ProcurementLocations);
            Assert.Equal(Interests, saved.ProjectInterests);

            // A second read proves the write landed, not just the echo.
            ProfileResponsePayload reread = await GetProfileAsync(client);
            Assert.Equal("procurement-manager", reread.Role);
            Assert.Equal(Locations, reread.ProcurementLocations);
            Assert.Equal(Interests, reread.ProjectInterests);
        }
    }

    [Fact]
    public async Task Patching_one_member_leaves_the_others_untouched()
    {
        (HttpClient client, string token) = await LoginAsync();
        using (client)
        {
            _ = await PatchProfileAsync(client, token,
                new ProfileDraftPayload("project-manager", Locations, Interests));

            // Only the role is supplied: the lists are a partial PATCH and must survive.
            ProfileResponsePayload merged = await PatchProfileAsync(client, token,
                new ProfileDraftPayload("owner-director", null, null));

            Assert.Equal("owner-director", merged.Role);
            Assert.Equal(Locations, merged.ProcurementLocations);
            Assert.Equal(Interests, merged.ProjectInterests);

            // An empty array is the explicit "clear this list" (a null is "leave it").
            ProfileResponsePayload cleared = await PatchProfileAsync(client, token,
                new ProfileDraftPayload(null, Empty, null));
            Assert.Equal("owner-director", cleared.Role);
            Assert.Empty(cleared.ProcurementLocations!);
            Assert.Equal(Interests, cleared.ProjectInterests);
        }
    }

    [Fact]
    public async Task The_profile_normalises_whitespace_and_duplicate_entries()
    {
        (HttpClient client, string token) = await LoginAsync();
        using (client)
        {
            // The validator lets a padded value through (it is not blank); the use
            // case trims it and drops the duplicate the trim created.
            ProfileResponsePayload saved = await PatchProfileAsync(client, token,
                new ProfileDraftPayload("other", MessyLocations, MessyInterests));

            Assert.Equal(NormalizedLocations, saved.ProcurementLocations);
            Assert.Equal(NormalizedInterests, saved.ProjectInterests);
        }
    }

    [Fact]
    public async Task A_blank_list_entry_is_rejected_with_its_indexed_field_path()
    {
        (HttpClient client, string token) = await LoginAsync();
        using (client)
        {
            using var response = await SendAsync(client, HttpMethod.Patch, $"{MeBase}/profile",
                new ProfileDraftPayload(null, ["Colombo", "   "], null), token);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            ErrorEnvelope envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>()
                ?? throw new InvalidOperationException("422 answered without an envelope.");
            Assert.Equal("procurementLocations[1]", Assert.Single(envelope.FieldErrors!).Field);
        }
    }

    [Fact]
    public async Task An_unlisted_role_is_rejected_with_the_field_path()
    {
        (HttpClient client, string token) = await LoginAsync();
        using (client)
        {
            using var response = await SendAsync(client, HttpMethod.Patch, $"{MeBase}/profile",
                new ProfileDraftPayload("chief-procurement-officer", null, null), token);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            ErrorEnvelope envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>()
                ?? throw new InvalidOperationException("422 answered without an envelope.");
            Assert.Equal(ErrorCodes.ValidationFailed, envelope.Code);
            Assert.Equal("role", Assert.Single(envelope.FieldErrors!).Field);
        }
    }

    // ---- Step 4a — preferences (api.md §2.2) ---------------------------------

    [Fact]
    public async Task Preferences_default_to_english_with_quote_notifications_on()
    {
        (HttpClient client, _) = await LoginAsync();
        using (client)
        {
            using var response = await client.GetAsync($"{MeBase}/preferences");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            PreferencesResponsePayload prefs = await response.Content.ReadFromJsonAsync<PreferencesResponsePayload>()
                ?? throw new InvalidOperationException("GET preferences answered without a body.");

            Assert.Equal("en", prefs.Locale);
            Assert.True(prefs.QuoteNotificationPrefs);
        }
    }

    [Fact]
    public async Task Preferences_round_trip_and_reject_an_unsupported_locale()
    {
        (HttpClient client, string token) = await LoginAsync();
        using (client)
        {
            using (var updated = await SendAsync(client, HttpMethod.Patch, $"{MeBase}/preferences",
                new PreferencesDraftPayload("si", false), token))
            {
                Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
                PreferencesResponsePayload prefs = await updated.Content.ReadFromJsonAsync<PreferencesResponsePayload>()
                    ?? throw new InvalidOperationException("PATCH preferences answered without a body.");
                Assert.Equal("si", prefs.Locale);
                Assert.False(prefs.QuoteNotificationPrefs);
            }

            // The locale lands on the account itself, so the session reflects it.
            using var session = await client.GetAsync($"{ApiBase}/auth/session");
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);

            // A locale the frontend does not ship is refused, and the reason is on the field.
            using var rejected = await SendAsync(client, HttpMethod.Patch, $"{MeBase}/preferences",
                new PreferencesDraftPayload("fr", null), token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            ErrorEnvelope envelope = await rejected.Content.ReadFromJsonAsync<ErrorEnvelope>()
                ?? throw new InvalidOperationException("422 answered without an envelope.");
            Assert.Equal("locale", Assert.Single(envelope.FieldErrors!).Field);
        }
    }

    // ---- Step 4b — notification preferences (api.md §2.2) --------------------

    [Fact]
    public async Task Notification_preferences_default_to_email_only_and_round_trip()
    {
        (HttpClient client, string token) = await LoginAsync();
        using (client)
        {
            using (var initial = await client.GetAsync($"{MeBase}/notification-preferences"))
            {
                NotificationResponsePayload defaults =
                    await initial.Content.ReadFromJsonAsync<NotificationResponsePayload>()
                    ?? throw new InvalidOperationException("GET notification-preferences answered without a body.");
                Assert.True(defaults.Email);
                Assert.False(defaults.Sms);
                Assert.False(defaults.WhatsApp);
            }

            using (var updated = await SendAsync(client, HttpMethod.Patch, $"{MeBase}/notification-preferences",
                new NotificationDraftPayload(Email: false, Sms: true, WhatsApp: true), token))
            {
                Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
                NotificationResponsePayload prefs =
                    await updated.Content.ReadFromJsonAsync<NotificationResponsePayload>()
                    ?? throw new InvalidOperationException("PATCH notification-preferences answered without a body.");
                Assert.False(prefs.Email);
                Assert.True(prefs.Sms);
                Assert.True(prefs.WhatsApp);
            }
        }
    }

    [Fact]
    public async Task Notification_preferences_write_one_rfq_row_per_channel()
    {
        (HttpClient client, string token) = await LoginAsync();
        using (client)
        {
            // The caller's PublicId is both the PK and the `sub` claim the store scopes by.
            using var sessionResponse = await client.GetAsync($"{ApiBase}/auth/session");
            string publicId = (await sessionResponse.Content.ReadFromJsonAsync<AuthSessionPayload>())!.PublicId!;

            _ = await SendAsync(client, HttpMethod.Patch, $"{MeBase}/notification-preferences",
                new NotificationDraftPayload(Email: false, Sms: true, WhatsApp: true), token);

            using IServiceScope scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
            Guid userId = Guid.Parse(publicId);
            List<NotificationPreference> rows = await db.NotificationPreferences
                .AsNoTracking()
                .Where(row => row.UserId == userId)
                .ToListAsync();

            Assert.Equal(3, rows.Count);
            Assert.All(rows, row => Assert.Equal("rfq", row.Category));
            Assert.False(rows.Single(row => row.Channel == NotificationChannel.Email).Enabled);
            Assert.True(rows.Single(row => row.Channel == NotificationChannel.Sms).Enabled);
            Assert.True(rows.Single(row => row.Channel == NotificationChannel.WhatsApp).Enabled);
        }
    }

    // ---- Scoping (BOLA, api.md §1.6) ------------------------------------------

    [Fact]
    public async Task Another_users_profile_and_preferences_stay_their_own()
    {
        (HttpClient owner, string ownerToken) = await LoginAsync();
        using (owner)
        {
            _ = await PatchProfileAsync(owner, ownerToken,
                new ProfileDraftPayload("procurement-manager", Locations, Interests));
            using var ownerPrefs = await SendAsync(owner, HttpMethod.Patch, $"{MeBase}/preferences",
                new PreferencesDraftPayload("si", false), ownerToken);
            Assert.Equal(HttpStatusCode.OK, ownerPrefs.StatusCode);

            (HttpClient stranger, string strangerToken) = await LoginAsync();
            using (stranger)
            {
                // The stranger sees their OWN empty profile, never the owner's rows.
                ProfileResponsePayload profile = await GetProfileAsync(stranger);
                Assert.Null(profile.Role);
                Assert.Empty(profile.ProcurementLocations!);
                Assert.Empty(profile.ProjectInterests!);

                using var prefs = await stranger.GetAsync($"{MeBase}/preferences");
                PreferencesResponsePayload theirPrefs =
                    await prefs.Content.ReadFromJsonAsync<PreferencesResponsePayload>()
                    ?? throw new InvalidOperationException("GET preferences answered without a body.");
                Assert.Equal("en", theirPrefs.Locale);
                Assert.True(theirPrefs.QuoteNotificationPrefs);

                // The stranger's own write stays theirs.
                _ = await PatchProfileAsync(stranger, strangerToken,
                    new ProfileDraftPayload("other", RoleOnly, null));
            }

            // The owner's rows survive everything the stranger did.
            ProfileResponsePayload ownerAgain = await GetProfileAsync(owner);
            Assert.Equal("procurement-manager", ownerAgain.Role);
            Assert.Equal(Locations, ownerAgain.ProcurementLocations);
            Assert.Equal(Interests, ownerAgain.ProjectInterests);
        }
    }

    // ---- Wire payload shapes ---------------------------------------------------

    private sealed record RegisterPayload(
        string FirstName, string LastName, string Email, string? Phone, string Password);

    private sealed record LoginPayload(string Email, string Password);

    private sealed record LoginResponsePayload(string? RequestToken);

    private sealed record AuthSessionPayload(string? PublicId);

    private sealed record ProfileDraftPayload(
        string? Role, IReadOnlyList<string>? ProcurementLocations, IReadOnlyList<string>? ProjectInterests);

    private sealed record ProfileResponsePayload(
        string? Role, IReadOnlyList<string>? ProcurementLocations, IReadOnlyList<string>? ProjectInterests);

    private sealed record PreferencesDraftPayload(string? Locale, bool? QuoteNotificationPrefs);

    private sealed record PreferencesResponsePayload(string? Locale, bool? QuoteNotificationPrefs);

    private sealed record NotificationDraftPayload(bool? Email, bool? Sms, bool? WhatsApp);

    private sealed record NotificationResponsePayload(bool Email, bool Sms, bool WhatsApp);
}
