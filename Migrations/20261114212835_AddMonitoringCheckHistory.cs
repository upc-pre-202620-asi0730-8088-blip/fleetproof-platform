using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FleetProof.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoringCheckHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_vehicle_monitorings_user_id_id",
                table: "vehicle_monitorings",
                columns: new[] { "user_id", "id" });

            migrationBuilder.CreateTable(
                name: "monitoring_checks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monitoring_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitoring_checks", x => x.id);
                    table.ForeignKey(
                        name: "FK_monitoring_checks_vehicle_monitorings_user_id_monitoring_id",
                        columns: x => new { x.user_id, x.monitoring_id },
                        principalTable: "vehicle_monitorings",
                        principalColumns: new[] { "user_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_checks_monitoring_id_checked_at",
                table: "monitoring_checks",
                columns: new[] { "monitoring_id", "checked_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_checks_user_id_monitoring_id",
                table: "monitoring_checks",
                columns: new[] { "user_id", "monitoring_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "monitoring_checks");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_vehicle_monitorings_user_id_id",
                table: "vehicle_monitorings");
        }
    }
}
