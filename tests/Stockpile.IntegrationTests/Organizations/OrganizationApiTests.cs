using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Organizations;

/// <summary>
/// Task 1.43 — organisation, member and address use cases over real HTTP
/// (implementation-plan 1.43; business-flows §2 step 3; security-test-matrix
/// P9, F3; api.md §2.1/§2.2). Proves the wizard's atomic step 3, that team
/// invitations land Pending (T6.4) and grant nothing until accepted, the P9
/// role-scope gate, the layer-3/4 BOLA binding (a capability holder who is not
/// a member of the target organisation gets the same 404 as a nonexistent
/// organisation), and the owner-scoped address book.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OrganizationApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (single migrate + seed of Stockpile_Test).</summary>
    public OrganizationApiTests(StockpileApiFactory factory) => _factory = factory;

    private const string Password = "correct-horse-battery-staple";
    private const string ApiBase = "/api/v1";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static string NewEmail() => $"org.{Guid.NewGuid():N}@test.local";

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

    /// <summary>Registers an account and returns the client plus the register-minted request token.</summary>
    private async Task<(HttpClient Client, string RegisterToken)> RegisterAsync(string email)
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string acquired = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var response = await SendAsync(client, HttpMethod.Post, $"{ApiBase}/auth/register",
            new RegisterPayload(
                FirstName: "Test",
                LastName: "Caller",
                Email: email,
                Phone: "+94771234567",
                Password), acquired);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<RegisterResponsePayload>();
        Assert.False(string.IsNullOrWhiteSpace(body!.RequestToken), "register must mint and return a request token (§1.10)");
        return (client, body.RequestToken!);
    }

    /// <summary>Registers, signs in, and returns the client plus the login-minted request token.</summary>
    private async Task<(HttpClient Client, string RequestToken)> LoginAsync(string email)
    {
        (HttpClient client, string registerToken) = await RegisterAsync(email);
        return await SignInAsync(client, registerToken, email);
    }

    /// <summary>Signs an already-registered account in on a fresh client (no second register).</summary>
    private async Task<(HttpClient Client, string RequestToken)> SignInAsync(string email)
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string acquired = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        return await SignInAsync(client, acquired, email);
    }

    private static async Task<(HttpClient Client, string RequestToken)> SignInAsync(
        HttpClient client, string requestToken, string email)
    {
        using var login = await SendAsync(client, HttpMethod.Post, $"{ApiBase}/auth/login",
            new LoginPayload(email, Password), requestToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        LoginResponsePayload body = await login.Content.ReadFromJsonAsync<LoginResponsePayload>()
            ?? throw new InvalidOperationException("Login answered without a body.");
        Assert.False(string.IsNullOrWhiteSpace(body.RequestToken), "login must re-mint a request token (§1.10)");
        return (client, body.RequestToken!);
    }

    private static async Task<AuthSessionResponse> ReadSessionAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"{ApiBase}/auth/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<AuthSessionResponse>()
            ?? throw new InvalidOperationException("session answered without a body.");
    }

    private static async Task<string> CreateOrganisationAsync(
        HttpClient client, string requestToken, IReadOnlyList<AddressInput>? addresses = null,
        IReadOnlyList<MemberPayload>? members = null)
    {
        using var response = await SendAsync(client, HttpMethod.Post, $"{ApiBase}/organizations",
            new CreateOrgRequest(
                new CompanyPayload(
                    Name: "Wizard Corp",
                    RegistrationNumber: null,
                    TaxId: null,
                    Website: "https://wizard.test"),
                Addresses: addresses ?? [ValidAddress()],
                Members: members), requestToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        OrganizationResponsePayload organization = await response.Content.ReadFromJsonAsync<OrganizationResponsePayload>()
            ?? throw new InvalidOperationException("Create organisation answered without a body.");
        if (string.IsNullOrWhiteSpace(organization.Id))
        {
            throw new InvalidOperationException("Create organisation answered without an id.");
        }

        return organization.Id;
    }

    private static AddressInput ValidAddress() => new(
        Line1: "42 Galle Road",
        Line2: null,
        City: "Colombo",
        District: "colombo",
        Province: "western",
        PostalCode: "10370",
        CountryCode: "LK",
        Latitude: 6.9271m,
        Longitude: 79.8612m,
        IsDefaultBilling: true,
        IsDefaultDelivery: true);

    // ---- Wizard step 3 (business-flows §2) ------------------------------------

    [Fact]
    public async Task Wizard_step_three_creates_the_organisation_atomically_and_lands_session_memberships()
    {
        string email = NewEmail();
        (HttpClient client, string requestToken) = await LoginAsync(email);

        using var response = await SendAsync(client, HttpMethod.Post, $"{ApiBase}/organizations",
            new CreateOrgRequest(
                new CompanyPayload("Zenith Traders", "PV2001", "134-558-990", "https://zenith.test"),
                Addresses: [ValidAddress()],
                Members: null), requestToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        OrganizationResponsePayload organization =
            await response.Content.ReadFromJsonAsync<OrganizationResponsePayload>()
            ?? throw new InvalidOperationException("Create answered without a body.");
        Assert.False(string.IsNullOrWhiteSpace(organization.Id));
        Assert.Equal("Zenith Traders", organization.Name);
        Assert.Equal("BuyerCompany", organization.Type);
        Assert.Equal("Active", organization.Status);
        Assert.Equal("PV2001", organization.RegistrationNumber);

        // The org's addresses ride the payload, not the caller's own address book.
        var address = Assert.Single(organization.Addresses!);
        Assert.False(string.IsNullOrWhiteSpace(address.Id));
        Assert.Equal("colombo", address.District);
        Assert.Equal("western", address.Province);
        Assert.Equal("LK", address.CountryCode);

        // The creator joins immediately as buyerAdmin (Accepted) — one row now;
        // no team members were requested.
        var creator = Assert.Single(organization.Members!);
        Assert.Equal("buyerAdmin", creator.Role);
        Assert.Equal("Accepted", creator.Status);
        Assert.NotNull(creator.JoinedAt);

        // The address is owned by the organisation, so the /me book stays empty.
        using (var mine = await client.GetAsync($"{ApiBase}/me/addresses"))
        {
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
            var myAddresses = await mine.Content.ReadFromJsonAsync<AddressResponsePayload[]>();
            Assert.Empty(myAddresses!);
        }

        // Session now carries the membership AND the creator capability resolves
        // from the accepted member row (the permission store union, 1.43).
        AuthSessionResponse session = await ReadSessionAsync(client);
        var membership = Assert.Single(session.OrgMemberships);
        Assert.Equal(organization.Id, membership.OrganizationId);
        Assert.Equal("Zenith Traders", membership.Name);
        Assert.Equal("buyerAdmin", membership.Role);
        Assert.Contains(PermissionsPayload.OrganizationMemberManage, session.Permissions);

        // One reviewable audit fact for the creation (§13.16).
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(
            row => row.Action == "organization.created" && row.TargetId == organization.Id));
    }

    [Fact]
    public async Task An_abandoned_step_three_never_leaves_a_half_built_organisation()
    {
        // A bad address (unknown district) rejects the WHOLE payload with the
        // wizard-nested field path — and nothing is persisted for the caller.
        string email = NewEmail();
        (HttpClient client, string requestToken) = await LoginAsync(email);

        // Tests share one serial host, so audit rows from earlier creations
        // exist; stamp this attempt's window for the row check below.
        DateTime startedAt = DateTime.UtcNow;

        using var response = await SendAsync(client, HttpMethod.Post, $"{ApiBase}/organizations",
            new CreateOrgRequest(
                new CompanyPayload("Half Built", null, null, null),
                Addresses:
                [
                    new AddressInput(
                        Line1: "Nowhere",
                        Line2: null,
                        City: "Atlantis",
                        District: "atlantis", // not a seeded reference code
                        Province: "western",
                        PostalCode: null,
                        CountryCode: "LK",
                        Latitude: null,
                        Longitude: null,
                        IsDefaultBilling: false,
                        IsDefaultDelivery: false),
                ],
                Members: null), requestToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        ErrorEnvelope envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>()
            ?? throw new InvalidOperationException("422 answered without an envelope.");
        Assert.Equal(ErrorCodes.ValidationFailed, envelope.Code);

        FieldError districtError = Assert.Single(
            envelope.FieldErrors!, error => error.Code == "invalid_reference");
        Assert.Equal("addresses[0].district", districtError.Field);

        // No organisation, no membership, no audit fact from this attempt.
        AuthSessionResponse session = await ReadSessionAsync(client);
        Assert.Empty(session.OrgMemberships);
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        Assert.False(await db.AuditLogs.AnyAsync(
            row => row.Action == "organization.created" && row.OccurredAt >= startedAt));
    }

    // ---- P9: the role-scope gate ---------------------------------------------

    [Fact]
    public async Task P9_a_platform_or_supplier_role_is_rejected_for_a_member_invite()
    {
        // The buyer admin of an organisation cannot assign a platform role
        // (or any non-organisation role) to a team member — only
        // organisation-scope roles are assignable. Rejected at the request
        // filter with a roleId field error, before any account lookup.
        string adminEmail = NewEmail();
        (HttpClient client, string requestToken) = await LoginAsync(adminEmail);
        string organizationId = await CreateOrganisationAsync(client, requestToken);

        string colleagueEmail = NewEmail();
        _ = await RegisterAsync(colleagueEmail);

        foreach (string rejectedRole in new[] { "platformAdministrator", "supplierOwner" })
        {
            using var response = await SendAsync(client, HttpMethod.Post,
                $"{ApiBase}/organizations/{organizationId}/members",
                new MemberPayload(colleagueEmail, rejectedRole), requestToken);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            ErrorEnvelope envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>()
                ?? throw new InvalidOperationException("422 answered without an envelope.");
            Assert.Equal(ErrorCodes.ValidationFailed, envelope.Code);

            FieldError error = Assert.Single(envelope.FieldErrors!);
            Assert.Equal("roleId", error.Field);
            Assert.Equal("invalid_role_scope", error.Code);
        }

        // The rejection left no invitation behind (T6.3: even a validated
        // request cannot sneak a platform role in — the use case re-checks).
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        Assert.False(await db.OrganizationMembers.AnyAsync(
            row => row.RoleCode == "platformAdministrator" || row.RoleCode == "supplierOwner"));
    }

    [Fact]
    public async Task An_admin_can_invite_an_existing_team_member_who_stays_pending_until_accepted()
    {
        string adminEmail = NewEmail();
        (HttpClient client, string requestToken) = await LoginAsync(adminEmail);
        string organizationId = await CreateOrganisationAsync(client, requestToken);

        string memberEmail = NewEmail();
        _ = await RegisterAsync(memberEmail);

        using var invite = await SendAsync(client, HttpMethod.Post,
            $"{ApiBase}/organizations/{organizationId}/members",
            new MemberPayload(memberEmail, "buyer"), requestToken);
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);
        MemberResponsePayload invited = await invite.Content.ReadFromJsonAsync<MemberResponsePayload>()
            ?? throw new InvalidOperationException("Invite answered without a body.");
        Assert.Equal("buyer", invited.Role);
        Assert.Equal("Pending", invited.Status);
        Assert.Null(invited.JoinedAt);

        // Re-inviting the same person to the same role is a validation failure.
        using var again = await SendAsync(client, HttpMethod.Post,
            $"{ApiBase}/organizations/{organizationId}/members",
            new MemberPayload(memberEmail, "buyer"), requestToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        ErrorEnvelope duplicate = await again.Content.ReadFromJsonAsync<ErrorEnvelope>()
            ?? throw new InvalidOperationException("422 answered without an envelope.");
        FieldError duplicateError = Assert.Single(duplicate.FieldErrors!);
        Assert.Equal("email", duplicateError.Field);
        Assert.Equal("already_member", duplicateError.Code);

        // The invitee is NOT a member yet: their session lists no membership
        // and their grants resolve nothing from the Pending row.
        (HttpClient memberClient, _) = await SignInAsync(memberEmail);
        AuthSessionResponse memberSession = await ReadSessionAsync(memberClient);
        Assert.Empty(memberSession.OrgMemberships);
        Assert.DoesNotContain(PermissionsPayload.OrganizationMemberManage, memberSession.Permissions);
    }

    [Fact]
    public async Task Inviting_an_account_that_does_not_exist_is_a_validation_failure()
    {
        // An invitation names an EXISTING account; an unknown address is a
        // field error (this surface is authenticated + rate-limited — A4's
        // anonymous enumeration protections apply to the public flows).
        string email = NewEmail();
        (HttpClient client, string requestToken) = await LoginAsync(email);
        string organizationId = await CreateOrganisationAsync(client, requestToken);

        using var response = await SendAsync(client, HttpMethod.Post,
            $"{ApiBase}/organizations/{organizationId}/members",
            new MemberPayload("nobody@nowhere.test", "buyer"), requestToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        ErrorEnvelope envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>()
            ?? throw new InvalidOperationException("422 answered without an envelope.");
        FieldError error = Assert.Single(envelope.FieldErrors!);
        Assert.Equal("email", error.Field);
        Assert.Equal("not_registered", error.Code);
    }

    // ---- Layer 3: capability is bound to membership (BOLA) -------------------

    [Fact]
    public async Task A_capability_holder_who_is_not_a_member_gets_the_same_404_as_a_missing_organisation()
    {
        // The layer-3 membership check (architecture §8.2): this caller holds
        // organization.member.manage — as buyerAdmin of THEIR OWN organisation —
        // but is not a member of the target. The target's existence (and the
        // attacker's lack of access) are indistinguishable: 404, never a 403.
        string attackerEmail = NewEmail();
        (HttpClient attacker, string attackerToken) = await LoginAsync(attackerEmail);
        string ownOrganizationId = await CreateOrganisationAsync(attacker, attackerToken);

        string victimEmail = NewEmail();
        (HttpClient victim, string victimToken) = await LoginAsync(victimEmail);
        string victimOrganizationId = await CreateOrganisationAsync(victim, victimToken);

        string targetEmail = NewEmail();
        _ = await RegisterAsync(targetEmail);

        Assert.NotEqual(ownOrganizationId, victimOrganizationId);

        using var response = await SendAsync(attacker, HttpMethod.Post,
            $"{ApiBase}/organizations/{victimOrganizationId}/members",
            new MemberPayload(targetEmail, "buyer"), attackerToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        ErrorEnvelope envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>()
            ?? throw new InvalidOperationException("404 answered without an envelope.");
        Assert.Equal(ErrorCodes.ResourceNotFound, envelope.Code);

        // And a random organisation id answers the identical 404 — existence is
        // not inferable (api.md §1.6).
        using var random = await SendAsync(attacker, HttpMethod.Post,
            $"{ApiBase}/organizations/{Guid.NewGuid()}/members",
            new MemberPayload(targetEmail, "buyer"), attackerToken);
        Assert.Equal(HttpStatusCode.NotFound, random.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_the_organisation_and_own_address_surfaces()
    {
        // F3 (wire twin): /me/** and the wizard's step 3 are authenticated
        // surfaces. The floor demands authentication for metadata-less routes
        // too — anonymous calls answer 401 (never a 403) under deny-by-default.
        HttpClient anonymous = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(anonymous);

        using var addresses = await anonymous.GetAsync($"{ApiBase}/me/addresses");
        Assert.Equal(HttpStatusCode.Unauthorized, addresses.StatusCode);

        // A token-valid POST to the wizard's step 3 — the action is gated by
        // [Authorize] and the floor alike.
        using var create = await SendAsync(anonymous, HttpMethod.Post, $"{ApiBase}/organizations",
            new CreateOrgRequest(new CompanyPayload("Anonymous", null, null, null),
                Addresses: null, Members: null), token);
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        ErrorEnvelope envelope = await create.Content.ReadFromJsonAsync<ErrorEnvelope>()
            ?? throw new InvalidOperationException("401 answered without an envelope.");
        Assert.Equal(ErrorCodes.Unauthenticated, envelope.Code);
    }

    // ---- /me/addresses (api.md §2.2) ------------------------------------------

    [Fact]
    public async Task The_own_address_book_is_scoped_to_the_caller_owned_written_and_deleted()
    {
        string email = NewEmail();
        (HttpClient client, string requestToken) = await LoginAsync(email);

        // Create → 201 with the stored row.
        using (var created = await SendAsync(client, HttpMethod.Post, $"{ApiBase}/me/addresses",
            ValidAddress(), requestToken))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            AddressResponsePayload address = await created.Content.ReadFromJsonAsync<AddressResponsePayload>()
                ?? throw new InvalidOperationException("Create answered without a body.");
            Assert.False(string.IsNullOrWhiteSpace(address.Id));
            Assert.Equal("colombo", address.District);

            // Read back.
            using var listed = await client.GetAsync($"{ApiBase}/me/addresses");
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            var all = await listed.Content.ReadFromJsonAsync<AddressResponsePayload[]>();
            Assert.Equal(address.Id, Assert.Single(all!).Id);

            // Update (PATCH, full-body) → 200 with the new values.
            using var updatedResponse = await SendAsync(client, HttpMethod.Patch,
                $"{ApiBase}/me/addresses/{address.Id}", ValidAddress() with { Line1 = "87 Marine Drive" }, requestToken);
            Assert.Equal(HttpStatusCode.OK, updatedResponse.StatusCode);
            AddressResponsePayload updated = await updatedResponse.Content.ReadFromJsonAsync<AddressResponsePayload>()
                ?? throw new InvalidOperationException("Patch answered without a body.");
            Assert.Equal("87 Marine Drive", updated.Line1);

            // Delete → 204, and the book is empty again.
            using var deleted = await SendAsync(client, HttpMethod.Delete,
                $"{ApiBase}/me/addresses/{address.Id}", payload: null, requestToken);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

            using var after = await client.GetAsync($"{ApiBase}/me/addresses");
            var remaining = await after.Content.ReadFromJsonAsync<AddressResponsePayload[]>();
            Assert.Empty(remaining!);
        }
    }

    [Fact]
    public async Task Another_users_address_is_invisible_and_untouchable()
    {
        string ownerEmail = NewEmail();
        (HttpClient owner, string ownerToken) = await LoginAsync(ownerEmail);

        AddressResponsePayload? address;
        using (var created = await SendAsync(owner, HttpMethod.Post, $"{ApiBase}/me/addresses",
            ValidAddress(), ownerToken))
        {
            address = await created.Content.ReadFromJsonAsync<AddressResponsePayload>();
        }

        string strangerEmail = NewEmail();
        (HttpClient stranger, string strangerToken) = await LoginAsync(strangerEmail);

        // The stranger's book is empty (scoping), and the owner's rows answer
        // 404 on every verb — the BOLA discipline of /me/** (api.md §1.6).
        using (var listed = await stranger.GetAsync($"{ApiBase}/me/addresses"))
        {
            var theirs = await listed.Content.ReadFromJsonAsync<AddressResponsePayload[]>();
            Assert.Empty(theirs!);
        }

        using (var patch = await SendAsync(stranger, HttpMethod.Patch,
            $"{ApiBase}/me/addresses/{address!.Id}", ValidAddress(), strangerToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        }

        using (var delete = await SendAsync(stranger, HttpMethod.Delete,
            $"{ApiBase}/me/addresses/{address.Id}", payload: null, strangerToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }

        // The owner can still read the untouched address.
        using var own = await owner.GetAsync($"{ApiBase}/me/addresses");
        var remaining = await own.Content.ReadFromJsonAsync<AddressResponsePayload[]>();
        Assert.Equal(address.Id, Assert.Single(remaining!).Id);
    }

    // ---- Wire payload shapes ---------------------------------------------------

    private sealed record RegisterPayload(
        string FirstName, string LastName, string Email, string? Phone, string Password);

    private sealed record RegisterResponsePayload(string? Status, string? RequestToken);

    private sealed record LoginPayload(string Email, string Password);

    private sealed record LoginResponsePayload(string? RequestToken, AuthSessionResponse? User);

    private sealed record CreateOrgRequest(
        CompanyPayload Company, IReadOnlyList<AddressInput>? Addresses, IReadOnlyList<MemberPayload>? Members);

    private sealed record CompanyPayload(
        string Name, string? RegistrationNumber, string? TaxId, string? Website);

    private sealed record MemberPayload(string Email, string RoleId);

    private sealed record AddressInput(
        string Line1, string? Line2, string City, string District, string Province,
        string? PostalCode, string CountryCode, decimal? Latitude, decimal? Longitude,
        bool IsDefaultBilling, bool IsDefaultDelivery);

    private sealed record OrganizationResponsePayload(
        string? Id, string? Name, string? Type, string? Status, string? RegistrationNumber,
        string? TaxId, string? Website,
        AddressResponsePayload[]? Addresses, MemberResponsePayload[]? Members);

    private sealed record MemberResponsePayload(
        string? UserId, string? Email, string? Role, string? Status, string? JoinedAt);

    private sealed record AddressResponsePayload(
        string? Id, string? Line1, string? Line2, string? City, string? District, string? Province,
        string? PostalCode, string? CountryCode, decimal? Latitude, decimal? Longitude,
        bool IsDefaultBilling, bool IsDefaultDelivery);
}

/// <summary>Wire mirror of the permission codes the assertions need (Contracts.Permissions references would pull the constant into the payload deserializer).</summary>
internal static class PermissionsPayload
{
    public const string OrganizationMemberManage = "organization.member.manage";
}