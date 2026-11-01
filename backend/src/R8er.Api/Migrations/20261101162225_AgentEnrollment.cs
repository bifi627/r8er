using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace R8er.Api.Migrations;

/// <inheritdoc />
public partial class AgentEnrollment : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LanUrl",
            table: "devices",
            type: "text",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "enrollment_tokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                TokenHash = table.Column<string>(type: "text", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_enrollment_tokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_enrollment_tokens_tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_devices_AgentCredentialHash",
            table: "devices",
            column: "AgentCredentialHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_enrollment_tokens_TenantId",
            table: "enrollment_tokens",
            column: "TenantId");

        migrationBuilder.CreateIndex(
            name: "IX_enrollment_tokens_TokenHash",
            table: "enrollment_tokens",
            column: "TokenHash",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "enrollment_tokens");

        migrationBuilder.DropIndex(
            name: "IX_devices_AgentCredentialHash",
            table: "devices");

        migrationBuilder.DropColumn(
            name: "LanUrl",
            table: "devices");
    }
}
