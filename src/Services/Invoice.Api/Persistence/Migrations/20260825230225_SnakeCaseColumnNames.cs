using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PandaPocket.Services.Invoice.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SnakeCaseColumnNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_invoice_status_history_invoices_InvoiceId",
                table: "invoice_status_history");

            migrationBuilder.DropForeignKey(
                name: "FK_payments_invoices_InvoiceId",
                table: "payments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_payments",
                table: "payments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_invoices",
                table: "invoices");

            migrationBuilder.DropPrimaryKey(
                name: "PK_invoice_status_history",
                table: "invoice_status_history");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "payments",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TxHash",
                table: "payments",
                newName: "tx_hash");

            migrationBuilder.RenameColumn(
                name: "ReceivedAt",
                table: "payments",
                newName: "received_at");

            migrationBuilder.RenameColumn(
                name: "InvoiceId",
                table: "payments",
                newName: "invoice_id");

            migrationBuilder.RenameColumn(
                name: "CorrelationId",
                table: "payments",
                newName: "correlation_id");

            migrationBuilder.RenameColumn(
                name: "AmountCrypto",
                table: "payments",
                newName: "amount_crypto");

            migrationBuilder.RenameIndex(
                name: "IX_payments_InvoiceId",
                table: "payments",
                newName: "ix_payments_invoice_id");

            migrationBuilder.RenameColumn(
                name: "Status",
                table: "invoices",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Reference",
                table: "invoices",
                newName: "reference");

            migrationBuilder.RenameColumn(
                name: "Asset",
                table: "invoices",
                newName: "asset");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "invoices",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "SettledAt",
                table: "invoices",
                newName: "settled_at");

            migrationBuilder.RenameColumn(
                name: "PayToAddress",
                table: "invoices",
                newName: "pay_to_address");

            migrationBuilder.RenameColumn(
                name: "MerchantId",
                table: "invoices",
                newName: "merchant_id");

            migrationBuilder.RenameColumn(
                name: "LockedRate",
                table: "invoices",
                newName: "locked_rate");

            migrationBuilder.RenameColumn(
                name: "ExpiresAt",
                table: "invoices",
                newName: "expires_at");

            migrationBuilder.RenameColumn(
                name: "CryptoAmount",
                table: "invoices",
                newName: "crypto_amount");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "invoices",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "AmountZar",
                table: "invoices",
                newName: "amount_zar");

            migrationBuilder.RenameColumn(
                name: "Reason",
                table: "invoice_status_history",
                newName: "reason");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "invoice_status_history",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "ToStatus",
                table: "invoice_status_history",
                newName: "to_status");

            migrationBuilder.RenameColumn(
                name: "InvoiceId",
                table: "invoice_status_history",
                newName: "invoice_id");

            migrationBuilder.RenameColumn(
                name: "FromStatus",
                table: "invoice_status_history",
                newName: "from_status");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "invoice_status_history",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "CorrelationId",
                table: "invoice_status_history",
                newName: "correlation_id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_payments",
                table: "payments",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_invoices",
                table: "invoices",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_invoice_status_history",
                table: "invoice_status_history",
                column: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_invoice_status_history_invoices_invoice_id",
                table: "invoice_status_history",
                column: "invoice_id",
                principalTable: "invoices",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_payments_invoices_invoice_id",
                table: "payments",
                column: "invoice_id",
                principalTable: "invoices",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_invoice_status_history_invoices_invoice_id",
                table: "invoice_status_history");

            migrationBuilder.DropForeignKey(
                name: "fk_payments_invoices_invoice_id",
                table: "payments");

            migrationBuilder.DropPrimaryKey(
                name: "pk_payments",
                table: "payments");

            migrationBuilder.DropPrimaryKey(
                name: "pk_invoices",
                table: "invoices");

            migrationBuilder.DropPrimaryKey(
                name: "pk_invoice_status_history",
                table: "invoice_status_history");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "payments",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "tx_hash",
                table: "payments",
                newName: "TxHash");

            migrationBuilder.RenameColumn(
                name: "received_at",
                table: "payments",
                newName: "ReceivedAt");

            migrationBuilder.RenameColumn(
                name: "invoice_id",
                table: "payments",
                newName: "InvoiceId");

            migrationBuilder.RenameColumn(
                name: "correlation_id",
                table: "payments",
                newName: "CorrelationId");

            migrationBuilder.RenameColumn(
                name: "amount_crypto",
                table: "payments",
                newName: "AmountCrypto");

            migrationBuilder.RenameIndex(
                name: "ix_payments_invoice_id",
                table: "payments",
                newName: "IX_payments_InvoiceId");

            migrationBuilder.RenameColumn(
                name: "status",
                table: "invoices",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "reference",
                table: "invoices",
                newName: "Reference");

            migrationBuilder.RenameColumn(
                name: "asset",
                table: "invoices",
                newName: "Asset");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "invoices",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "settled_at",
                table: "invoices",
                newName: "SettledAt");

            migrationBuilder.RenameColumn(
                name: "pay_to_address",
                table: "invoices",
                newName: "PayToAddress");

            migrationBuilder.RenameColumn(
                name: "merchant_id",
                table: "invoices",
                newName: "MerchantId");

            migrationBuilder.RenameColumn(
                name: "locked_rate",
                table: "invoices",
                newName: "LockedRate");

            migrationBuilder.RenameColumn(
                name: "expires_at",
                table: "invoices",
                newName: "ExpiresAt");

            migrationBuilder.RenameColumn(
                name: "crypto_amount",
                table: "invoices",
                newName: "CryptoAmount");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "invoices",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "amount_zar",
                table: "invoices",
                newName: "AmountZar");

            migrationBuilder.RenameColumn(
                name: "reason",
                table: "invoice_status_history",
                newName: "Reason");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "invoice_status_history",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "to_status",
                table: "invoice_status_history",
                newName: "ToStatus");

            migrationBuilder.RenameColumn(
                name: "invoice_id",
                table: "invoice_status_history",
                newName: "InvoiceId");

            migrationBuilder.RenameColumn(
                name: "from_status",
                table: "invoice_status_history",
                newName: "FromStatus");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "invoice_status_history",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "correlation_id",
                table: "invoice_status_history",
                newName: "CorrelationId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_payments",
                table: "payments",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_invoices",
                table: "invoices",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_invoice_status_history",
                table: "invoice_status_history",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_invoice_status_history_invoices_InvoiceId",
                table: "invoice_status_history",
                column: "InvoiceId",
                principalTable: "invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_payments_invoices_InvoiceId",
                table: "payments",
                column: "InvoiceId",
                principalTable: "invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
