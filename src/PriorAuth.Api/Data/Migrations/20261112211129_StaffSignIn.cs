using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PriorAuth.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class StaffSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                table: "ProviderClients",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedTo",
                table: "AuthorizationRequests",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedToName",
                table: "AuthorizationRequests",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecidedByAccount",
                table: "AuthorizationRequests",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DenialReasonCode",
                table: "AuthorizationRequests",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Modification",
                table: "AuthorizationRequests",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StaffMembers",
                columns: table => new
                {
                    Account = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Roles = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffMembers", x => x.Account);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationRequests_AssignedTo",
                table: "AuthorizationRequests",
                column: "AssignedTo");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffMembers");

            migrationBuilder.DropIndex(
                name: "IX_AuthorizationRequests_AssignedTo",
                table: "AuthorizationRequests");

            migrationBuilder.DropColumn(
                name: "ContactEmail",
                table: "ProviderClients");

            migrationBuilder.DropColumn(
                name: "AssignedTo",
                table: "AuthorizationRequests");

            migrationBuilder.DropColumn(
                name: "AssignedToName",
                table: "AuthorizationRequests");

            migrationBuilder.DropColumn(
                name: "DecidedByAccount",
                table: "AuthorizationRequests");

            migrationBuilder.DropColumn(
                name: "DenialReasonCode",
                table: "AuthorizationRequests");

            migrationBuilder.DropColumn(
                name: "Modification",
                table: "AuthorizationRequests");
        }
    }
}
