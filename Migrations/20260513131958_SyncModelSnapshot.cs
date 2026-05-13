using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalList.API.NET.Migrations
{
    /// <inheritdoc />
    public partial class SyncModelSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No-op: chat_sessions table was already created by AddChatSession +
            // AddChatSessionSecurityColumns migrations. This migration exists solely
            // to re-sync the model snapshot that was reverted during PR 1.5 development.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: matches the no-op Up().
        }
    }
}
