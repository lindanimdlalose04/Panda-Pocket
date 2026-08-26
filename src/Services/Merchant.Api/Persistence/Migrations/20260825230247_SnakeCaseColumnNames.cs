using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PandaPocket.Services.Merchant.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SnakeCaseColumnNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_api_keys_merchants_MerchantId",
                table: "api_keys");

            migrationBuilder.DropForeignKey(
                name: "FK_users_merchants_MerchantId",
                table: "users");

            migrationBuilder.DropPrimaryKey(
                name: "PK_users",
                table: "users");

            migrationBuilder.DropPrimaryKey(
                name: "PK_merchants",
                table: "merchants");

            migrationBuilder.DropPrimaryKey(
                name: "PK_api_keys",
                table: "api_keys");

            migrationBuilder.RenameColumn(
                name: "Role",
                table: "users",
                newName: "role");

            migrationBuilder.RenameColumn(
                name: "Email",
                table: "users",
                newName: "email");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "users",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "PasswordHash",
                table: "users",
                newName: "password_hash");

            migrationBuilder.RenameColumn(
                name: "MerchantId",
                table: "users",
                newName: "merchant_id");

            migrationBuilder.RenameColumn(
                name: "LastLoginAt",
                table: "users",
                newName: "last_login_at");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "users",
                newName: "created_at");

            migrationBuilder.RenameIndex(
                name: "IX_users_MerchantId",
                table: "users",
                newName: "ix_users_merchant_id");

            migrationBuilder.RenameColumn(
                name: "Status",
                table: "merchants",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Email",
                table: "merchants",
                newName: "email");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "merchants",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "WebhookUrl",
                table: "merchants",
                newName: "webhook_url");

            migrationBuilder.RenameColumn(
                name: "WebhookSecret",
                table: "merchants",
                newName: "webhook_secret");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "merchants",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "FeePercent",
                table: "merchants",
                newName: "fee_percent");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "merchants",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "BusinessName",
                table: "merchants",
                newName: "business_name");

            migrationBuilder.RenameColumn(
                name: "Label",
                table: "api_keys",
                newName: "label");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "api_keys",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "RevokedAt",
                table: "api_keys",
                newName: "revoked_at");

            migrationBuilder.RenameColumn(
                name: "MerchantId",
                table: "api_keys",
                newName: "merchant_id");

            migrationBuilder.RenameColumn(
                name: "LastUsedAt",
                table: "api_keys",
                newName: "last_used_at");

            migrationBuilder.RenameColumn(
                name: "KeyPrefix",
                table: "api_keys",
                newName: "key_prefix");

            migrationBuilder.RenameColumn(
                name: "KeyHash",
                table: "api_keys",
                newName: "key_hash");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "api_keys",
                newName: "created_at");

            migrationBuilder.AddPrimaryKey(
                name: "pk_users",
                table: "users",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_merchants",
                table: "merchants",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_api_keys",
                table: "api_keys",
                column: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_api_keys_merchants_merchant_id",
                table: "api_keys",
                column: "merchant_id",
                principalTable: "merchants",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_users_merchants_merchant_id",
                table: "users",
                column: "merchant_id",
                principalTable: "merchants",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_api_keys_merchants_merchant_id",
                table: "api_keys");

            migrationBuilder.DropForeignKey(
                name: "fk_users_merchants_merchant_id",
                table: "users");

            migrationBuilder.DropPrimaryKey(
                name: "pk_users",
                table: "users");

            migrationBuilder.DropPrimaryKey(
                name: "pk_merchants",
                table: "merchants");

            migrationBuilder.DropPrimaryKey(
                name: "pk_api_keys",
                table: "api_keys");

            migrationBuilder.RenameColumn(
                name: "role",
                table: "users",
                newName: "Role");

            migrationBuilder.RenameColumn(
                name: "email",
                table: "users",
                newName: "Email");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "users",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "password_hash",
                table: "users",
                newName: "PasswordHash");

            migrationBuilder.RenameColumn(
                name: "merchant_id",
                table: "users",
                newName: "MerchantId");

            migrationBuilder.RenameColumn(
                name: "last_login_at",
                table: "users",
                newName: "LastLoginAt");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "users",
                newName: "CreatedAt");

            migrationBuilder.RenameIndex(
                name: "ix_users_merchant_id",
                table: "users",
                newName: "IX_users_MerchantId");

            migrationBuilder.RenameColumn(
                name: "status",
                table: "merchants",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "email",
                table: "merchants",
                newName: "Email");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "merchants",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "webhook_url",
                table: "merchants",
                newName: "WebhookUrl");

            migrationBuilder.RenameColumn(
                name: "webhook_secret",
                table: "merchants",
                newName: "WebhookSecret");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                table: "merchants",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "fee_percent",
                table: "merchants",
                newName: "FeePercent");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "merchants",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "business_name",
                table: "merchants",
                newName: "BusinessName");

            migrationBuilder.RenameColumn(
                name: "label",
                table: "api_keys",
                newName: "Label");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "api_keys",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "revoked_at",
                table: "api_keys",
                newName: "RevokedAt");

            migrationBuilder.RenameColumn(
                name: "merchant_id",
                table: "api_keys",
                newName: "MerchantId");

            migrationBuilder.RenameColumn(
                name: "last_used_at",
                table: "api_keys",
                newName: "LastUsedAt");

            migrationBuilder.RenameColumn(
                name: "key_prefix",
                table: "api_keys",
                newName: "KeyPrefix");

            migrationBuilder.RenameColumn(
                name: "key_hash",
                table: "api_keys",
                newName: "KeyHash");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "api_keys",
                newName: "CreatedAt");

            migrationBuilder.AddPrimaryKey(
                name: "PK_users",
                table: "users",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_merchants",
                table: "merchants",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_api_keys",
                table: "api_keys",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_api_keys_merchants_MerchantId",
                table: "api_keys",
                column: "MerchantId",
                principalTable: "merchants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_users_merchants_MerchantId",
                table: "users",
                column: "MerchantId",
                principalTable: "merchants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
