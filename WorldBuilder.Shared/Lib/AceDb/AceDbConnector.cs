using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace WorldBuilder.Shared.Lib.AceDb {
    /// <summary>
    /// Thin wrapper around MySqlConnector for reading/writing the ACE
    /// ace_world.landblock_instance table.
    /// </summary>
    public class AceDbConnector : IDisposable {
        private readonly AceDbSettings _settings;

        public AceDbConnector(AceDbSettings settings) {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Tests the MySQL connection. Returns null on success or the error message on failure.
        /// </summary>
        public async Task<string?> TestConnectionAsync(CancellationToken ct = default) {
            try {
                await using var conn = new MySqlConnection(_settings.ConnectionString);
                await conn.OpenAsync(ct);
                return null;
            }
            catch (Exception ex) {
                return ex.Message;
            }
        }

        /// <summary>
        /// Queries all outdoor landblock_instance rows for the given landblock IDs.
        /// Outdoor cells have cell numbers 0x0001–0x0040 (1–64).
        /// Uses a single bulk query for large sets, batched queries for smaller ones.
        /// </summary>
        public async Task<List<LandblockInstanceRecord>> GetOutdoorInstancesAsync(
            IEnumerable<ushort> landblockIds, CancellationToken ct = default) {

            var lbSet = new HashSet<ushort>(landblockIds);
            var results = new List<LandblockInstanceRecord>();
            await using var conn = new MySqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);

            if (lbSet.Count > 500) {
                // For large sets, fetch all outdoor instances in one query and filter in memory
                const string sql = @"
                    SELECT `guid`, `weenie_Class_Id`, `obj_Cell_Id`,
                           `origin_X`, `origin_Y`, `origin_Z`
                    FROM `landblock_instance`
                    WHERE (`obj_Cell_Id` & 0xFFFF) BETWEEN 1 AND 64";

                await using var cmd = new MySqlCommand(sql, conn);
                cmd.CommandTimeout = 300;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) {
                    uint objCellId = reader.GetUInt32("obj_Cell_Id");
                    ushort lbId = (ushort)(objCellId >> 16);
                    if (!lbSet.Contains(lbId)) continue;

                    results.Add(new LandblockInstanceRecord {
                        Guid = reader.GetUInt32("guid"),
                        WeenieClassId = reader.GetUInt32("weenie_Class_Id"),
                        ObjCellId = objCellId,
                        OriginX = reader.GetFloat("origin_X"),
                        OriginY = reader.GetFloat("origin_Y"),
                        OriginZ = reader.GetFloat("origin_Z"),
                    });
                }
            }
            else {
                // For small sets, query per landblock
                foreach (var lbId in lbSet) {
                    uint lbIdShifted = (uint)lbId << 16;
                    uint minCellId = lbIdShifted | 0x0001;
                    uint maxCellId = lbIdShifted | 0x0040;

                    const string sql = @"
                        SELECT `guid`, `weenie_Class_Id`, `obj_Cell_Id`,
                               `origin_X`, `origin_Y`, `origin_Z`
                        FROM `landblock_instance`
                        WHERE `obj_Cell_Id` >= @minCell AND `obj_Cell_Id` <= @maxCell";

                    await using var cmd = new MySqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@minCell", minCellId);
                    cmd.Parameters.AddWithValue("@maxCell", maxCellId);

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) {
                        results.Add(new LandblockInstanceRecord {
                            Guid = reader.GetUInt32("guid"),
                            WeenieClassId = reader.GetUInt32("weenie_Class_Id"),
                            ObjCellId = reader.GetUInt32("obj_Cell_Id"),
                            OriginX = reader.GetFloat("origin_X"),
                            OriginY = reader.GetFloat("origin_Y"),
                            OriginZ = reader.GetFloat("origin_Z"),
                        });
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Executes a batch of SQL statements (the generated reposition script) against the database.
        /// </summary>
        public async Task<int> ExecuteSqlAsync(string sql, CancellationToken ct = default) {
            await using var conn = new MySqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand(sql, conn);
            return await cmd.ExecuteNonQueryAsync(ct);
        }

        public void Dispose() { }
    }
}
