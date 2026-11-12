using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PriorAuth.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReviewType",
                table: "AuthorizationRequests",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            // Before this change every request that needed a decision was a prior authorization request.
            migrationBuilder.Sql("UPDATE AuthorizationRequests SET ReviewType = 'PriorAuthorization' WHERE Status <> 'NotRequired';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewType",
                table: "AuthorizationRequests");
        }
    }
}
