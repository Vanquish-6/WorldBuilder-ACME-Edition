using System.Data;

using MySqlConnector;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Shared.Lib.AceDb {
    /// <summary>
    /// Thin wrapper around MySqlConnector for reading/writing the ACE
    /// ace_world.landblock_instance table.
    /// </summary>
    public partial class AceDbConnector : IDisposable {
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
        /// Does not read rotation angles; use GetInstancesAsync for full placement data.
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
        /// Queries landblock_instance rows for a single landblock, optionally restricted by cell range.
        /// Use this to load all instances (outdoor + dungeon), or only dungeon cells (e.g. 0x0100–0xFFFE),
        /// so server operators can see and sync generators, items, and portals in dungeons.
        /// When includeAngles is true, reads angles_w/x/y/z for full placement round-trip.
        /// </summary>
        /// <param name="landblockId">Landblock ID (e.g. 0x01D9).</param>
        /// <param name="cellMin">Minimum cell number (inclusive). Null = 1 (outdoor start).</param>
        /// <param name="cellMax">Maximum cell number (inclusive). Null = 0xFFFE (all interiors).</param>
        /// <param name="includeAngles">When true, SELECT includes angles_w/x/y/z.</param>
        public async Task<List<LandblockInstanceRecord>> GetInstancesAsync(
            ushort landblockId,
            ushort? cellMin = null,
            ushort? cellMax = null,
            bool includeAngles = true,
            CancellationToken ct = default) {

            ushort cMin = cellMin ?? 1;
            ushort cMax = cellMax ?? 0xFFFE;
            uint lbIdShifted = (uint)landblockId << 16;
            uint minCellId = lbIdShifted | cMin;
            uint maxCellId = lbIdShifted | cMax;

            string cols = includeAngles
                ? "`guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_w`, `angles_x`, `angles_y`, `angles_z`"
                : "`guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`";

            string sql = $@"
                SELECT {cols}
                FROM `landblock_instance`
                WHERE `obj_Cell_Id` >= @minCell AND `obj_Cell_Id` <= @maxCell";

            var results = new List<LandblockInstanceRecord>();
            await using var conn = new MySqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@minCell", minCellId);
            cmd.Parameters.AddWithValue("@maxCell", maxCellId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                var rec = new LandblockInstanceRecord {
                    Guid = reader.GetUInt32("guid"),
                    WeenieClassId = reader.GetUInt32("weenie_Class_Id"),
                    ObjCellId = reader.GetUInt32("obj_Cell_Id"),
                    OriginX = reader.GetFloat("origin_X"),
                    OriginY = reader.GetFloat("origin_Y"),
                    OriginZ = reader.GetFloat("origin_Z"),
                };
                if (includeAngles && reader["angles_w"] != DBNull.Value) {
                    rec.AnglesW = reader.GetFloat("angles_w");
                    rec.AnglesX = reader.GetFloat("angles_x");
                    rec.AnglesY = reader.GetFloat("angles_y");
                    rec.AnglesZ = reader.GetFloat("angles_z");
                }
                results.Add(rec);
            }
            return results;
        }

        /// <summary>
        /// Generates a single INSERT statement for ace_world.landblock_instance.
        /// Use for placing generators/items/portals in dungeons. If Guid is 0, a new guid is generated.
        /// Angles default to identity quaternion (0, 0, 0, 1) when null.
        /// </summary>
        public static string GenerateInsertSql(LandblockInstanceRecord record, string databaseName = "ace_world") {
            uint guid = record.Guid;
            if (guid == 0)
                guid = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);

            float w = record.AnglesW ?? 0f;
            float x = record.AnglesX ?? 0f;
            float y = record.AnglesY ?? 0f;
            float z = record.AnglesZ ?? 1f;
            if (record.AnglesW == null && record.AnglesX == null && record.AnglesY == null && record.AnglesZ == null) {
                w = 1f; // identity quaternion (W=1, X=Y=Z=0)
                x = 0f;
                y = 0f;
                z = 0f;
            }

            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "INSERT INTO `{0}`.`landblock_instance` (`guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_w`, `angles_x`, `angles_y`, `angles_z`) VALUES ({1}, {2}, {3}, {4:F6}, {5:F6}, {6:F6}, {7:F6}, {8:F6}, {9:F6}, {10:F6});",
                databaseName, guid, record.WeenieClassId, record.ObjCellId,
                record.OriginX, record.OriginY, record.OriginZ,
                w, x, y, z);
        }

        /// <summary>
        /// Generates a batch of INSERT statements for landblock_instance (e.g. dungeon generator placements).
        /// </summary>
        public static string GenerateInsertSqlBatch(IEnumerable<LandblockInstanceRecord> records, string databaseName = "ace_world") {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("-- ACME WorldBuilder: landblock_instance (generators/items/portals)");
            sb.AppendLine($"-- Database: {databaseName}");
            sb.AppendLine();
            foreach (var r in records)
                sb.AppendLine(GenerateInsertSql(r, databaseName));
            return sb.ToString();
        }

        /// <summary>
        /// Converts dungeon instance placements to landblock_instance records for SQL generation.
        /// </summary>
        public static List<LandblockInstanceRecord> ToLandblockInstanceRecords(
            ushort landblockId,
            IEnumerable<DungeonInstancePlacement> placements) {
            var list = new List<LandblockInstanceRecord>();
            foreach (var p in placements) {
                uint objCellId = ((uint)landblockId << 16) | p.CellNumber;
                var q = p.Orientation;
                list.Add(new LandblockInstanceRecord {
                    Guid = 0,
                    WeenieClassId = p.WeenieClassId,
                    ObjCellId = objCellId,
                    OriginX = p.Origin.X,
                    OriginY = p.Origin.Y,
                    OriginZ = p.Origin.Z,
                    AnglesW = q.W,
                    AnglesX = q.X,
                    AnglesY = q.Y,
                    AnglesZ = q.Z,
                });
            }
            return list;
        }

        /// <summary>
        /// Converts outdoor instance placements to landblock_instance records for SQL generation.
        /// </summary>
        public static List<LandblockInstanceRecord> ToLandblockInstanceRecordsFromOutdoor(
            IEnumerable<OutdoorInstancePlacement> placements) {
            var list = new List<LandblockInstanceRecord>();
            foreach (var p in placements) {
                uint objCellId = ((uint)p.LandblockId << 16) | p.CellNumber;
                list.Add(new LandblockInstanceRecord {
                    Guid = 0,
                    WeenieClassId = p.WeenieClassId,
                    ObjCellId = objCellId,
                    OriginX = p.OriginX,
                    OriginY = p.OriginY,
                    OriginZ = p.OriginZ,
                    AnglesW = p.AnglesW,
                    AnglesX = p.AnglesX,
                    AnglesY = p.AnglesY,
                    AnglesZ = p.AnglesZ,
                });
            }
            return list;
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

        /// <summary>
        /// Weenie name lookup result for pickers (ID, display name, and optional Setup DID for 3D preview).
        /// </summary>
        public record WeenieEntry(uint ClassId, string Name, uint SetupId);

        /// <summary>
        /// Loads weenie class IDs, names, and setup DIDs from ace_world for picker/list UI.
        /// Name comes from weenie_properties_string type 1 (PropertyString.Name).
        /// Setup DID comes from weenie_properties_d_i_d type 1 (PropertyDataId.Setup).
        /// </summary>
        /// <param name="search">Optional filter: names containing this text (case-insensitive). Supports partial matching.</param>
        /// <param name="limit">Max results (default 500).</param>
        public async Task<List<WeenieEntry>> GetWeenieNamesAsync(string? search = null, int limit = 500, CancellationToken ct = default) {
            var results = new List<WeenieEntry>();
            try {
                await using var conn = new MySqlConnection(_settings.ConnectionString);
                await conn.OpenAsync(ct);

                string sql;
                if (string.IsNullOrWhiteSpace(search)) {
                    sql = @"
                        SELECT n.`object_Id`, n.`value` AS `name`,
                               COALESCE(d.`value`, 0) AS `setup_did`
                        FROM `weenie_properties_string` n
                        LEFT JOIN `weenie_properties_d_i_d` d
                            ON d.`object_Id` = n.`object_Id` AND d.`type` = 1
                        WHERE n.`type` = 1
                        ORDER BY n.`value`
                        LIMIT @limit";
                }
                else {
                    sql = @"
                        SELECT n.`object_Id`, n.`value` AS `name`,
                               COALESCE(d.`value`, 0) AS `setup_did`
                        FROM `weenie_properties_string` n
                        LEFT JOIN `weenie_properties_d_i_d` d
                            ON d.`object_Id` = n.`object_Id` AND d.`type` = 1
                        WHERE n.`type` = 1
                          AND n.`value` LIKE CONCAT('%', @search, '%')
                        ORDER BY n.`value`
                        LIMIT @limit";
                }

                await using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@limit", limit);
                if (!string.IsNullOrWhiteSpace(search))
                    cmd.Parameters.AddWithValue("@search", search.Trim());

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) {
                    results.Add(new WeenieEntry(
                        reader.GetUInt32("object_Id"),
                        reader.GetString("name"),
                        reader.IsDBNull(reader.GetOrdinal("setup_did")) ? 0 : reader.GetUInt32("setup_did")
                    ));
                }
            }
            catch (MySqlException) {
            }

            return results;
        }

        /// <summary>
        /// Batch lookup of Setup DIDs (PropertyDataId.Setup = type 1) for a set of weenie class IDs.
        /// Returns a dictionary mapping WCID -> Setup DID. WCIDs without a Setup are omitted.
        /// </summary>
        public async Task<Dictionary<uint, uint>> GetSetupDidsAsync(
            IEnumerable<uint> weenieClassIds, CancellationToken ct = default) {
            var result = new Dictionary<uint, uint>();
            var idList = new HashSet<uint>(weenieClassIds).ToList();
            if (idList.Count == 0) return result;

            try {
                await using var conn = new MySqlConnection(_settings.ConnectionString);
                await conn.OpenAsync(ct);

                for (int offset = 0; offset < idList.Count; offset += 500) {
                    var batch = idList.Skip(offset).Take(500).ToList();
                    var paramNames = string.Join(",", batch.Select((_, i) => $"@w{offset + i}"));
                    var sql = $@"SELECT `object_Id`, `value`
                                 FROM `weenie_properties_d_i_d`
                                 WHERE `type` = 1 AND `object_Id` IN ({paramNames})";

                    await using var cmd = new MySqlCommand(sql, conn);
                    for (int i = 0; i < batch.Count; i++)
                        cmd.Parameters.AddWithValue($"@w{offset + i}", batch[i]);

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        result.TryAdd(reader.GetUInt32("object_Id"), reader.GetUInt32("value"));
                }
            }
            catch (MySqlException) {
            }

            return result;
        }


        public async Task<SpellRecord?> GetSpellAsync(uint id, CancellationToken ct = default) {
            try {
                await using var conn = new MySqlConnection(_settings.ConnectionString);
                await conn.OpenAsync(ct);

                const string sql = "SELECT * FROM spell WHERE id = @id LIMIT 1";

                await using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", id);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    return null;

                // Helpers
                int? GetInt(string name) => reader.IsDBNull(name) ? null : reader.GetInt32(name);
                uint? GetUInt(string name) => reader.IsDBNull(name) ? null : reader.GetUInt32(name);
                float? GetFloat(string name) => reader.IsDBNull(name) ? null : reader.GetFloat(name);
                double? GetDouble(string name) => reader.IsDBNull(name) ? null : reader.GetDouble(name);
                bool? GetBool(string name) => reader.IsDBNull(name) ? null : reader.GetBoolean(name);

                return new SpellRecord {
                    Id = reader.GetUInt32("id"),
                    Name = reader.GetString("name"),

                    StatModType = GetUInt("stat_Mod_Type"),
                    StatModKey = GetUInt("stat_Mod_Key"),
                    StatModVal = GetFloat("stat_Mod_Val"),

                    EType = GetUInt("e_Type"),
                    BaseIntensity = GetInt("base_Intensity"),
                    Variance = GetInt("variance"),

                    Wcid = GetUInt("wcid"),

                    NumProjectiles = GetInt("num_Projectiles"),
                    NumProjectilesVariance = GetInt("num_Projectiles_Variance"),

                    SpreadAngle = GetFloat("spread_Angle"),
                    VerticalAngle = GetFloat("vertical_Angle"),
                    DefaultLaunchAngle = GetFloat("default_Launch_Angle"),

                    NonTracking = GetBool("non_Tracking"),

                    CreateOffsetOriginX = GetFloat("create_Offset_Origin_X"),
                    CreateOffsetOriginY = GetFloat("create_Offset_Origin_Y"),
                    CreateOffsetOriginZ = GetFloat("create_Offset_Origin_Z"),

                    PaddingOriginX = GetFloat("padding_Origin_X"),
                    PaddingOriginY = GetFloat("padding_Origin_Y"),
                    PaddingOriginZ = GetFloat("padding_Origin_Z"),

                    DimsOriginX = GetFloat("dims_Origin_X"),
                    DimsOriginY = GetFloat("dims_Origin_Y"),
                    DimsOriginZ = GetFloat("dims_Origin_Z"),

                    PeturbationOriginX = GetFloat("peturbation_Origin_X"),
                    PeturbationOriginY = GetFloat("peturbation_Origin_Y"),
                    PeturbationOriginZ = GetFloat("peturbation_Origin_Z"),

                    ImbuedEffect = GetUInt("imbued_Effect"),

                    SlayerCreatureType = GetInt("slayer_Creature_Type"),
                    SlayerDamageBonus = GetFloat("slayer_Damage_Bonus"),

                    CritFreq = GetDouble("crit_Freq"),
                    CritMultiplier = GetDouble("crit_Multiplier"),

                    IgnoreMagicResist = GetInt("ignore_Magic_Resist"),
                    ElementalModifier = GetDouble("elemental_Modifier"),

                    DrainPercentage = GetFloat("drain_Percentage"),
                    DamageRatio = GetFloat("damage_Ratio"),

                    DamageType = GetInt("damage_Type"),

                    Boost = GetInt("boost"),
                    BoostVariance = GetInt("boost_Variance"),

                    Source = GetInt("source"),
                    Destination = GetInt("destination"),

                    Proportion = GetFloat("proportion"),
                    LossPercent = GetFloat("loss_Percent"),

                    SourceLoss = GetInt("source_Loss"),
                    TransferCap = GetInt("transfer_Cap"),
                    MaxBoostAllowed = GetInt("max_Boost_Allowed"),

                    TransferBitfield = GetUInt("transfer_Bitfield"),

                    Index = GetInt("index"),
                    Link = GetInt("link"),

                    PositionObjCellId = GetUInt("position_Obj_Cell_ID"),

                    PositionOriginX = GetFloat("position_Origin_X"),
                    PositionOriginY = GetFloat("position_Origin_Y"),
                    PositionOriginZ = GetFloat("position_Origin_Z"),

                    PositionAnglesW = GetFloat("position_Angles_W"),
                    PositionAnglesX = GetFloat("position_Angles_X"),
                    PositionAnglesY = GetFloat("position_Angles_Y"),
                    PositionAnglesZ = GetFloat("position_Angles_Z"),

                    MinPower = GetInt("min_Power"),
                    MaxPower = GetInt("max_Power"),
                    PowerVariance = GetFloat("power_Variance"),

                    DispelSchool = GetInt("dispel_School"),

                    Align = GetInt("align"),
                    Number = GetInt("number"),
                    NumberVariance = GetFloat("number_Variance"),

                    DotDuration = GetDouble("dot_Duration")
                };
            }
            catch (MySqlException) {
                return null;
            }
        }

        public async Task<bool> SaveSpellAsync(SpellRecord spell, CancellationToken ct = default) {
            try {
                await using var conn = new MySqlConnection(_settings.ConnectionString);
                await conn.OpenAsync(ct);

                const string sql = @"
                    INSERT INTO spell (
                        id, name,
                        stat_Mod_Type, stat_Mod_Key, stat_Mod_Val,
                        e_Type, base_Intensity, variance,
                        wcid,
                        num_Projectiles, num_Projectiles_Variance,
                        spread_Angle, vertical_Angle, default_Launch_Angle,
                        non_Tracking,
                        create_Offset_Origin_X, create_Offset_Origin_Y, create_Offset_Origin_Z,
                        padding_Origin_X, padding_Origin_Y, padding_Origin_Z,
                        dims_Origin_X, dims_Origin_Y, dims_Origin_Z,
                        peturbation_Origin_X, peturbation_Origin_Y, peturbation_Origin_Z,
                        imbued_Effect,
                        slayer_Creature_Type, slayer_Damage_Bonus,
                        crit_Freq, crit_Multiplier,
                        ignore_Magic_Resist, elemental_Modifier,
                        drain_Percentage, damage_Ratio,
                        damage_Type,
                        boost, boost_Variance,
                        source, destination,
                        proportion, loss_Percent,
                        source_Loss, transfer_Cap, max_Boost_Allowed,
                        transfer_Bitfield,
                        `index`, link,
                        position_Obj_Cell_ID,
                        position_Origin_X, position_Origin_Y, position_Origin_Z,
                        position_Angles_W, position_Angles_X, position_Angles_Y, position_Angles_Z,
                        min_Power, max_Power, power_Variance,
                        dispel_School,
                        align, number, number_Variance,
                        dot_Duration
                    )
                    VALUES (
                        @id, @name,
                        @statModType, @statModKey, @statModVal,
                        @eType, @baseIntensity, @variance,
                        @wcid,
                        @numProjectiles, @numProjectilesVariance,
                        @spreadAngle, @verticalAngle, @defaultLaunchAngle,
                        @nonTracking,
                        @createOffsetOriginX, @createOffsetOriginY, @createOffsetOriginZ,
                        @paddingOriginX, @paddingOriginY, @paddingOriginZ,
                        @dimsOriginX, @dimsOriginY, @dimsOriginZ,
                        @peturbationOriginX, @peturbationOriginY, @peturbationOriginZ,
                        @imbuedEffect,
                        @slayerCreatureType, @slayerDamageBonus,
                        @critFreq, @critMultiplier,
                        @ignoreMagicResist, @elementalModifier,
                        @drainPercentage, @damageRatio,
                        @damageType,
                        @boost, @boostVariance,
                        @source, @destination,
                        @proportion, @lossPercent,
                        @sourceLoss, @transferCap, @maxBoostAllowed,
                        @transferBitfield,
                        @index, @link,
                        @positionObjCellId,
                        @positionOriginX, @positionOriginY, @positionOriginZ,
                        @positionAnglesW, @positionAnglesX, @positionAnglesY, @positionAnglesZ,
                        @minPower, @maxPower, @powerVariance,
                        @dispelSchool,
                        @align, @number, @numberVariance,
                        @dotDuration
                    )
                    ON DUPLICATE KEY UPDATE
                        name = VALUES(name),
                        stat_Mod_Type = VALUES(stat_Mod_Type),
                        stat_Mod_Key = VALUES(stat_Mod_Key),
                        stat_Mod_Val = VALUES(stat_Mod_Val),
                        e_Type = VALUES(e_Type),
                        base_Intensity = VALUES(base_Intensity),
                        variance = VALUES(variance),
                        wcid = VALUES(wcid),
                        num_Projectiles = VALUES(num_Projectiles),
                        num_Projectiles_Variance = VALUES(num_Projectiles_Variance),
                        spread_Angle = VALUES(spread_Angle),
                        vertical_Angle = VALUES(vertical_Angle),
                        default_Launch_Angle = VALUES(default_Launch_Angle),
                        non_Tracking = VALUES(non_Tracking),
                        create_Offset_Origin_X = VALUES(create_Offset_Origin_X),
                        create_Offset_Origin_Y = VALUES(create_Offset_Origin_Y),
                        create_Offset_Origin_Z = VALUES(create_Offset_Origin_Z),
                        padding_Origin_X = VALUES(padding_Origin_X),
                        padding_Origin_Y = VALUES(padding_Origin_Y),
                        padding_Origin_Z = VALUES(padding_Origin_Z),
                        dims_Origin_X = VALUES(dims_Origin_X),
                        dims_Origin_Y = VALUES(dims_Origin_Y),
                        dims_Origin_Z = VALUES(dims_Origin_Z),
                        peturbation_Origin_X = VALUES(peturbation_Origin_X),
                        peturbation_Origin_Y = VALUES(peturbation_Origin_Y),
                        peturbation_Origin_Z = VALUES(peturbation_Origin_Z),
                        imbued_Effect = VALUES(imbued_Effect),
                        slayer_Creature_Type = VALUES(slayer_Creature_Type),
                        slayer_Damage_Bonus = VALUES(slayer_Damage_Bonus),
                        crit_Freq = VALUES(crit_Freq),
                        crit_Multiplier = VALUES(crit_Multiplier),
                        ignore_Magic_Resist = VALUES(ignore_Magic_Resist),
                        elemental_Modifier = VALUES(elemental_Modifier),
                        drain_Percentage = VALUES(drain_Percentage),
                        damage_Ratio = VALUES(damage_Ratio),
                        damage_Type = VALUES(damage_Type),
                        boost = VALUES(boost),
                        boost_Variance = VALUES(boost_Variance),
                        source = VALUES(source),
                        destination = VALUES(destination),
                        proportion = VALUES(proportion),
                        loss_Percent = VALUES(loss_Percent),
                        source_Loss = VALUES(source_Loss),
                        transfer_Cap = VALUES(transfer_Cap),
                        max_Boost_Allowed = VALUES(max_Boost_Allowed),
                        transfer_Bitfield = VALUES(transfer_Bitfield),
                        `index` = VALUES(`index`),
                        link = VALUES(link),
                        position_Obj_Cell_ID = VALUES(position_Obj_Cell_ID),
                        position_Origin_X = VALUES(position_Origin_X),
                        position_Origin_Y = VALUES(position_Origin_Y),
                        position_Origin_Z = VALUES(position_Origin_Z),
                        position_Angles_W = VALUES(position_Angles_W),
                        position_Angles_X = VALUES(position_Angles_X),
                        position_Angles_Y = VALUES(position_Angles_Y),
                        position_Angles_Z = VALUES(position_Angles_Z),
                        min_Power = VALUES(min_Power),
                        max_Power = VALUES(max_Power),
                        power_Variance = VALUES(power_Variance),
                        dispel_School = VALUES(dispel_School),
                        align = VALUES(align),
                        number = VALUES(number),
                        number_Variance = VALUES(number_Variance),
                        dot_Duration = VALUES(dot_Duration);";

                await using var cmd = new MySqlCommand(sql, conn);

                cmd.Parameters.AddWithValue("@id", spell.Id);
                cmd.Parameters.AddWithValue("@name", spell.Name ?? "");

                object Db(object? v) => v ?? DBNull.Value;

                cmd.Parameters.AddWithValue("@statModType", Db(spell.StatModType));
                cmd.Parameters.AddWithValue("@statModKey", Db(spell.StatModKey));
                cmd.Parameters.AddWithValue("@statModVal", Db(spell.StatModVal));

                cmd.Parameters.AddWithValue("@eType", Db(spell.EType));
                cmd.Parameters.AddWithValue("@baseIntensity", Db(spell.BaseIntensity));
                cmd.Parameters.AddWithValue("@variance", Db(spell.Variance));

                cmd.Parameters.AddWithValue("@wcid", Db(spell.Wcid));

                cmd.Parameters.AddWithValue("@numProjectiles", Db(spell.NumProjectiles));
                cmd.Parameters.AddWithValue("@numProjectilesVariance", Db(spell.NumProjectilesVariance));

                cmd.Parameters.AddWithValue("@spreadAngle", Db(spell.SpreadAngle));
                cmd.Parameters.AddWithValue("@verticalAngle", Db(spell.VerticalAngle));
                cmd.Parameters.AddWithValue("@defaultLaunchAngle", Db(spell.DefaultLaunchAngle));

                cmd.Parameters.AddWithValue("@nonTracking", Db(spell.NonTracking));

                cmd.Parameters.AddWithValue("@createOffsetOriginX", Db(spell.CreateOffsetOriginX));
                cmd.Parameters.AddWithValue("@createOffsetOriginY", Db(spell.CreateOffsetOriginY));
                cmd.Parameters.AddWithValue("@createOffsetOriginZ", Db(spell.CreateOffsetOriginZ));

                cmd.Parameters.AddWithValue("@paddingOriginX", Db(spell.PaddingOriginX));
                cmd.Parameters.AddWithValue("@paddingOriginY", Db(spell.PaddingOriginY));
                cmd.Parameters.AddWithValue("@paddingOriginZ", Db(spell.PaddingOriginZ));

                cmd.Parameters.AddWithValue("@dimsOriginX", Db(spell.DimsOriginX));
                cmd.Parameters.AddWithValue("@dimsOriginY", Db(spell.DimsOriginY));
                cmd.Parameters.AddWithValue("@dimsOriginZ", Db(spell.DimsOriginZ));

                cmd.Parameters.AddWithValue("@peturbationOriginX", Db(spell.PeturbationOriginX));
                cmd.Parameters.AddWithValue("@peturbationOriginY", Db(spell.PeturbationOriginY));
                cmd.Parameters.AddWithValue("@peturbationOriginZ", Db(spell.PeturbationOriginZ));

                cmd.Parameters.AddWithValue("@imbuedEffect", Db(spell.ImbuedEffect));

                cmd.Parameters.AddWithValue("@slayerCreatureType", Db(spell.SlayerCreatureType));
                cmd.Parameters.AddWithValue("@slayerDamageBonus", Db(spell.SlayerDamageBonus));

                cmd.Parameters.AddWithValue("@critFreq", Db(spell.CritFreq));
                cmd.Parameters.AddWithValue("@critMultiplier", Db(spell.CritMultiplier));

                cmd.Parameters.AddWithValue("@ignoreMagicResist", Db(spell.IgnoreMagicResist));
                cmd.Parameters.AddWithValue("@elementalModifier", Db(spell.ElementalModifier));

                cmd.Parameters.AddWithValue("@drainPercentage", Db(spell.DrainPercentage));
                cmd.Parameters.AddWithValue("@damageRatio", Db(spell.DamageRatio));

                cmd.Parameters.AddWithValue("@damageType", Db(spell.DamageType));

                cmd.Parameters.AddWithValue("@boost", Db(spell.Boost));
                cmd.Parameters.AddWithValue("@boostVariance", Db(spell.BoostVariance));

                cmd.Parameters.AddWithValue("@source", Db(spell.Source));
                cmd.Parameters.AddWithValue("@destination", Db(spell.Destination));

                cmd.Parameters.AddWithValue("@proportion", Db(spell.Proportion));
                cmd.Parameters.AddWithValue("@lossPercent", Db(spell.LossPercent));

                cmd.Parameters.AddWithValue("@sourceLoss", Db(spell.SourceLoss));
                cmd.Parameters.AddWithValue("@transferCap", Db(spell.TransferCap));
                cmd.Parameters.AddWithValue("@maxBoostAllowed", Db(spell.MaxBoostAllowed));

                cmd.Parameters.AddWithValue("@transferBitfield", Db(spell.TransferBitfield));

                cmd.Parameters.AddWithValue("@index", Db(spell.Index));
                cmd.Parameters.AddWithValue("@link", Db(spell.Link));

                cmd.Parameters.AddWithValue("@positionObjCellId", Db(spell.PositionObjCellId));

                cmd.Parameters.AddWithValue("@positionOriginX", Db(spell.PositionOriginX));
                cmd.Parameters.AddWithValue("@positionOriginY", Db(spell.PositionOriginY));
                cmd.Parameters.AddWithValue("@positionOriginZ", Db(spell.PositionOriginZ));

                cmd.Parameters.AddWithValue("@positionAnglesW", Db(spell.PositionAnglesW));
                cmd.Parameters.AddWithValue("@positionAnglesX", Db(spell.PositionAnglesX));
                cmd.Parameters.AddWithValue("@positionAnglesY", Db(spell.PositionAnglesY));
                cmd.Parameters.AddWithValue("@positionAnglesZ", Db(spell.PositionAnglesZ));

                cmd.Parameters.AddWithValue("@minPower", Db(spell.MinPower));
                cmd.Parameters.AddWithValue("@maxPower", Db(spell.MaxPower));
                cmd.Parameters.AddWithValue("@powerVariance", Db(spell.PowerVariance));

                cmd.Parameters.AddWithValue("@dispelSchool", Db(spell.DispelSchool));

                cmd.Parameters.AddWithValue("@align", Db(spell.Align));
                cmd.Parameters.AddWithValue("@number", Db(spell.Number));
                cmd.Parameters.AddWithValue("@numberVariance", Db(spell.NumberVariance));

                cmd.Parameters.AddWithValue("@dotDuration", Db(spell.DotDuration));

                return await cmd.ExecuteNonQueryAsync(ct) > 0;
            }
            catch (MySqlException) {
                return false;
            }
        }

        public void Dispose() { }
    }
}
