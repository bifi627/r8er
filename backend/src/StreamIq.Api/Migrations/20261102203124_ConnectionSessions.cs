using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamIq.Api.Migrations;

/// <summary>
/// Connection telemetry, tenant-scoped (item 5 PR B). Replaces the POC's raw
/// poc_connectivity table (Checkpoint-1 spike sink, no tenant): dropped here,
/// its findings live in docs/poc-connectivity-spike.md.
/// </summary>
public partial class ConnectionSessions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS poc_connectivity;");

        migrationBuilder.CreateTable(
            name: "connection_sessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                GatewayId = table.Column<Guid>(type: "uuid", nullable: false),
                Relay = table.Column<bool>(type: "boolean", nullable: false),
                Ipv6 = table.Column<bool>(type: "boolean", nullable: false),
                LocalCandidateType = table.Column<string>(type: "text", nullable: true),
                RemoteCandidateType = table.Column<string>(type: "text", nullable: true),
                Protocol = table.Column<string>(type: "text", nullable: true),
                BytesReceived = table.Column<long>(type: "bigint", nullable: true),
                BytesSent = table.Column<long>(type: "bigint", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_connection_sessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_connection_sessions_gateways_GatewayId",
                    column: x => x.GatewayId,
                    principalTable: "gateways",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_connection_sessions_tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_connection_sessions_GatewayId",
            table: "connection_sessions",
            column: "GatewayId");

        migrationBuilder.CreateIndex(
            name: "IX_connection_sessions_TenantId",
            table: "connection_sessions",
            column: "TenantId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "connection_sessions");
    }
}
