using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.MicroNode.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "BTCPayServer.Plugins.MicroNode");

            migrationBuilder.CreateTable(
                name: "MicroAccounts",
                schema: "BTCPayServer.Plugins.MicroNode",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    Balance = table.Column<long>(type: "bigint", nullable: false),
                    BalanceCheckpoint = table.Column<long>(type: "bigint", nullable: false),
                    MasterStoreId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MicroAccounts", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "MicroTransactions",
                schema: "BTCPayServer.Plugins.MicroNode",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    AccountId = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    Accounted = table.Column<bool>(type: "boolean", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    DependentId = table.Column<string>(type: "text", nullable: true),
                    DependentAccountId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MicroTransactions", x => new { x.Id, x.AccountId });
                    table.ForeignKey(
                        name: "FK_MicroTransactions_MicroAccounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "BTCPayServer.Plugins.MicroNode",
                        principalTable: "MicroAccounts",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MicroTransactions_MicroTransactions_DependentId_AccountId",
                        columns: x => new { x.DependentId, x.AccountId },
                        principalSchema: "BTCPayServer.Plugins.MicroNode",
                        principalTable: "MicroTransactions",
                        principalColumns: new[] { "Id", "AccountId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MicroTransactions_MicroTransactions_DependentId_DependentAc~",
                        columns: x => new { x.DependentId, x.DependentAccountId },
                        principalSchema: "BTCPayServer.Plugins.MicroNode",
                        principalTable: "MicroTransactions",
                        principalColumns: new[] { "Id", "AccountId" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_MicroTransactions_AccountId",
                schema: "BTCPayServer.Plugins.MicroNode",
                table: "MicroTransactions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_MicroTransactions_DependentId_AccountId",
                schema: "BTCPayServer.Plugins.MicroNode",
                table: "MicroTransactions",
                columns: new[] { "DependentId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_MicroTransactions_DependentId_DependentAccountId",
                schema: "BTCPayServer.Plugins.MicroNode",
                table: "MicroTransactions",
                columns: new[] { "DependentId", "DependentAccountId" });

            migrationBuilder.Sql("CREATE FUNCTION \"BTCPayServer.Plugins.MicroNode\".\"LC_TRIGGER_AFTER_DELETE_MICROTRANSACTION\"() RETURNS trigger as $LC_TRIGGER_AFTER_DELETE_MICROTRANSACTION$\r\nBEGIN\r\n  \r\n  IF OLD.\"Accounted\" IS TRUE THEN \r\n    UPDATE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\"\r\n    SET \"Balance\" = \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"Balance\" - OLD.\"Amount\"\r\n    WHERE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"Key\" = OLD.\"AccountId\";\r\n  END IF;\r\n  UPDATE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\"\r\n  SET \"BalanceCheckpoint\" = \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"BalanceCheckpoint\" + 1\r\n  WHERE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"Key\" = OLD.\"AccountId\";\r\nRETURN OLD;\r\nEND;\r\n$LC_TRIGGER_AFTER_DELETE_MICROTRANSACTION$ LANGUAGE plpgsql;\r\nCREATE TRIGGER LC_TRIGGER_AFTER_DELETE_MICROTRANSACTION AFTER DELETE\r\nON \"BTCPayServer.Plugins.MicroNode\".\"MicroTransactions\"\r\nFOR EACH ROW EXECUTE PROCEDURE \"BTCPayServer.Plugins.MicroNode\".\"LC_TRIGGER_AFTER_DELETE_MICROTRANSACTION\"();");

            migrationBuilder.Sql("CREATE FUNCTION \"BTCPayServer.Plugins.MicroNode\".\"LC_TRIGGER_AFTER_INSERT_MICROTRANSACTION\"() RETURNS trigger as $LC_TRIGGER_AFTER_INSERT_MICROTRANSACTION$\r\nBEGIN\r\n  \r\n  IF NEW.\"Accounted\" IS TRUE THEN \r\n    UPDATE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\"\r\n    SET \"Balance\" = \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"Balance\" + NEW.\"Amount\"\r\n    WHERE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"Key\" = NEW.\"AccountId\";\r\n  END IF;\r\n  UPDATE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\"\r\n  SET \"BalanceCheckpoint\" = \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"BalanceCheckpoint\" + 1\r\n  WHERE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"Key\" = NEW.\"AccountId\";\r\n  UPDATE \"BTCPayServer.Plugins.MicroNode\".\"MicroTransactions\"\r\n  SET \"Accounted\" = NEW.\"Accounted\", \"Active\" = NEW.\"Active\"\r\n  WHERE NEW.\"Id\" = \"BTCPayServer.Plugins.MicroNode\".\"MicroTransactions\".\"DependentId\" AND \"BTCPayServer.Plugins.MicroNode\".\"MicroTransactions\".\"AccountId\" = NEW.\"AccountId\";\r\nRETURN NEW;\r\nEND;\r\n$LC_TRIGGER_AFTER_INSERT_MICROTRANSACTION$ LANGUAGE plpgsql;\r\nCREATE TRIGGER LC_TRIGGER_AFTER_INSERT_MICROTRANSACTION AFTER INSERT\r\nON \"BTCPayServer.Plugins.MicroNode\".\"MicroTransactions\"\r\nFOR EACH ROW EXECUTE PROCEDURE \"BTCPayServer.Plugins.MicroNode\".\"LC_TRIGGER_AFTER_INSERT_MICROTRANSACTION\"();");

            migrationBuilder.Sql("CREATE FUNCTION \"BTCPayServer.Plugins.MicroNode\".\"LC_TRIGGER_AFTER_UPDATE_MICROTRANSACTION\"() RETURNS trigger as $LC_TRIGGER_AFTER_UPDATE_MICROTRANSACTION$\r\nBEGIN\r\n  UPDATE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\"\r\n  SET \"BalanceCheckpoint\" = \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"BalanceCheckpoint\" + 1\r\n  WHERE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"Key\" = OLD.\"AccountId\";\r\n  UPDATE \"BTCPayServer.Plugins.MicroNode\".\"MicroTransactions\"\r\n  SET \"Accounted\" = NEW.\"Accounted\", \"Active\" = NEW.\"Active\", \"DependentId\" = NEW.\"Id\"\r\n  WHERE OLD.\"Id\" = \"BTCPayServer.Plugins.MicroNode\".\"MicroTransactions\".\"DependentId\" AND \"BTCPayServer.Plugins.MicroNode\".\"MicroTransactions\".\"AccountId\" = NEW.\"AccountId\";\r\n  \r\n  IF NEW.\"Accounted\" IS TRUE AND OLD.\"Accounted\" IS FALSE THEN \r\n    UPDATE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\"\r\n    SET \"Balance\" = \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"Balance\" + NEW.\"Amount\"\r\n    WHERE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"Key\" = OLD.\"AccountId\";\r\n  END IF;\r\n  \r\n  IF NEW.\"Accounted\" IS TRUE AND OLD.\"Accounted\" IS TRUE AND OLD.\"Amount\" <> NEW.\"Amount\" THEN \r\n    UPDATE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\"\r\n    SET \"Balance\" = \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"Balance\" - OLD.\"Amount\" + NEW.\"Amount\"\r\n    WHERE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"Key\" = OLD.\"AccountId\";\r\n  END IF;\r\n  \r\n  IF NEW.\"Accounted\" IS FALSE AND OLD.\"Accounted\" IS TRUE THEN \r\n    UPDATE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\"\r\n    SET \"Balance\" = \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"Balance\" - OLD.\"Amount\"\r\n    WHERE \"BTCPayServer.Plugins.MicroNode\".\"MicroAccounts\".\"Key\" = OLD.\"AccountId\";\r\n  END IF;\r\nRETURN NEW;\r\nEND;\r\n$LC_TRIGGER_AFTER_UPDATE_MICROTRANSACTION$ LANGUAGE plpgsql;\r\nCREATE TRIGGER LC_TRIGGER_AFTER_UPDATE_MICROTRANSACTION AFTER UPDATE\r\nON \"BTCPayServer.Plugins.MicroNode\".\"MicroTransactions\"\r\nFOR EACH ROW EXECUTE PROCEDURE \"BTCPayServer.Plugins.MicroNode\".\"LC_TRIGGER_AFTER_UPDATE_MICROTRANSACTION\"();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION \"BTCPayServer.Plugins.MicroNode\".\"LC_TRIGGER_AFTER_DELETE_MICROTRANSACTION\"() CASCADE;");

            migrationBuilder.Sql("DROP FUNCTION \"BTCPayServer.Plugins.MicroNode\".\"LC_TRIGGER_AFTER_INSERT_MICROTRANSACTION\"() CASCADE;");

            migrationBuilder.Sql("DROP FUNCTION \"BTCPayServer.Plugins.MicroNode\".\"LC_TRIGGER_AFTER_UPDATE_MICROTRANSACTION\"() CASCADE;");

            migrationBuilder.DropTable(
                name: "MicroTransactions",
                schema: "BTCPayServer.Plugins.MicroNode");

            migrationBuilder.DropTable(
                name: "MicroAccounts",
                schema: "BTCPayServer.Plugins.MicroNode");
        }
    }
}
