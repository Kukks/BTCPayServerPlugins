using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Electrum.Migrations
{
    /// <inheritdoc />
    public partial class ReservedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReservedReceiveIndex",
                schema: "electrum",
                table: "tracked_wallets",
                type: "integer",
                nullable: false,
                defaultValue: -1);

            migrationBuilder.AddColumn<int>(
                name: "ReservedChangeIndex",
                schema: "electrum",
                table: "tracked_wallets",
                type: "integer",
                nullable: false,
                defaultValue: -1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ReservedReceiveIndex", schema: "electrum", table: "tracked_wallets");
            migrationBuilder.DropColumn(name: "ReservedChangeIndex", schema: "electrum", table: "tracked_wallets");
        }
    }
}
