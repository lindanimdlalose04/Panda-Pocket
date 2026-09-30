using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PandaPocket.Services.Soc.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSocSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "soc_events",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    service_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    endpoint = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    http_method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    affected_entity = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_soc_events", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_soc_events_correlation",
                table: "soc_events",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_soc_events_source_time",
                table: "soc_events",
                columns: new[] { "source_ip", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_soc_events_type_time",
                table: "soc_events",
                columns: new[] { "event_type", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_soc_events_user_time",
                table: "soc_events",
                columns: new[] { "user_id", "timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "soc_events");
        }
    }
}
