using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PandaPocket.Services.Soc.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlertsAndDetectionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    rule_id = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    rule_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    recommended_action = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    affected_service = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    affected_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    affected_entity = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    source_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    suppression_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alerts", x => x.alert_id);
                });

            migrationBuilder.CreateTable(
                name: "detection_rules",
                columns: table => new
                {
                    rule_id = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    purpose = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    input_event_types = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    condition = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    alert_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    recommended_action = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    threat_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    window_minutes = table.Column<int>(type: "integer", nullable: false),
                    threshold = table.Column<int>(type: "integer", nullable: false),
                    suppression_minutes = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_detection_rules", x => x.rule_id);
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incidents", x => x.incident_id);
                });

            migrationBuilder.CreateTable(
                name: "alert_events",
                columns: table => new
                {
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_events", x => new { x.alert_id, x.event_id });
                    table.ForeignKey(
                        name: "fk_alert_events_alerts_alert_id",
                        column: x => x.alert_id,
                        principalTable: "alerts",
                        principalColumn: "alert_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_events_event",
                table: "alert_events",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_last_seen",
                table: "alerts",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_suppression",
                table: "alerts",
                columns: new[] { "suppression_key", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_events");

            migrationBuilder.DropTable(
                name: "detection_rules");

            migrationBuilder.DropTable(
                name: "incidents");

            migrationBuilder.DropTable(
                name: "alerts");
        }
    }
}
