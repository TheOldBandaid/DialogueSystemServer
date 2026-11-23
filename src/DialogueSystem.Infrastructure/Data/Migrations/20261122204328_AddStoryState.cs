using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StoryState",
                table: "Previews",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoryState",
                table: "PreviewHistoryEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NextState",
                table: "Choices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequiredState",
                table: "Choices",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StoryState",
                table: "Previews");

            migrationBuilder.DropColumn(
                name: "StoryState",
                table: "PreviewHistoryEntry");

            migrationBuilder.DropColumn(
                name: "NextState",
                table: "Choices");

            migrationBuilder.DropColumn(
                name: "RequiredState",
                table: "Choices");
        }
    }
}
