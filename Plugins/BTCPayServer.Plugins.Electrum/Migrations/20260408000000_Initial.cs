using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Electrum.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "electrum");

            migrationBuilder.CreateTable(
                name: "tracked_wallets",
                schema: "electrum",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    CryptoCode = table.Column<string>(type: "text", nullable: true),
                    DerivationStrategy = table.Column<string>(type: "text", nullable: true),
                    ReceiveGapIndex = table.Column<int>(type: "integer", nullable: false),
                    ChangeGapIndex = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracked_wallets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sync_state",
                schema: "electrum",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_state", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "tracked_addresses",
                schema: "electrum",
                columns: table => new
                {
                    Scripthash = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: true),
                    KeyPath = table.Column<string>(type: "text", nullable: true),
                    ScriptPubKey = table.Column<byte[]>(type: "bytea", nullable: true),
                    Address = table.Column<string>(type: "text", nullable: true),
                    IsChange = table.Column<bool>(type: "boolean", nullable: false),
                    IsUsed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracked_addresses", x => x.Scripthash);
                    table.ForeignKey(
                        name: "FK_tracked_addresses_tracked_wallets_WalletId",
                        column: x => x.WalletId,
                        principalSchema: "electrum",
                        principalTable: "tracked_wallets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "utxos",
                schema: "electrum",
                columns: table => new
                {
                    Outpoint = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: true),
                    Scripthash = table.Column<string>(type: "text", nullable: true),
                    Txid = table.Column<string>(type: "text", nullable: true),
                    Vout = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false),
                    ScriptPubKey = table.Column<byte[]>(type: "bytea", nullable: true),
                    KeyPath = table.Column<string>(type: "text", nullable: true),
                    BlockHeight = table.Column<long>(type: "bigint", nullable: true),
                    BlockHash = table.Column<string>(type: "text", nullable: true),
                    SeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsSpent = table.Column<bool>(type: "boolean", nullable: false),
                    SpendingTxid = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_utxos", x => x.Outpoint);
                    table.ForeignKey(
                        name: "FK_utxos_tracked_wallets_WalletId",
                        column: x => x.WalletId,
                        principalSchema: "electrum",
                        principalTable: "tracked_wallets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_utxos_tracked_addresses_Scripthash",
                        column: x => x.Scripthash,
                        principalSchema: "electrum",
                        principalTable: "tracked_addresses",
                        principalColumn: "Scripthash");
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                schema: "electrum",
                columns: table => new
                {
                    Txid = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    RawTx = table.Column<byte[]>(type: "bytea", nullable: true),
                    BlockHeight = table.Column<long>(type: "bigint", nullable: true),
                    BlockHash = table.Column<string>(type: "text", nullable: true),
                    Fee = table.Column<long>(type: "bigint", nullable: true),
                    SeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    BalanceChange = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transactions", x => new { x.Txid, x.WalletId });
                    table.ForeignKey(
                        name: "FK_transactions_tracked_wallets_WalletId",
                        column: x => x.WalletId,
                        principalSchema: "electrum",
                        principalTable: "tracked_wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tracked_addresses_WalletId",
                schema: "electrum",
                table: "tracked_addresses",
                column: "WalletId");

            migrationBuilder.CreateIndex(
                name: "IX_utxos_WalletId",
                schema: "electrum",
                table: "utxos",
                column: "WalletId",
                filter: "NOT \"IsSpent\"");

            migrationBuilder.CreateIndex(
                name: "IX_utxos_Scripthash",
                schema: "electrum",
                table: "utxos",
                column: "Scripthash");

            migrationBuilder.CreateIndex(
                name: "IX_transactions_WalletId",
                schema: "electrum",
                table: "transactions",
                column: "WalletId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "transactions", schema: "electrum");
            migrationBuilder.DropTable(name: "utxos", schema: "electrum");
            migrationBuilder.DropTable(name: "tracked_addresses", schema: "electrum");
            migrationBuilder.DropTable(name: "sync_state", schema: "electrum");
            migrationBuilder.DropTable(name: "tracked_wallets", schema: "electrum");
        }
    }
}
