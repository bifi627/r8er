using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace R8er.Api.Migrations;

/// <summary>
/// Vocabulary rename (TECH_DEBT T9): devices → gateways. Hand-written renames, not
/// EF's scaffolded drop/create — rows (enrolled gateways, credentials, pins) survive.
/// </summary>
public partial class RenameDeviceToGateway : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameTable(name: "devices", newName: "gateways");
        migrationBuilder.RenameColumn(name: "AgentCredentialHash", table: "gateways", newName: "CredentialHash");
        migrationBuilder.RenameIndex(name: "IX_devices_AgentCredentialHash", table: "gateways", newName: "IX_gateways_CredentialHash");
        migrationBuilder.RenameIndex(name: "IX_devices_TenantId", table: "gateways", newName: "IX_gateways_TenantId");
        migrationBuilder.Sql("""ALTER TABLE gateways RENAME CONSTRAINT "PK_devices" TO "PK_gateways";""");
        migrationBuilder.Sql("""ALTER TABLE gateways RENAME CONSTRAINT "FK_devices_tenants_TenantId" TO "FK_gateways_tenants_TenantId";""");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""ALTER TABLE gateways RENAME CONSTRAINT "FK_gateways_tenants_TenantId" TO "FK_devices_tenants_TenantId";""");
        migrationBuilder.Sql("""ALTER TABLE gateways RENAME CONSTRAINT "PK_gateways" TO "PK_devices";""");
        migrationBuilder.RenameIndex(name: "IX_gateways_TenantId", table: "gateways", newName: "IX_devices_TenantId");
        migrationBuilder.RenameIndex(name: "IX_gateways_CredentialHash", table: "gateways", newName: "IX_devices_AgentCredentialHash");
        migrationBuilder.RenameColumn(name: "CredentialHash", table: "gateways", newName: "AgentCredentialHash");
        migrationBuilder.RenameTable(name: "gateways", newName: "devices");
    }
}
