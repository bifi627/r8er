using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace R8er.Api.Migrations;

/// <inheritdoc />
public partial class GatewayVersion : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Version",
            table: "gateways",
            type: "text",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Version",
            table: "gateways");
    }
}
