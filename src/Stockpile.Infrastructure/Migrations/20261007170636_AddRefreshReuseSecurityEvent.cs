using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockpile.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshReuseSecurityEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SecurityEvents_Type",
                table: "SecurityEvents");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SecurityEvents_Type",
                table: "SecurityEvents",
                sql: "[Type] IN ('LoginSuccess', 'LoginFailure', 'LockoutTriggered', 'LockoutReleased', 'PasswordChanged', 'PasswordResetRequested', 'PasswordResetCompleted', 'EmailVerificationRequested', 'EmailVerified', 'SessionRevoked', 'AllSessionsRevoked', 'PrivilegeChanged', 'RateLimitHit', 'WebhookSignatureFailure', 'RefreshReuseDetected')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SecurityEvents_Type",
                table: "SecurityEvents");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SecurityEvents_Type",
                table: "SecurityEvents",
                sql: "[Type] IN ('LoginSuccess', 'LoginFailure', 'LockoutTriggered', 'LockoutReleased', 'PasswordChanged', 'PasswordResetRequested', 'PasswordResetCompleted', 'EmailVerificationRequested', 'EmailVerified', 'SessionRevoked', 'AllSessionsRevoked', 'PrivilegeChanged', 'RateLimitHit', 'WebhookSignatureFailure')");
        }
    }
}
