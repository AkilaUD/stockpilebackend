IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [AuditLogs] (
        [Id] bigint NOT NULL IDENTITY,
        [ActorId] uniqueidentifier NULL,
        [ActorOrganizationId] uniqueidentifier NULL,
        [Action] nvarchar(100) NOT NULL,
        [TargetType] nvarchar(100) NOT NULL,
        [TargetId] nvarchar(100) NULL,
        [Result] nvarchar(40) NOT NULL,
        [IpAddress] nvarchar(45) NULL,
        [UserAgent] nvarchar(400) NULL,
        [OccurredAt] datetime2 NOT NULL,
        [CorrelationId] nvarchar(64) NULL,
        [Metadata] nvarchar(max) NULL,
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_AuditLogs_Result] CHECK ([Result] IN ('Success', 'Failure', 'Denied'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [Countries] (
        [Code] nvarchar(2) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [NumericCode] nvarchar(3) NOT NULL,
        CONSTRAINT [PK_Countries] PRIMARY KEY ([Code])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [FeatureFlags] (
        [Code] nvarchar(80) NOT NULL,
        [Enabled] bit NOT NULL,
        [RolloutPercent] int NOT NULL,
        [AllowedEnvironments] nvarchar(200) NOT NULL,
        CONSTRAINT [PK_FeatureFlags] PRIMARY KEY ([Code]),
        CONSTRAINT [CK_FeatureFlags_RolloutPercent] CHECK ([RolloutPercent] >= 0 AND [RolloutPercent] <= 100)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [IdempotencyKeys] (
        [OwnerId] uniqueidentifier NOT NULL,
        [Key] nvarchar(100) NOT NULL,
        [RequestHash] nvarchar(64) NOT NULL,
        [ResponseReference] nvarchar(400) NULL,
        [ResponseStatus] int NULL,
        [ExpiresAt] datetime2 NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_IdempotencyKeys] PRIMARY KEY ([OwnerId], [Key])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [OutboxMessages] (
        [Id] bigint NOT NULL IDENTITY,
        [OccurredAt] datetime2 NOT NULL,
        [EventType] nvarchar(100) NOT NULL,
        [Payload] nvarchar(max) NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [Attempts] int NOT NULL,
        [NextAttemptAt] datetime2 NOT NULL,
        [CorrelationId] nvarchar(64) NULL,
        [DeadLetterReason] nvarchar(500) NULL,
        CONSTRAINT [PK_OutboxMessages] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_OutboxMessages_Status] CHECK ([Status] IN ('Pending', 'Processed', 'DeadLettered'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [Permissions] (
        [Code] nvarchar(100) NOT NULL,
        [Description] nvarchar(200) NOT NULL,
        [Category] nvarchar(40) NOT NULL,
        CONSTRAINT [PK_Permissions] PRIMARY KEY ([Code])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [Provinces] (
        [Code] nvarchar(20) NOT NULL,
        [Name] nvarchar(60) NOT NULL,
        CONSTRAINT [PK_Provinces] PRIMARY KEY ([Code])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [Roles] (
        [Code] nvarchar(80) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Scope] nvarchar(40) NOT NULL,
        CONSTRAINT [PK_Roles] PRIMARY KEY ([Code]),
        CONSTRAINT [CK_Roles_Scope] CHECK ([Scope] IN ('Platform', 'Organization', 'Supplier'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [SecurityEvents] (
        [Id] bigint NOT NULL IDENTITY,
        [Type] nvarchar(40) NOT NULL,
        [ActorId] uniqueidentifier NULL,
        [UserId] uniqueidentifier NULL,
        [IpAddress] nvarchar(45) NULL,
        [UserAgent] nvarchar(400) NULL,
        [OccurredAt] datetime2 NOT NULL,
        [CorrelationId] nvarchar(64) NULL,
        [Detail] nvarchar(500) NULL,
        [Metadata] nvarchar(max) NULL,
        CONSTRAINT [PK_SecurityEvents] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_SecurityEvents_Type] CHECK ([Type] IN ('LoginSuccess', 'LoginFailure', 'LockoutTriggered', 'LockoutReleased', 'PasswordChanged', 'PasswordResetRequested', 'PasswordResetCompleted', 'EmailVerificationRequested', 'EmailVerified', 'SessionRevoked', 'AllSessionsRevoked', 'PrivilegeChanged', 'RateLimitHit', 'WebhookSignatureFailure'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [SystemSettings] (
        [Key] nvarchar(100) NOT NULL,
        [Value] nvarchar(max) NOT NULL,
        [ValueType] nvarchar(40) NOT NULL,
        [IsSecret] bit NOT NULL,
        [UpdatedBy] uniqueidentifier NULL,
        CONSTRAINT [PK_SystemSettings] PRIMARY KEY ([Key]),
        CONSTRAINT [CK_SystemSettings_ValueType] CHECK ([ValueType] IN ('String', 'Integer', 'Decimal', 'Boolean', 'Json', 'Duration'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [Users] (
        [PublicId] uniqueidentifier NOT NULL,
        [Email] nvarchar(256) NOT NULL,
        [EmailNormalized] nvarchar(256) NOT NULL,
        [EmailConfirmedAt] datetime2 NULL,
        [PasswordHash] nvarchar(max) NOT NULL,
        [FirstName] nvarchar(100) NOT NULL,
        [LastName] nvarchar(100) NOT NULL,
        [Phone] nvarchar(30) NULL,
        [PhoneNumberConfirmedAt] datetime2 NULL,
        [LockoutEndsAt] datetime2 NULL,
        [FailedAccessCount] int NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [TwoFactorEnabled] bit NOT NULL,
        [PreferredLocale] nvarchar(10) NOT NULL,
        [DeactivatedAt] datetime2 NULL,
        [RowVersion] rowversion NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [CreatedBy] uniqueidentifier NULL,
        [UpdatedAt] datetime2 NULL,
        [UpdatedBy] uniqueidentifier NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([PublicId]),
        CONSTRAINT [CK_Users_FailedAccessCount] CHECK ([FailedAccessCount] >= 0),
        CONSTRAINT [CK_Users_Status] CHECK ([Status] IN ('Active', 'Suspended', 'Deleted'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [StateTransitions] (
        [Entity] nvarchar(40) NOT NULL,
        [FromState] nvarchar(40) NOT NULL,
        [ToState] nvarchar(40) NOT NULL,
        [PermissionCode] nvarchar(100) NULL,
        [RequiresReason] bit NOT NULL,
        [IsForbidden] bit NOT NULL,
        [Notes] nvarchar(300) NULL,
        CONSTRAINT [PK_StateTransitions] PRIMARY KEY ([Entity], [FromState], [ToState]),
        CONSTRAINT [FK_StateTransitions_Permissions_PermissionCode] FOREIGN KEY ([PermissionCode]) REFERENCES [Permissions] ([Code]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [Districts] (
        [Code] nvarchar(20) NOT NULL,
        [Name] nvarchar(60) NOT NULL,
        [ProvinceCode] nvarchar(20) NOT NULL,
        CONSTRAINT [PK_Districts] PRIMARY KEY ([Code]),
        CONSTRAINT [FK_Districts_Provinces_ProvinceCode] FOREIGN KEY ([ProvinceCode]) REFERENCES [Provinces] ([Code]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [RolePermissions] (
        [RoleCode] nvarchar(80) NOT NULL,
        [PermissionCode] nvarchar(100) NOT NULL,
        CONSTRAINT [PK_RolePermissions] PRIMARY KEY ([RoleCode], [PermissionCode]),
        CONSTRAINT [FK_RolePermissions_Permissions_PermissionCode] FOREIGN KEY ([PermissionCode]) REFERENCES [Permissions] ([Code]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RolePermissions_Roles_RoleCode] FOREIGN KEY ([RoleCode]) REFERENCES [Roles] ([Code]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [LoginSessions] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [IssuedAt] datetime2 NOT NULL,
        [ExpiresAt] datetime2 NOT NULL,
        [RevokedAt] datetime2 NULL,
        [IpAddress] nvarchar(45) NULL,
        [UserAgent] nvarchar(400) NULL,
        [LastSeenAt] datetime2 NOT NULL,
        CONSTRAINT [PK_LoginSessions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_LoginSessions_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [MediaAssets] (
        [PublicId] uniqueidentifier NOT NULL,
        [StorageKey] nvarchar(400) NOT NULL,
        [OriginalFileName] nvarchar(255) NOT NULL,
        [ContentType] nvarchar(120) NOT NULL,
        [ByteSize] bigint NOT NULL,
        [Sha256] nvarchar(64) NOT NULL,
        [Width] int NULL,
        [Height] int NULL,
        [FocalX] decimal(5,4) NULL,
        [FocalY] decimal(5,4) NULL,
        [AltText] nvarchar(160) NULL,
        [Caption] nvarchar(300) NULL,
        [ProvenanceSourceUrl] nvarchar(500) NULL,
        [ProvenanceRetrievedAt] datetime2 NULL,
        [ProvenanceAuthorisation] nvarchar(300) NULL,
        [ProvenanceLicense] nvarchar(120) NULL,
        [UploadedBy] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        CONSTRAINT [PK_MediaAssets] PRIMARY KEY ([PublicId]),
        CONSTRAINT [CK_MediaAssets_ByteSize] CHECK ([ByteSize] >= 0),
        CONSTRAINT [CK_MediaAssets_FocalX] CHECK ([FocalX] IS NULL OR ([FocalX] >= 0 AND [FocalX] <= 1)),
        CONSTRAINT [CK_MediaAssets_FocalY] CHECK ([FocalY] IS NULL OR ([FocalY] >= 0 AND [FocalY] <= 1)),
        CONSTRAINT [CK_MediaAssets_Height] CHECK ([Height] IS NULL OR [Height] >= 0),
        CONSTRAINT [CK_MediaAssets_Status] CHECK ([Status] IN ('Active', 'Archived', 'Rejected')),
        CONSTRAINT [CK_MediaAssets_Width] CHECK ([Width] IS NULL OR [Width] >= 0),
        CONSTRAINT [FK_MediaAssets_Users_UploadedBy] FOREIGN KEY ([UploadedBy]) REFERENCES [Users] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [NotificationPreferences] (
        [UserId] uniqueidentifier NOT NULL,
        [Category] nvarchar(60) NOT NULL,
        [Channel] nvarchar(40) NOT NULL,
        [Enabled] bit NOT NULL DEFAULT CAST(1 AS bit),
        CONSTRAINT [PK_NotificationPreferences] PRIMARY KEY ([UserId], [Category], [Channel]),
        CONSTRAINT [CK_NotificationPreferences_Channel] CHECK ([Channel] IN ('Email', 'Sms', 'WhatsApp', 'InApp')),
        CONSTRAINT [FK_NotificationPreferences_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [UserPreferences] (
        [UserId] uniqueidentifier NOT NULL,
        [Key] nvarchar(80) NOT NULL,
        [Value] nvarchar(400) NOT NULL,
        CONSTRAINT [PK_UserPreferences] PRIMARY KEY ([UserId], [Key]),
        CONSTRAINT [FK_UserPreferences_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [UserRoles] (
        [UserId] uniqueidentifier NOT NULL,
        [RoleCode] nvarchar(80) NOT NULL,
        CONSTRAINT [PK_UserRoles] PRIMARY KEY ([UserId], [RoleCode]),
        CONSTRAINT [FK_UserRoles_Roles_RoleCode] FOREIGN KEY ([RoleCode]) REFERENCES [Roles] ([Code]) ON DELETE NO ACTION,
        CONSTRAINT [FK_UserRoles_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [RefreshSessions] (
        [Id] uniqueidentifier NOT NULL,
        [SessionId] uniqueidentifier NOT NULL,
        [TokenHash] nvarchar(64) NOT NULL,
        [ExpiresAt] datetime2 NOT NULL,
        [RotatedToId] uniqueidentifier NULL,
        [RevokedAt] datetime2 NULL,
        CONSTRAINT [PK_RefreshSessions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RefreshSessions_LoginSessions_SessionId] FOREIGN KEY ([SessionId]) REFERENCES [LoginSessions] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RefreshSessions_RefreshSessions_RotatedToId] FOREIGN KEY ([RotatedToId]) REFERENCES [RefreshSessions] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [Addresses] (
        [Id] uniqueidentifier NOT NULL,
        [Line1] nvarchar(200) NOT NULL,
        [Line2] nvarchar(200) NULL,
        [City] nvarchar(100) NOT NULL,
        [District] nvarchar(20) NOT NULL,
        [Province] nvarchar(20) NOT NULL,
        [PostalCode] nvarchar(20) NULL,
        [CountryCode] nvarchar(2) NOT NULL,
        [Latitude] decimal(9,6) NULL,
        [Longitude] decimal(9,6) NULL,
        [IsDefaultBilling] bit NOT NULL,
        [IsDefaultDelivery] bit NOT NULL,
        [OrganizationId] uniqueidentifier NULL,
        [UserId] uniqueidentifier NULL,
        CONSTRAINT [PK_Addresses] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Addresses_Latitude] CHECK ([Latitude] IS NULL OR ([Latitude] >= -90 AND [Latitude] <= 90)),
        CONSTRAINT [CK_Addresses_Longitude] CHECK ([Longitude] IS NULL OR ([Longitude] >= -180 AND [Longitude] <= 180)),
        CONSTRAINT [CK_Addresses_Owner] CHECK (([OrganizationId] IS NOT NULL AND [UserId] IS NULL) OR ([OrganizationId] IS NULL AND [UserId] IS NOT NULL)),
        CONSTRAINT [FK_Addresses_Countries_CountryCode] FOREIGN KEY ([CountryCode]) REFERENCES [Countries] ([Code]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Addresses_Districts_District] FOREIGN KEY ([District]) REFERENCES [Districts] ([Code]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Addresses_Provinces_Province] FOREIGN KEY ([Province]) REFERENCES [Provinces] ([Code]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Addresses_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [Organizations] (
        [PublicId] uniqueidentifier NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [Type] nvarchar(40) NOT NULL,
        [RegistrationNumber] nvarchar(60) NULL,
        [TaxId] nvarchar(60) NULL,
        [Website] nvarchar(300) NULL,
        [Status] nvarchar(40) NOT NULL,
        [PrimaryAddressId] uniqueidentifier NULL,
        [RowVersion] rowversion NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [CreatedBy] uniqueidentifier NULL,
        [UpdatedAt] datetime2 NULL,
        [UpdatedBy] uniqueidentifier NULL,
        CONSTRAINT [PK_Organizations] PRIMARY KEY ([PublicId]),
        CONSTRAINT [CK_Organizations_Status] CHECK ([Status] IN ('Active', 'Suspended', 'Closed')),
        CONSTRAINT [FK_Organizations_Addresses_PrimaryAddressId] FOREIGN KEY ([PrimaryAddressId]) REFERENCES [Addresses] ([Id]) ON DELETE SET NULL
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE TABLE [OrganizationMembers] (
        [OrganizationId] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [RoleCode] nvarchar(80) NOT NULL,
        [InvitationStatus] int NOT NULL,
        [JoinedAt] datetime2 NULL,
        CONSTRAINT [PK_OrganizationMembers] PRIMARY KEY ([OrganizationId], [UserId], [RoleCode]),
        CONSTRAINT [CK_OrganizationMembers_InvitationStatus] CHECK ([InvitationStatus] IN ('Pending', 'Accepted', 'Declined', 'Revoked')),
        CONSTRAINT [FK_OrganizationMembers_Organizations_OrganizationId] FOREIGN KEY ([OrganizationId]) REFERENCES [Organizations] ([PublicId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_OrganizationMembers_Roles_RoleCode] FOREIGN KEY ([RoleCode]) REFERENCES [Roles] ([Code]) ON DELETE NO ACTION,
        CONSTRAINT [FK_OrganizationMembers_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Addresses_CountryCode] ON [Addresses] ([CountryCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Addresses_District] ON [Addresses] ([District]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Addresses_OrganizationId] ON [Addresses] ([OrganizationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Addresses_Province] ON [Addresses] ([Province]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Addresses_UserId] ON [Addresses] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_Action_OccurredAt] ON [AuditLogs] ([Action], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_ActorId_OccurredAt] ON [AuditLogs] ([ActorId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_OccurredAt] ON [AuditLogs] ([OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Districts_ProvinceCode] ON [Districts] ([ProvinceCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_IdempotencyKeys_ExpiresAt] ON [IdempotencyKeys] ([ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_LoginSessions_ExpiresAt] ON [LoginSessions] ([ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_LoginSessions_UserId_ExpiresAt] ON [LoginSessions] ([UserId], [ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MediaAssets_Status_CreatedAt] ON [MediaAssets] ([Status], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MediaAssets_UploadedBy] ON [MediaAssets] ([UploadedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_MediaAssets_Sha256] ON [MediaAssets] ([Sha256]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_MediaAssets_StorageKey] ON [MediaAssets] ([StorageKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_OrganizationMembers_RoleCode] ON [OrganizationMembers] ([RoleCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_OrganizationMembers_UserId] ON [OrganizationMembers] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Organizations_PrimaryAddressId] ON [Organizations] ([PrimaryAddressId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_OutboxMessages_Status_NextAttemptAt] ON [OutboxMessages] ([Status], [NextAttemptAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RefreshSessions_RotatedToId] ON [RefreshSessions] ([RotatedToId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RefreshSessions_SessionId] ON [RefreshSessions] ([SessionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_RefreshSessions_TokenHash] ON [RefreshSessions] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RolePermissions_PermissionCode] ON [RolePermissions] ([PermissionCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SecurityEvents_OccurredAt] ON [SecurityEvents] ([OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SecurityEvents_UserId_OccurredAt] ON [SecurityEvents] ([UserId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_StateTransitions_PermissionCode] ON [StateTransitions] ([PermissionCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UserRoles_RoleCode] ON [UserRoles] ([RoleCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Users_EmailNormalized] ON [Users] ([EmailNormalized]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    ALTER TABLE [Addresses] ADD CONSTRAINT [FK_Addresses_Organizations_OrganizationId] FOREIGN KEY ([OrganizationId]) REFERENCES [Organizations] ([PublicId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006114440_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261006114440_InitialCreate', N'10.0.12');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007144259_AddPasswordResetTokens'
)
BEGIN
    CREATE TABLE [PasswordResetTokens] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [TokenHash] nvarchar(64) NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [UsedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_PasswordResetTokens] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PasswordResetTokens_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007144259_AddPasswordResetTokens'
)
BEGIN
    CREATE INDEX [IX_PasswordResetTokens_UserId_TokenHash] ON [PasswordResetTokens] ([UserId], [TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007144259_AddPasswordResetTokens'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261007144259_AddPasswordResetTokens', N'10.0.12');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007152928_AddEmailVerificationTokens'
)
BEGIN
    CREATE TABLE [EmailVerificationTokens] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [TokenHash] nvarchar(64) NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [UsedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_EmailVerificationTokens] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_EmailVerificationTokens_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007152928_AddEmailVerificationTokens'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EmailVerificationTokens_TokenHash] ON [EmailVerificationTokens] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007152928_AddEmailVerificationTokens'
)
BEGIN
    CREATE INDEX [IX_EmailVerificationTokens_UserId] ON [EmailVerificationTokens] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007152928_AddEmailVerificationTokens'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261007152928_AddEmailVerificationTokens', N'10.0.12');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007155331_AddUserLockoutEpisode'
)
BEGIN
    ALTER TABLE [Users] ADD [LockoutEpisode] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007155331_AddUserLockoutEpisode'
)
BEGIN
    EXEC(N'ALTER TABLE [Users] ADD CONSTRAINT [CK_Users_LockoutEpisode] CHECK ([LockoutEpisode] >= 0)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007155331_AddUserLockoutEpisode'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261007155331_AddUserLockoutEpisode', N'10.0.12');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007170636_AddRefreshReuseSecurityEvent'
)
BEGIN
    ALTER TABLE [SecurityEvents] DROP CONSTRAINT [CK_SecurityEvents_Type];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007170636_AddRefreshReuseSecurityEvent'
)
BEGIN
    EXEC(N'ALTER TABLE [SecurityEvents] ADD CONSTRAINT [CK_SecurityEvents_Type] CHECK ([Type] IN (''LoginSuccess'', ''LoginFailure'', ''LockoutTriggered'', ''LockoutReleased'', ''PasswordChanged'', ''PasswordResetRequested'', ''PasswordResetCompleted'', ''EmailVerificationRequested'', ''EmailVerified'', ''SessionRevoked'', ''AllSessionsRevoked'', ''PrivilegeChanged'', ''RateLimitHit'', ''WebhookSignatureFailure'', ''RefreshReuseDetected''))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007170636_AddRefreshReuseSecurityEvent'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261007170636_AddRefreshReuseSecurityEvent', N'10.0.12');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008020553_StoreInvitationStatusAsString'
)
BEGIN
    ALTER TABLE [OrganizationMembers] DROP CONSTRAINT [CK_OrganizationMembers_InvitationStatus];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008020553_StoreInvitationStatusAsString'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[OrganizationMembers]') AND [c].[name] = N'InvitationStatus');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [OrganizationMembers] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [OrganizationMembers] ALTER COLUMN [InvitationStatus] nvarchar(40) NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008020553_StoreInvitationStatusAsString'
)
BEGIN
    EXEC(N'ALTER TABLE [OrganizationMembers] ADD CONSTRAINT [CK_OrganizationMembers_InvitationStatus] CHECK ([InvitationStatus] IN (''Pending'', ''Accepted'', ''Declined'', ''Revoked''))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008020553_StoreInvitationStatusAsString'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261008020553_StoreInvitationStatusAsString', N'10.0.12');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008033952_AddUserTwoFactorSecret'
)
BEGIN
    ALTER TABLE [Users] ADD [TwoFactorSecret] nvarchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008033952_AddUserTwoFactorSecret'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261008033952_AddUserTwoFactorSecret', N'10.0.12');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    DECLARE @var1 nvarchar(max);
    SELECT @var1 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[MediaAssets]') AND [c].[name] = N'UploadedBy');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [MediaAssets] DROP CONSTRAINT ' + @var1 + ';');
    ALTER TABLE [MediaAssets] ALTER COLUMN [UploadedBy] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [Brands] (
        [PublicId] uniqueidentifier NOT NULL,
        [Code] nvarchar(80) NOT NULL,
        [Name] nvarchar(160) NOT NULL,
        [Website] nvarchar(300) NULL,
        [LogoMediaId] uniqueidentifier NULL,
        [CountryCode] nvarchar(2) NULL,
        CONSTRAINT [PK_Brands] PRIMARY KEY ([PublicId]),
        CONSTRAINT [FK_Brands_MediaAssets_LogoMediaId] FOREIGN KEY ([LogoMediaId]) REFERENCES [MediaAssets] ([PublicId]) ON DELETE SET NULL
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [Categories] (
        [PublicId] uniqueidentifier NOT NULL,
        [ParentId] uniqueidentifier NULL,
        [Slug] nvarchar(160) NOT NULL,
        [Name] nvarchar(160) NOT NULL,
        [NameSi] nvarchar(160) NULL,
        [Description] nvarchar(2000) NULL,
        [ImageMediaId] uniqueidentifier NULL,
        [IconKey] nvarchar(40) NULL,
        [SortOrder] int NOT NULL,
        [IsActive] bit NOT NULL,
        [ProcurementMetaphor] nvarchar(max) NULL,
        [MaterializedPath] nvarchar(400) NOT NULL,
        CONSTRAINT [PK_Categories] PRIMARY KEY ([PublicId]),
        CONSTRAINT [FK_Categories_Categories_ParentId] FOREIGN KEY ([ParentId]) REFERENCES [Categories] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [Manufacturers] (
        [PublicId] uniqueidentifier NOT NULL,
        [Code] nvarchar(80) NOT NULL,
        [Name] nvarchar(160) NOT NULL,
        [Website] nvarchar(300) NULL,
        [CountryCode] nvarchar(2) NULL,
        CONSTRAINT [PK_Manufacturers] PRIMARY KEY ([PublicId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [Suppliers] (
        [PublicId] uniqueidentifier NOT NULL,
        [OrganizationId] uniqueidentifier NULL,
        [Slug] nvarchar(160) NOT NULL,
        [DisplayName] nvarchar(200) NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [VerificationState] nvarchar(40) NOT NULL,
        [Story] nvarchar(max) NULL,
        [LogoMediaId] uniqueidentifier NULL,
        [BannerMediaId] uniqueidentifier NULL,
        [FoundedYear] int NULL,
        [EmployeeBand] nvarchar(30) NULL,
        [DefaultAudiencePolicy] nvarchar(40) NOT NULL,
        [ServiceRadiusKm] int NULL,
        [RowVersion] rowversion NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [CreatedBy] uniqueidentifier NULL,
        [UpdatedAt] datetime2 NULL,
        [UpdatedBy] uniqueidentifier NULL,
        CONSTRAINT [PK_Suppliers] PRIMARY KEY ([PublicId]),
        CONSTRAINT [CK_Suppliers_Status] CHECK ([Status] IN ('Draft', 'PendingVerification', 'Active', 'Suspended', 'Closed')),
        CONSTRAINT [CK_Suppliers_VerificationState] CHECK ([VerificationState] IN ('Unverified', 'Pending', 'Verified', 'Rejected', 'Expired')),
        CONSTRAINT [FK_Suppliers_MediaAssets_BannerMediaId] FOREIGN KEY ([BannerMediaId]) REFERENCES [MediaAssets] ([PublicId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Suppliers_MediaAssets_LogoMediaId] FOREIGN KEY ([LogoMediaId]) REFERENCES [MediaAssets] ([PublicId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Suppliers_Organizations_OrganizationId] FOREIGN KEY ([OrganizationId]) REFERENCES [Organizations] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [UnitOfMeasures] (
        [PublicId] uniqueidentifier NOT NULL,
        [Code] nvarchar(30) NOT NULL,
        [Name] nvarchar(60) NOT NULL,
        [PluralName] nvarchar(60) NOT NULL,
        [DimensionKind] nvarchar(40) NOT NULL,
        CONSTRAINT [PK_UnitOfMeasures] PRIMARY KEY ([PublicId]),
        CONSTRAINT [CK_UnitOfMeasures_DimensionKind] CHECK ([DimensionKind] IN ('Count', 'Length', 'Area', 'Volume', 'Mass', 'Time'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [Products] (
        [PublicId] uniqueidentifier NOT NULL,
        [SupplierId] uniqueidentifier NOT NULL,
        [CategoryId] uniqueidentifier NOT NULL,
        [ItemType] nvarchar(40) NOT NULL,
        [Name] nvarchar(300) NOT NULL,
        [Slug] nvarchar(300) NOT NULL,
        [Sku] nvarchar(100) NOT NULL,
        [BrandId] uniqueidentifier NULL,
        [ManufacturerId] uniqueidentifier NULL,
        [Summary] nvarchar(500) NULL,
        [Description] nvarchar(max) NULL,
        [Status] nvarchar(40) NOT NULL,
        [CountryOfOriginCode] nvarchar(2) NULL,
        [MinOrderQuantity] decimal(18,2) NULL,
        [LeadTimeDays] int NULL,
        [TaxClassCode] nvarchar(40) NULL,
        [PublishedAt] datetime2 NULL,
        [SearchDocumentVersion] bigint NOT NULL,
        [RowVersion] rowversion NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [CreatedBy] uniqueidentifier NULL,
        [UpdatedAt] datetime2 NULL,
        [UpdatedBy] uniqueidentifier NULL,
        CONSTRAINT [PK_Products] PRIMARY KEY ([PublicId]),
        CONSTRAINT [CK_Products_Status] CHECK ([Status] IN ('Draft', 'PendingReview', 'Published', 'Unpublished', 'Rejected', 'Archived')),
        CONSTRAINT [FK_Products_Brands_BrandId] FOREIGN KEY ([BrandId]) REFERENCES [Brands] ([PublicId]) ON DELETE SET NULL,
        CONSTRAINT [FK_Products_Categories_CategoryId] FOREIGN KEY ([CategoryId]) REFERENCES [Categories] ([PublicId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Products_Manufacturers_ManufacturerId] FOREIGN KEY ([ManufacturerId]) REFERENCES [Manufacturers] ([PublicId]) ON DELETE SET NULL,
        CONSTRAINT [FK_Products_Suppliers_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [Suppliers] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [SupplierAudiencePolicies] (
        [SupplierId] uniqueidentifier NOT NULL,
        [Audience] nvarchar(40) NOT NULL,
        [AppliesTo] nvarchar(40) NOT NULL,
        [Visibility] nvarchar(40) NOT NULL,
        CONSTRAINT [PK_SupplierAudiencePolicies] PRIMARY KEY ([SupplierId]),
        CONSTRAINT [CK_SupplierAudiencePolicies_AppliesTo] CHECK ([AppliesTo] IN ('AllProducts', 'SelectedProducts')),
        CONSTRAINT [CK_SupplierAudiencePolicies_Audience] CHECK ([Audience] IN ('Contractors', 'Architects', 'Consultants', 'Developers', 'Everyone')),
        CONSTRAINT [CK_SupplierAudiencePolicies_Visibility] CHECK ([Visibility] IN ('Prices', 'FullDetails')),
        CONSTRAINT [FK_SupplierAudiencePolicies_Suppliers_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [Suppliers] ([PublicId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [SupplierBadges] (
        [PublicId] uniqueidentifier NOT NULL,
        [SupplierId] uniqueidentifier NOT NULL,
        [BadgeKey] nvarchar(40) NOT NULL,
        [Label] nvarchar(80) NULL,
        [GrantedAt] datetime2 NOT NULL,
        [Reason] nvarchar(200) NULL,
        CONSTRAINT [PK_SupplierBadges] PRIMARY KEY ([PublicId]),
        CONSTRAINT [FK_SupplierBadges_Suppliers_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [Suppliers] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [SupplierCategories] (
        [PublicId] uniqueidentifier NOT NULL,
        [SupplierId] uniqueidentifier NOT NULL,
        [CategoryId] uniqueidentifier NOT NULL,
        [SpecialtyLevel] nvarchar(40) NOT NULL,
        [Quotable] bit NOT NULL,
        CONSTRAINT [PK_SupplierCategories] PRIMARY KEY ([PublicId]),
        CONSTRAINT [CK_SupplierCategories_SpecialtyLevel] CHECK ([SpecialtyLevel] IN ('Primary', 'Secondary')),
        CONSTRAINT [FK_SupplierCategories_Categories_CategoryId] FOREIGN KEY ([CategoryId]) REFERENCES [Categories] ([PublicId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SupplierCategories_Suppliers_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [Suppliers] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [SupplierProfiles] (
        [SupplierId] uniqueidentifier NOT NULL,
        [ContactPerson] nvarchar(160) NULL,
        [Phone] nvarchar(30) NULL,
        [Email] nvarchar(160) NULL,
        [Website] nvarchar(300) NULL,
        [OperationAddress] nvarchar(300) NULL,
        [CorporateAddress] nvarchar(300) NULL,
        [Latitude] float(9) NULL,
        [Longitude] float(9) NULL,
        CONSTRAINT [PK_SupplierProfiles] PRIMARY KEY ([SupplierId]),
        CONSTRAINT [FK_SupplierProfiles_Suppliers_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [Suppliers] ([PublicId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [SupplierPublicPageConfigs] (
        [SupplierId] uniqueidentifier NOT NULL,
        [HeroHeadline] nvarchar(200) NULL,
        [HeroImageMediaId] uniqueidentifier NULL,
        [ShowProducts] bit NOT NULL,
        [ShowProjects] bit NOT NULL,
        [ShowReviews] bit NOT NULL,
        [ShowPolicies] bit NOT NULL,
        [PrimaryCtaLabel] nvarchar(60) NULL,
        [CustomSections] nvarchar(max) NULL,
        CONSTRAINT [PK_SupplierPublicPageConfigs] PRIMARY KEY ([SupplierId]),
        CONSTRAINT [FK_SupplierPublicPageConfigs_Suppliers_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [Suppliers] ([PublicId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [SupplierServiceAreas] (
        [PublicId] uniqueidentifier NOT NULL,
        [SupplierId] uniqueidentifier NOT NULL,
        [DistrictCode] nvarchar(20) NOT NULL,
        [IsDefault] bit NOT NULL,
        [RadiusKm] int NULL,
        CONSTRAINT [PK_SupplierServiceAreas] PRIMARY KEY ([PublicId]),
        CONSTRAINT [FK_SupplierServiceAreas_Districts_DistrictCode] FOREIGN KEY ([DistrictCode]) REFERENCES [Districts] ([Code]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SupplierServiceAreas_Suppliers_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [Suppliers] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [SupplierVerifications] (
        [PublicId] uniqueidentifier NOT NULL,
        [SupplierId] uniqueidentifier NOT NULL,
        [RequestedAt] datetime2 NOT NULL,
        [RequestedBy] uniqueidentifier NULL,
        [DocumentSetId] uniqueidentifier NULL,
        [DecisionAt] datetime2 NULL,
        [DecisionBy] uniqueidentifier NULL,
        [Decision] nvarchar(40) NOT NULL,
        [Reason] nvarchar(400) NULL,
        [ExpiresAt] datetime2 NULL,
        CONSTRAINT [PK_SupplierVerifications] PRIMARY KEY ([PublicId]),
        CONSTRAINT [CK_SupplierVerifications_Decision] CHECK ([Decision] IN ('None', 'Approved', 'Rejected')),
        CONSTRAINT [FK_SupplierVerifications_Suppliers_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [Suppliers] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [AttributeDefinitions] (
        [PublicId] uniqueidentifier NOT NULL,
        [Code] nvarchar(80) NOT NULL,
        [DisplayName] nvarchar(120) NOT NULL,
        [DataType] nvarchar(40) NOT NULL,
        [UnitOfMeasureId] uniqueidentifier NULL,
        [IsSystem] bit NOT NULL,
        [HelpText] nvarchar(500) NULL,
        [DisplayFormat] nvarchar(40) NULL,
        [Searchable] bit NOT NULL,
        CONSTRAINT [PK_AttributeDefinitions] PRIMARY KEY ([PublicId]),
        CONSTRAINT [FK_AttributeDefinitions_UnitOfMeasures_UnitOfMeasureId] FOREIGN KEY ([UnitOfMeasureId]) REFERENCES [UnitOfMeasures] ([PublicId]) ON DELETE SET NULL
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [ProductMedia] (
        [PublicId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [MediaAssetId] uniqueidentifier NOT NULL,
        [SortOrder] int NOT NULL,
        [IsPrimary] bit NOT NULL,
        [AltText] nvarchar(300) NULL,
        CONSTRAINT [PK_ProductMedia] PRIMARY KEY ([PublicId]),
        CONSTRAINT [FK_ProductMedia_MediaAssets_MediaAssetId] FOREIGN KEY ([MediaAssetId]) REFERENCES [MediaAssets] ([PublicId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ProductMedia_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [ProductPriceHistory] (
        [PublicId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [CurrencyCode] nvarchar(3) NOT NULL,
        [PreviousAmount] decimal(18,2) NOT NULL,
        [NewAmount] decimal(18,2) NOT NULL,
        [ChangedAt] datetime2 NOT NULL,
        [ChangedBy] uniqueidentifier NULL,
        [Reason] nvarchar(200) NULL,
        CONSTRAINT [PK_ProductPriceHistory] PRIMARY KEY ([PublicId]),
        CONSTRAINT [CK_ProductPriceHistory_NonNegativeChange] CHECK ([PreviousAmount] >= 0 AND [NewAmount] >= 0),
        CONSTRAINT [FK_ProductPriceHistory_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [ProductPrices] (
        [PublicId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [CurrencyCode] nvarchar(3) NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [PriceState] nvarchar(40) NOT NULL,
        [MinQty] decimal(18,2) NULL,
        [MaxQty] decimal(18,2) NULL,
        [EffectiveAt] datetime2 NOT NULL,
        [IsPublic] bit NOT NULL,
        CONSTRAINT [PK_ProductPrices] PRIMARY KEY ([PublicId]),
        CONSTRAINT [CK_ProductPrices_PriceState] CHECK ([PriceState] IN ('Public', 'OnRequest', 'BulkTiered', 'Negotiable')),
        CONSTRAINT [FK_ProductPrices_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [ProductStocks] (
        [PublicId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [IsStockTracked] bit NOT NULL,
        [StockState] nvarchar(40) NOT NULL,
        [OnHand] int NOT NULL,
        [Reserved] int NOT NULL,
        [LowStockThreshold] int NULL,
        [LastMovementAt] datetime2 NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_ProductStocks] PRIMARY KEY ([PublicId]),
        CONSTRAINT [CK_ProductStocks_NonNegative] CHECK ([OnHand] >= 0 AND [Reserved] >= 0),
        CONSTRAINT [CK_ProductStocks_StockState] CHECK ([StockState] IN ('InStock', 'LowStock', 'OutOfStock', 'MadeToOrder', 'EnquireForAvailability')),
        CONSTRAINT [FK_ProductStocks_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [StockAdjustments] (
        [PublicId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [Delta] int NOT NULL,
        [Reason] nvarchar(200) NULL,
        [ReferenceType] nvarchar(40) NULL,
        [ReferenceId] uniqueidentifier NULL,
        [ActorId] uniqueidentifier NULL,
        [OccurredAt] datetime2 NOT NULL,
        CONSTRAINT [PK_StockAdjustments] PRIMARY KEY ([PublicId]),
        CONSTRAINT [FK_StockAdjustments_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [AttributeOptions] (
        [PublicId] uniqueidentifier NOT NULL,
        [AttributeId] uniqueidentifier NOT NULL,
        [Code] nvarchar(80) NOT NULL,
        [DisplayName] nvarchar(120) NOT NULL,
        [SortOrder] int NOT NULL,
        [Swatch] nvarchar(9) NULL,
        CONSTRAINT [PK_AttributeOptions] PRIMARY KEY ([PublicId]),
        CONSTRAINT [FK_AttributeOptions_AttributeDefinitions_AttributeId] FOREIGN KEY ([AttributeId]) REFERENCES [AttributeDefinitions] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [CategoryAttributeDefinitions] (
        [CategoryId] uniqueidentifier NOT NULL,
        [AttributeId] uniqueidentifier NOT NULL,
        [IsRequired] bit NOT NULL,
        [IsFilterable] bit NOT NULL,
        [IsSortable] bit NOT NULL,
        [IsComparable] bit NOT NULL,
        [SortOrder] int NOT NULL,
        [DefaultValue] nvarchar(200) NULL,
        [AppliesToItemType] nvarchar(40) NOT NULL,
        CONSTRAINT [PK_CategoryAttributeDefinitions] PRIMARY KEY ([CategoryId], [AttributeId]),
        CONSTRAINT [FK_CategoryAttributeDefinitions_AttributeDefinitions_AttributeId] FOREIGN KEY ([AttributeId]) REFERENCES [AttributeDefinitions] ([PublicId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CategoryAttributeDefinitions_Categories_CategoryId] FOREIGN KEY ([CategoryId]) REFERENCES [Categories] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE TABLE [ProductAttributeValues] (
        [PublicId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [AttributeId] uniqueidentifier NOT NULL,
        [ValueText] nvarchar(600) NULL,
        [ValueNumber] decimal(18,4) NULL,
        [ValueBool] bit NULL,
        [OptionId] uniqueidentifier NULL,
        CONSTRAINT [PK_ProductAttributeValues] PRIMARY KEY ([PublicId]),
        CONSTRAINT [FK_ProductAttributeValues_AttributeDefinitions_AttributeId] FOREIGN KEY ([AttributeId]) REFERENCES [AttributeDefinitions] ([PublicId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ProductAttributeValues_AttributeOptions_OptionId] FOREIGN KEY ([OptionId]) REFERENCES [AttributeOptions] ([PublicId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ProductAttributeValues_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([PublicId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_AttributeDefinitions_Code] ON [AttributeDefinitions] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_AttributeDefinitions_UnitOfMeasureId] ON [AttributeDefinitions] ([UnitOfMeasureId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_AttributeOptions_AttributeId_Code] ON [AttributeOptions] ([AttributeId], [Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Brands_Code] ON [Brands] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_Brands_LogoMediaId] ON [Brands] ([LogoMediaId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_Categories_MaterializedPath] ON [Categories] ([MaterializedPath]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_Categories_ParentId] ON [Categories] ([ParentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Categories_Slug] ON [Categories] ([Slug]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_CategoryAttributeDefinitions_AttributeId] ON [CategoryAttributeDefinitions] ([AttributeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Manufacturers_Code] ON [Manufacturers] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_ProductAttributeValues_AttributeId] ON [ProductAttributeValues] ([AttributeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_ProductAttributeValues_OptionId] ON [ProductAttributeValues] ([OptionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ProductAttributeValues_ProductId_AttributeId] ON [ProductAttributeValues] ([ProductId], [AttributeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_ProductMedia_MediaAssetId] ON [ProductMedia] ([MediaAssetId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_ProductMedia_ProductId_SortOrder] ON [ProductMedia] ([ProductId], [SortOrder]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_ProductPriceHistory_ProductId_ChangedAt] ON [ProductPriceHistory] ([ProductId], [ChangedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_ProductPrices_ProductId] ON [ProductPrices] ([ProductId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ProductPrices_ProductId_CurrencyCode] ON [ProductPrices] ([ProductId], [CurrencyCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_Products_BrandId] ON [Products] ([BrandId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_Products_CategoryId] ON [Products] ([CategoryId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_Products_ManufacturerId] ON [Products] ([ManufacturerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Products_Slug] ON [Products] ([Slug]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_Products_Status] ON [Products] ([Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Products_SupplierId_Sku] ON [Products] ([SupplierId], [Sku]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ProductStocks_ProductId] ON [ProductStocks] ([ProductId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_StockAdjustments_ProductId_OccurredAt] ON [StockAdjustments] ([ProductId], [OccurredAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_SupplierBadges_SupplierId_BadgeKey] ON [SupplierBadges] ([SupplierId], [BadgeKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_SupplierCategories_CategoryId] ON [SupplierCategories] ([CategoryId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_SupplierCategories_SupplierId_CategoryId] ON [SupplierCategories] ([SupplierId], [CategoryId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_Suppliers_BannerMediaId] ON [Suppliers] ([BannerMediaId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_Suppliers_LogoMediaId] ON [Suppliers] ([LogoMediaId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_Suppliers_OrganizationId] ON [Suppliers] ([OrganizationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Suppliers_Slug] ON [Suppliers] ([Slug]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_Suppliers_Status] ON [Suppliers] ([Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_SupplierServiceAreas_DistrictCode] ON [SupplierServiceAreas] ([DistrictCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_SupplierServiceAreas_SupplierId_DistrictCode] ON [SupplierServiceAreas] ([SupplierId], [DistrictCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE INDEX [IX_SupplierVerifications_SupplierId_RequestedAt] ON [SupplierVerifications] ([SupplierId], [RequestedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    CREATE UNIQUE INDEX [IX_UnitOfMeasures_Code] ON [UnitOfMeasures] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184310_CataloguePhase2'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261008184310_CataloguePhase2', N'10.0.12');
END;

COMMIT;
GO

