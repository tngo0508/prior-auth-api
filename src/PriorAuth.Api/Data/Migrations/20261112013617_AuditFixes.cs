using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PriorAuth.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthorizationRequests_ClientId_ProviderRequestId",
                table: "AuthorizationRequests");

            migrationBuilder.AddColumn<string>(
                name: "Sha256",
                table: "SupportingDocuments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "AuthorizationRequests",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "ProviderRequestId",
                table: "AuthorizationRequests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "ClientId",
                table: "AuthorizationRequests",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastChangedAtUtc",
                table: "AuthorizationRequests",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "MemberIdentifier",
                table: "AuthorizationRequests",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderRequestSystem",
                table: "AuthorizationRequests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "ServiceSystem",
                table: "AuthorizationRequests",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "AuthorizationRequests",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Existing rows: their last change is unknown, so use when they arrived; every listed code so far is HCPCS.
            migrationBuilder.Sql("UPDATE AuthorizationRequests SET LastChangedAtUtc = ReceivedAtUtc, Version = 1");
            migrationBuilder.Sql("UPDATE AuthorizationRequests SET ServiceSystem = 'http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets' WHERE ServiceSystem = ''");
            migrationBuilder.CreateTable(
                name: "ReceivedBundle",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AuthorizationRequestId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceivedBundle", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceivedBundle_AuthorizationRequests_AuthorizationRequestId",
                        column: x => x.AuthorizationRequestId,
                        principalTable: "AuthorizationRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationRequests_ClientId_ProviderRequestSystem_ProviderRequestId",
                table: "AuthorizationRequests",
                columns: new[] { "ClientId", "ProviderRequestSystem", "ProviderRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationRequests_Status_ReceivedAtUtc",
                table: "AuthorizationRequests",
                columns: new[] { "Status", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedBundle_AuthorizationRequestId",
                table: "ReceivedBundle",
                column: "AuthorizationRequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceivedBundle");

            migrationBuilder.DropIndex(
                name: "IX_AuthorizationRequests_ClientId_ProviderRequestSystem_ProviderRequestId",
                table: "AuthorizationRequests");

            migrationBuilder.DropIndex(
                name: "IX_AuthorizationRequests_Status_ReceivedAtUtc",
                table: "AuthorizationRequests");

            migrationBuilder.DropColumn(
                name: "Sha256",
                table: "SupportingDocuments");

            migrationBuilder.DropColumn(
                name: "LastChangedAtUtc",
                table: "AuthorizationRequests");

            migrationBuilder.DropColumn(
                name: "MemberIdentifier",
                table: "AuthorizationRequests");

            migrationBuilder.DropColumn(
                name: "ProviderRequestSystem",
                table: "AuthorizationRequests");

            migrationBuilder.DropColumn(
                name: "ServiceSystem",
                table: "AuthorizationRequests");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "AuthorizationRequests");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "AuthorizationRequests",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "ProviderRequestId",
                table: "AuthorizationRequests",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "ClientId",
                table: "AuthorizationRequests",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationRequests_ClientId_ProviderRequestId",
                table: "AuthorizationRequests",
                columns: new[] { "ClientId", "ProviderRequestId" },
                unique: true);
        }
    }
}
